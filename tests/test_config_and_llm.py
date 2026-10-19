from __future__ import annotations

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import pytest

from gitruvian.config import ConfigError, Settings, load_settings, parse_env_file
from gitruvian.llm import (
    CommandProvider,
    ModelError,
    ModelUnavailable,
    OpenAICompatProvider,
    anthropic_request_kwargs,
    describe_provider,
    get_provider,
)


def test_parse_env_file_reads_only_known_keys(tmp_path):
    env = tmp_path / ".env"
    env.write_text(
        "# comentário\nGITRUVIAN_PROVIDER='command'\nOUTRA_COISA=ignorada\nexport GITRUVIAN_TIMEOUT=30 # s\n",
        encoding="utf-8",
    )
    assert parse_env_file(env) == {"GITRUVIAN_PROVIDER": "command", "GITRUVIAN_TIMEOUT": "30"}
    env.write_text('GITRUVIAN_MODEL_COMMAND="C:/a b/python.exe" "x.py"\n', encoding="utf-8")
    assert parse_env_file(env)["GITRUVIAN_MODEL_COMMAND"] == '"C:/a b/python.exe" "x.py"'


def test_environment_overrides_env_file(tmp_path):
    env = tmp_path / "custom.env"
    env.write_text("GITRUVIAN_PROVIDER=command\nGITRUVIAN_TIMEOUT=30\n", encoding="utf-8")
    settings = load_settings(tmp_path, {"GITRUVIAN_ENV_FILE": str(env), "GITRUVIAN_TIMEOUT": "45"})
    assert settings.provider == "command" and settings.timeout == 45


def test_invalid_config_is_rejected(tmp_path):
    with pytest.raises(ConfigError):
        load_settings(tmp_path, {"GITRUVIAN_ENV_FILE": "x", "GITRUVIAN_PROVIDER": "magic"})
    with pytest.raises(ConfigError):
        load_settings(tmp_path, {"GITRUVIAN_ENV_FILE": "x", "GITRUVIAN_TIMEOUT": "abc"})


def test_secrets_hidden_from_repr_and_description():
    s = Settings(provider="anthropic", model="claude-opus-5-5", anthropic_api_key="sk-ant-secret-xyz",
                 allow_remote=True)
    assert "sk-ant-secret-xyz" not in repr(s)
    assert "sk-ant-secret-xyz" not in " ".join(describe_provider(s))


def test_default_is_private_degraded_mode(tmp_path):
    s = load_settings(tmp_path, {"GITRUVIAN_ENV_FILE": str(tmp_path / "nao-existe.env")})
    assert s.provider == "none" and not s.is_remote
    with pytest.raises(ModelUnavailable):
        get_provider(s)


def test_remote_detection():
    assert Settings(provider="openai_compat", openai_base_url="http://localhost:11434/v1").is_remote is False
    assert Settings(provider="openai_compat", openai_base_url="https://api.example.com/v1").is_remote is True
    with pytest.raises(ModelUnavailable, match="ALLOW_REMOTE"):
        get_provider(Settings(provider="openai_compat", model="m", openai_base_url="https://api.example.com/v1"))


def test_anthropic_request_shape():
    kwargs = anthropic_request_kwargs("claude-opus-5-5", "sys", "user")
    assert kwargs["fallbacks"] == "default" and kwargs["betas"] == ["server-side-fallback-2026-07-01"]
    assert kwargs["messages"] == [{"role": "user", "content": "user"}]
    assert "fallbacks" not in anthropic_request_kwargs("claude-haiku-5-5", "s", "u")


class _Handler(BaseHTTPRequestHandler):
    received: list[dict] = []

    def do_POST(self):  # noqa: N802
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        _Handler.received.append({"path": self.path, "body": body, "auth": self.headers.get("Authorization")})
        payload = json.dumps({"model": body["model"], "choices": [{"message": {"content": "COMMIT: ok"}}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *args):  # silencia o servidor de teste
        pass


def test_openai_compat_provider_over_real_http():
    server = HTTPServer(("127.0.0.1", 0), _Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        settings = Settings(provider="openai_compat", model="modelo-local",
                            openai_base_url=f"http://127.0.0.1:{server.server_port}/v1", openai_api_key="tok")
        reply = get_provider(settings).complete("sys", "user")
        assert reply.text == "COMMIT: ok" and reply.model == "modelo-local"
        req = _Handler.received[-1]
        assert req["path"] == "/v1/chat/completions" and req["auth"] == "Bearer tok"
        assert req["body"]["messages"][0] == {"role": "system", "content": "sys"}
    finally:
        server.shutdown()


def test_openai_compat_unreachable_is_degraded():
    settings = Settings(provider="openai_compat", model="m", openai_base_url="http://127.0.0.1:9/v1", timeout=5)
    with pytest.raises(ModelUnavailable, match="conectar"):
        OpenAICompatProvider(settings).complete("s", "u")


def test_command_provider(tmp_path: Path):
    script = tmp_path / "echo_model.py"
    script.write_text("import sys\ndata = sys.stdin.buffer.read().decode()\nprint('RECEBI', len(data) > 0)\n",
                      encoding="utf-8")
    provider = CommandProvider(Settings(provider="command", model_command=f'"{sys.executable}" "{script}"'))
    assert provider.complete("s", "u").text.strip() == "RECEBI True"
    failing = CommandProvider(Settings(provider="command", model_command=f'"{sys.executable}" -c "raise SystemExit(4)"'))
    with pytest.raises(ModelError, match="código 4"):
        failing.complete("s", "u")
