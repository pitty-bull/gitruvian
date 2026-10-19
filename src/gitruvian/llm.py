"""Provedores de modelo. Todos são opcionais: sem modelo, o app segue útil (modo manual)."""

from __future__ import annotations

import json
import os
import shlex
import subprocess
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Protocol

from gitruvian.config import Settings

SYSTEM_PROMPT = """You are a careful software engineer working inside a local git repository.
Propose the smallest correct change that fulfils the user's request.

Reply with exactly:
1. A first line `COMMIT: <short imperative commit message>`.
2. One fenced code block tagged `diff` containing a unified diff in `git diff` format:
   paths prefixed with a/ and b/, relative to the repository root, 3 lines of unchanged
   context copied exactly from the files shown, correct @@ hunk headers.

Do not add explanations outside those two items. Only modify files shown in the context,
unless a new file is strictly necessary. Never touch .env files, credentials, the .git or
.gitruvian directories. If the request cannot be done safely, reply with
`COMMIT: none` and no diff."""

# Modelos que aceitam o fallback automático do lado do servidor em caso de recusa.
ANTHROPIC_FALLBACK_MODELS = frozenset({"claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"})


class ModelError(RuntimeError):
    """O modelo respondeu com erro ou de forma inutilizável."""


class ModelUnavailable(ModelError):
    """Nenhum modelo utilizável está configurado (modo degradado)."""


@dataclass
class ModelReply:
    text: str
    model: str
    latency_ms: int


class Provider(Protocol):
    name: str
    model: str

    def complete(self, system: str, user: str) -> ModelReply: ...


def build_user_prompt(instruction: str, context_text: str) -> str:
    return (
        f"Request from the user:\n{instruction}\n\n"
        f"Repository context (may be partial):\n{context_text}\n\n"
        "Return the COMMIT line and the diff block now."
    )


# --------------------------------------------------------------------- Anthropic
def anthropic_request_kwargs(model: str, system: str, user: str) -> dict[str, Any]:
    kwargs: dict[str, Any] = {
        "model": model,
        "max_tokens": 16000,
        "system": system,
        "messages": [{"role": "user", "content": user}],
    }
    if model in ANTHROPIC_FALLBACK_MODELS:
        kwargs["betas"] = ["server-side-fallback-2026-07-01"]
        kwargs["fallbacks"] = "default"
    return kwargs


class AnthropicProvider:
    name = "anthropic"

    def __init__(self, settings: Settings):
        self.model = settings.model
        self._settings = settings

    def complete(self, system: str, user: str) -> ModelReply:
        try:
            import anthropic
        except ImportError as exc:
            raise ModelUnavailable(
                "o SDK 'anthropic' não está instalado. Instale com: pip install \"gitruvian[anthropic]\""
            ) from exc
        if not self._settings.anthropic_api_key:
            raise ModelUnavailable("ANTHROPIC_API_KEY não definida no .env")
        client = anthropic.Anthropic(
            api_key=self._settings.anthropic_api_key, timeout=float(self._settings.timeout), max_retries=2
        )
        started = time.monotonic()
        try:
            response = client.beta.messages.create(**anthropic_request_kwargs(self.model, system, user))
        except anthropic.AuthenticationError as exc:
            raise ModelError("chave da Anthropic rejeitada (401). Confira ANTHROPIC_API_KEY.") from exc
        except anthropic.NotFoundError as exc:
            raise ModelError(f"modelo '{self.model}' não encontrado (404). Confira GITRUVIAN_MODEL.") from exc
        except anthropic.RateLimitError as exc:
            raise ModelError("limite de uso atingido (429). Tente novamente em instantes.") from exc
        except anthropic.APIStatusError as exc:
            raise ModelError(f"a API da Anthropic respondeu com erro {exc.status_code}.") from exc
        except anthropic.APIConnectionError as exc:
            raise ModelUnavailable("sem conexão com a API da Anthropic.") from exc
        latency = int((time.monotonic() - started) * 1000)
        if response.stop_reason == "refusal":
            raise ModelError("o modelo recusou o pedido. Reformule a solicitação.")
        text = "".join(block.text for block in response.content if getattr(block, "type", "") == "text")
        if response.stop_reason == "max_tokens":
            text += "\n"  # o diff provavelmente foi cortado; a validação vai acusar
        return ModelReply(text=text, model=getattr(response, "model", self.model), latency_ms=latency)


