"""Smoke test de ponta a ponta: executa a CLI real como subprocesso, sem rede."""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest
from conftest import git, make_repo

FAKE_MODEL = Path(__file__).parent / "fixtures" / "fake_model.py"


def _env(env_file: Path) -> dict[str, str]:
    env = {k: v for k, v in os.environ.items() if not k.startswith("GITRUVIAN_") and k != "ANTHROPIC_API_KEY"}
    env.update({"GITRUVIAN_ENV_FILE": str(env_file), "NO_COLOR": "1", "PYTHONIOENCODING": "utf-8",
                "COLUMNS": "200"})
    return env


def cli(cwd: Path, env: dict[str, str], *args: str, expect: int = 0) -> str:
    proc = subprocess.run([sys.executable, "-m", "gitruvian", *args], cwd=cwd, env=env, capture_output=True,
                          text=True, encoding="utf-8", timeout=120)
    output = proc.stdout + proc.stderr
    assert proc.returncode == expect, f"gitruvian {' '.join(args)} -> {proc.returncode}\n{output}"
    return output


def test_core_loop_end_to_end(tmp_path):
    repo = make_repo(tmp_path / "proj")
    env_file = tmp_path / "gitruvian.env"
    env_file.write_text(
        "GITRUVIAN_PROVIDER=command\n"
        f'GITRUVIAN_MODEL_COMMAND="{sys.executable}" "{FAKE_MODEL}"\n'
        "GITRUVIAN_MODEL=modelo-falso-de-teste\n",
        encoding="utf-8",
    )
    env = _env(env_file)

    out = cli(repo, env)
    assert "gitruvian init" in out  # estado vazio: ainda não inicializado
    out = cli(repo, env, "init", "--test-command", "{python} -m unittest discover -s tests")
    assert "inicializado" in out
    cli(repo, env, "test", expect=1)  # o bug existe antes da alteração

    out = cli(repo, env, "index")
    assert "1 sensível" in out
    assert "calc.py" in cli(repo, env, "search", "desconto")

    out = cli(repo, env, "go", "corrigir o desconto percentual em total", "--yes", "--commit")
    assert "testes passaram" in out and "commit" in out

    log = git(repo, "log", "-1", "--format=%B")
    assert log.startswith("Aplica o desconto como percentual") and "Gitruvian-Proposal:" in log
    assert git(repo, "status", "--porcelain", "--", "calc.py") == ""
    assert ".gitruvian" not in git(repo, "status", "--porcelain")

    history = cli(repo, env, "history")
    for kind in ("propose", "apply", "test", "commit"):
        assert kind in history
    assert "commitada" in cli(repo, env, "proposals")

    backup = tmp_path / "backup.json"
    cli(repo, env, "export", str(backup))
    data = json.loads(backup.read_text(encoding="utf-8"))
    assert data["proposals"][0]["status"] == "committed"

    other = make_repo(tmp_path / "outro")
    cli(other, env, "init")
    assert "1 proposals" in cli(other, env, "import", str(backup))
    assert "commitada" in cli(other, env, "proposals")


def test_degraded_mode_and_validation_errors(tmp_path):
    repo = make_repo(tmp_path / "proj")
    env = _env(tmp_path / "vazio.env")
    cli(repo, env, "init")
    out = cli(repo, env, "propose", "corrigir o desconto", expect=3)
    assert "modelo indisponível" in out and "--patch-file" in out
    evil = tmp_path / "evil.diff"
    evil.write_text("--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-a\n+b\n", encoding="utf-8")
    assert "entrada rejeitada" in cli(repo, env, "propose", "x" * 5, "--patch-file", str(evil), expect=2)
    assert "nenhuma proposta" in cli(repo, env, "show", "abcd1234", expect=2)
    assert "Nenhuma proposta" in cli(repo, env, "proposals")


@pytest.mark.parametrize("keep", [False])
def test_sample_data_lifecycle(tmp_path, keep):
    env = _env(tmp_path / "vazio.env")
    out = cli(tmp_path, env, "sample", "create", "loja")
    sample = tmp_path / "loja"
    assert "repositório de exemplo criado" in out and (sample / "DADOS_DE_EXEMPLO.md").is_file()
    proposals = cli(sample, env, "proposals")
    assert "[EXEMPLO]" in proposals
    pid = next(line.split()[0] for line in proposals.splitlines() if "[EXEMPLO]" in line)
    assert "testes passaram" in cli(sample, env, "apply", pid)
    assert "commit" in cli(sample, env, "accept", pid)
    assert "1 proposals" in cli(sample, env, "sample", "clear")
    cli(tmp_path, env, "sample", "remove", "loja", "--yes")
    assert not sample.exists()
