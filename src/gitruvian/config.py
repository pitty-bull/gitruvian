"""Configuração vinda do ambiente e de arquivos .env (nunca do banco)."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import urlparse

PROVIDERS = ("none", "anthropic", "openai_compat", "command")

# Só estas chaves são lidas de arquivos .env; o resto é ignorado.
KNOWN_KEYS = frozenset(
    {
        "GITRUVIAN_PROVIDER",
        "GITRUVIAN_MODEL",
        "GITRUVIAN_ALLOW_REMOTE",
        "GITRUVIAN_TIMEOUT",
        "GITRUVIAN_MAX_CONTEXT_CHARS",
        "GITRUVIAN_OPENAI_BASE_URL",
        "GITRUVIAN_OPENAI_API_KEY",
        "GITRUVIAN_MODEL_COMMAND",
        "GITRUVIAN_TEST_COMMAND",
        "GITRUVIAN_TEST_TIMEOUT",
        "GITRUVIAN_DATA_DIR",
        "ANTHROPIC_API_KEY",
    }
)

DEFAULT_ANTHROPIC_MODEL = "claude-opus-5-5"
DEFAULT_OPENAI_BASE_URL = "http://localhost:11434/v1"
LOCAL_HOSTS = frozenset({"localhost", "127.0.0.1", "::1", "[::1]"})


class ConfigError(ValueError):
    """Configuração inválida no ambiente ou no .env."""


def parse_env_file(path: Path) -> dict[str, str]:
    """Lê um arquivo .env simples (CHAVE=valor). Ignora chaves desconhecidas."""
    values: dict[str, str] = {}
    try:
        raw = path.read_text(encoding="utf-8-sig")
    except (OSError, UnicodeDecodeError):
        return values
    for line in raw.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        if line.startswith("export "):
            line = line[len("export ") :]
        key, _, value = line.partition("=")
        key = key.strip()
        if key not in KNOWN_KEYS:
            continue
        value = value.strip()
        if len(value) >= 2 and value[0] in "'\"" and value.endswith(value[0]) and value.count(value[0]) == 2:
            value = value[1:-1]
        elif " #" in value:
            value = value.split(" #", 1)[0].rstrip()
        values[key] = value
    return values


def user_config_dir() -> Path:
    if os.name == "nt":
        base = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
        return Path(base) / "gitruvian"
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "gitruvian"


def env_file_candidates(cwd: Path, environ: dict[str, str]) -> list[Path]:
    """Ordem de precedência dos arquivos .env (o primeiro que define a chave vence).

    Se GITRUVIAN_ENV_FILE estiver definido, somente esse arquivo é lido.
    """
    explicit = environ.get("GITRUVIAN_ENV_FILE")
    if explicit:
        return [Path(explicit)]
    return [cwd / ".env", user_config_dir() / ".env"]


def load_values(cwd: Path | None = None, environ: dict[str, str] | None = None) -> tuple[dict[str, str], list[Path]]:
    """Mescla variáveis de ambiente com arquivos .env sem alterar os.environ."""
    environ = dict(os.environ if environ is None else environ)
    cwd = cwd or Path.cwd()
    merged: dict[str, str] = {}
    used: list[Path] = []
    for candidate in reversed(env_file_candidates(cwd, environ)):
        if candidate.is_file():
            parsed = parse_env_file(candidate)
            if parsed:
                used.append(candidate)
            merged.update(parsed)
    for key in KNOWN_KEYS:
        if environ.get(key):
            merged[key] = environ[key]
    return merged, list(reversed(used))


def _int(values: dict[str, str], key: str, default: int, minimum: int, maximum: int) -> int:
    raw = values.get(key, "").strip()
    if not raw:
        return default
    try:
        number = int(raw)
    except ValueError as exc:
        raise ConfigError(f"{key} deve ser um número inteiro") from exc
    if not minimum <= number <= maximum:
        raise ConfigError(f"{key} deve estar entre {minimum} e {maximum}")
    return number


def _bool(values: dict[str, str], key: str) -> bool:
    return values.get(key, "").strip().lower() in {"1", "true", "yes", "sim", "on"}


@dataclass(frozen=True)
class Settings:
    provider: str = "none"
    model: str = ""
    allow_remote: bool = False
    timeout: int = 120
    max_context_chars: int = 24000
    openai_base_url: str = DEFAULT_OPENAI_BASE_URL
    model_command: str = ""
    test_command: str = ""
    test_timeout: int = 600
    data_dir: str = ""
    env_files: tuple[str, ...] = ()
    # Segredos nunca aparecem em repr() nem em logs.
    anthropic_api_key: str = field(default="", repr=False)
    openai_api_key: str = field(default="", repr=False)

    @property
    def is_remote(self) -> bool:
        """True se o provedor envia trechos de código para fora desta máquina."""
        if self.provider == "anthropic":
            return True
        if self.provider == "openai_compat":
            host = (urlparse(self.openai_base_url).hostname or "").lower()
            return host not in LOCAL_HOSTS
        return False


def load_settings(cwd: Path | None = None, environ: dict[str, str] | None = None) -> Settings:
    values, used = load_values(cwd, environ)
    provider = values.get("GITRUVIAN_PROVIDER", "none").strip().lower() or "none"
    if provider not in PROVIDERS:
        raise ConfigError(f"GITRUVIAN_PROVIDER inválido: use um de {', '.join(PROVIDERS)}")
    model = values.get("GITRUVIAN_MODEL", "").strip()
    if provider == "anthropic" and not model:
        model = DEFAULT_ANTHROPIC_MODEL
    base_url = values.get("GITRUVIAN_OPENAI_BASE_URL", "").strip() or DEFAULT_OPENAI_BASE_URL
    if urlparse(base_url).scheme not in {"http", "https"}:
        raise ConfigError("GITRUVIAN_OPENAI_BASE_URL deve começar com http:// ou https://")
    return Settings(
        provider=provider,
        model=model,
        allow_remote=_bool(values, "GITRUVIAN_ALLOW_REMOTE"),
        timeout=_int(values, "GITRUVIAN_TIMEOUT", 120, 5, 3600),
        max_context_chars=_int(values, "GITRUVIAN_MAX_CONTEXT_CHARS", 24000, 2000, 400000),
        openai_base_url=base_url.rstrip("/"),
        model_command=values.get("GITRUVIAN_MODEL_COMMAND", "").strip(),
        test_command=values.get("GITRUVIAN_TEST_COMMAND", "").strip(),
        test_timeout=_int(values, "GITRUVIAN_TEST_TIMEOUT", 600, 5, 86400),
        data_dir=values.get("GITRUVIAN_DATA_DIR", "").strip(),
        env_files=tuple(str(p) for p in used),
        anthropic_api_key=values.get("ANTHROPIC_API_KEY", "").strip(),
        openai_api_key=values.get("GITRUVIAN_OPENAI_API_KEY", "").strip(),
    )
