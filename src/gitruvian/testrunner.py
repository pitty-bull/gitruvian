"""Detecção e execução do comando de testes do repositório."""

from __future__ import annotations

import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

from gitruvian.llm import split_command
from gitruvian.safety import redact

OUTPUT_TAIL_LINES = 60
OUTPUT_TAIL_CHARS = 6000


@dataclass
class CommandResult:
    exit_code: int
    duration_ms: int
    output_tail: str

    @property
    def passed(self) -> bool:
        return self.exit_code == 0


def detect_test_command(repo: Path) -> str | None:
    """Sugere um comando de testes a partir dos arquivos do projeto."""
    if (repo / "pyproject.toml").is_file() or (repo / "pytest.ini").is_file() or (repo / "setup.cfg").is_file():
        return "python -m pytest -q"
    if (repo / "package.json").is_file():
        return "npm test"
    if (repo / "Cargo.toml").is_file():
        return "cargo test"
    if (repo / "go.mod").is_file():
        return "go test ./..."
    if (repo / "tests").is_dir() and any((repo / "tests").glob("test_*.py")):
        return "{python} -m unittest discover -s tests"
    return None


def expand_command(command: str) -> list[str]:
    """Divide o comando (sem shell) e expande {python} para o interpretador atual."""
    argv = [part.replace("{python}", sys.executable) for part in split_command(command)]
    if not argv:
        raise ValueError("comando de testes vazio")
    resolved = shutil.which(argv[0])
    if resolved:
        argv[0] = resolved
    return argv


def tail(text: str) -> str:
    lines = text.replace("\r\n", "\n").rstrip("\n").split("\n")[-OUTPUT_TAIL_LINES:]
    return redact("\n".join(lines)[-OUTPUT_TAIL_CHARS:])


def run_tests(command: str, repo: Path, timeout: int) -> CommandResult:
    argv = expand_command(command)
    started = time.monotonic()
    try:
        proc = subprocess.run(argv, cwd=str(repo), capture_output=True, timeout=timeout)
    except FileNotFoundError:
        return CommandResult(127, 0, f"comando não encontrado: {argv[0]}")
    except subprocess.TimeoutExpired as exc:
        output = (exc.stdout or b"") + (exc.stderr or b"")
        return CommandResult(
            124,
            int((time.monotonic() - started) * 1000),
            tail(output.decode("utf-8", errors="replace")) + f"\n[tempo esgotado após {timeout}s]",
        )
    duration = int((time.monotonic() - started) * 1000)
    output = (proc.stdout + b"\n" + proc.stderr).decode("utf-8", errors="replace")
    return CommandResult(proc.returncode, duration, tail(output))