# ------------------------------------------------------- OpenAI-compatível local
class OpenAICompatProvider:
    """Qualquer servidor com /v1/chat/completions (Ollama, LM Studio, llama.cpp, vLLM...)."""

    name = "openai_compat"

    def __init__(self, settings: Settings):
        if not settings.model:
            raise ModelUnavailable("defina GITRUVIAN_MODEL (ex.: qwen2.5-coder:7b no Ollama)")
        self.model = settings.model
        self._settings = settings

    def complete(self, system: str, user: str) -> ModelReply:
        body = json.dumps(
            {
                "model": self.model,
                "temperature": 0.1,
                "stream": False,
                "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            }
        ).encode("utf-8")
        headers = {"Content-Type": "application/json"}
        if self._settings.openai_api_key:
            headers["Authorization"] = f"Bearer {self._settings.openai_api_key}"
        url = f"{self._settings.openai_base_url}/chat/completions"
        request = urllib.request.Request(url, data=body, headers=headers, method="POST")
        started = time.monotonic()
        try:
            with urllib.request.urlopen(request, timeout=self._settings.timeout) as resp:
                payload = json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            raise ModelError(f"o servidor de modelo respondeu HTTP {exc.code} em {url}") from exc
        except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
            raise ModelUnavailable(
                f"não foi possível conectar em {self._settings.openai_base_url} "
                "(o servidor local está rodando? ex.: 'ollama serve')"
            ) from exc
        except json.JSONDecodeError as exc:
            raise ModelError("o servidor de modelo devolveu JSON inválido") from exc
        try:
            text = payload["choices"][0]["message"]["content"] or ""
        except (KeyError, IndexError, TypeError) as exc:
            raise ModelError("resposta do servidor sem 'choices[0].message.content'") from exc
        return ModelReply(
            text=text, model=str(payload.get("model") or self.model), latency_ms=int((time.monotonic() - started) * 1000)
        )


# ------------------------------------------------------------ Comando local (CLI)
def split_command(command: str) -> list[str]:
    if os.name == "nt":
        parts = shlex.split(command, posix=False)
        return [p[1:-1] if len(p) >= 2 and p[0] == p[-1] == '"' else p for p in parts]
    return shlex.split(command)


class CommandProvider:
    """Executa um programa local que lê o prompt no stdin e escreve a resposta no stdout."""

    name = "command"

    def __init__(self, settings: Settings):
        if not settings.model_command:
            raise ModelUnavailable("defina GITRUVIAN_MODEL_COMMAND (ex.: ollama run qwen2.5-coder:7b)")
        self.command = settings.model_command
        self.model = settings.model or split_command(settings.model_command)[0]
        self._timeout = settings.timeout

    def complete(self, system: str, user: str) -> ModelReply:
        argv = split_command(self.command)
        started = time.monotonic()
        try:
            proc = subprocess.run(
                argv,
                input=f"{system}\n\n{user}".encode("utf-8"),
                capture_output=True,
                timeout=self._timeout,
            )
        except FileNotFoundError as exc:
            raise ModelUnavailable(f"comando não encontrado: {argv[0]}") from exc
        except subprocess.TimeoutExpired as exc:
            raise ModelError(f"o comando do modelo excedeu {self._timeout}s") from exc
        if proc.returncode != 0:
            raise ModelError(f"o comando do modelo terminou com código {proc.returncode}")
        return ModelReply(
            text=proc.stdout.decode("utf-8", errors="replace"),
            model=self.model,
            latency_ms=int((time.monotonic() - started) * 1000),
        )


# ---------------------------------------------------------------------- seleção
DEGRADED_EXPLANATION = (
    "Nenhum modelo configurado (GITRUVIAN_PROVIDER=none). O Gitruvian continua útil: "
    "indexa e busca no código, valida e aplica diffs que você traz (--patch-file), roda testes, "
    "commita e registra tudo. Para gerar diffs automaticamente, configure um provedor no .env."
)


def get_provider(settings: Settings) -> Provider:
    if settings.provider == "none":
        raise ModelUnavailable(DEGRADED_EXPLANATION)
    if settings.is_remote and not settings.allow_remote:
        raise ModelUnavailable(
            "este provedor envia trechos do seu código para fora da máquina. "
            "Para permitir, defina GITRUVIAN_ALLOW_REMOTE=1 no .env."
        )
    if settings.provider == "anthropic":
        return AnthropicProvider(settings)
    if settings.provider == "openai_compat":
        return OpenAICompatProvider(settings)
    if settings.provider == "command":
        return CommandProvider(settings)
    raise ModelUnavailable(f"provedor desconhecido: {settings.provider}")


def describe_provider(settings: Settings) -> tuple[str, str]:
    """Resumo legível do modo atual: (rótulo, explicação). Nunca inclui segredos."""
    if settings.provider == "none":
        return "manual (modo degradado)", DEGRADED_EXPLANATION
    where = "remoto" if settings.is_remote else "local"
    label = f"{settings.provider} · {settings.model or '(modelo não definido)'} · {where}"
    try:
        get_provider(settings)
    except ModelUnavailable as exc:
        return label + " · indisponível", str(exc)
    if settings.provider == "anthropic":
        try:
            import anthropic  # noqa: F401
        except ImportError:
            return label + " · indisponível", 'SDK ausente: pip install "gitruvian[anthropic]"'
        key = "presente" if settings.anthropic_api_key else "ausente"
        return label, f"Chave ANTHROPIC_API_KEY: {key}. Trechos do código são enviados à API da Anthropic."
    if settings.provider == "openai_compat":
        return label, f"Endpoint: {settings.openai_base_url}"
    return label, "Comando local configurado em GITRUVIAN_MODEL_COMMAND."
