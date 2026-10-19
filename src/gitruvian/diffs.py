"""Extração e validação de diffs unificados vindos de fontes não confiáveis."""

from __future__ import annotations

import re
from dataclasses import dataclass

from gitruvian.safety import UnsafePathError, check_repo_relative_path

MAX_DIFF_BYTES = 400_000
MAX_FILES = 50

_FENCE_RE = re.compile(r"```[ \t]*(?:diff|patch|udiff)?[ \t]*\n(.*?)```", re.DOTALL | re.IGNORECASE)
_COMMIT_RE = re.compile(r"^\s*COMMIT:\s*(.+?)\s*$", re.MULTILINE | re.IGNORECASE)
_HUNK_RE = re.compile(r"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@")


class DiffValidationError(ValueError):
    """O texto não é um diff aceitável."""


@dataclass
class ParsedDiff:
    text: str
    files: list[str]
    additions: int
    deletions: int


def _looks_like_diff(text: str) -> bool:
    return "\n@@ " in "\n" + text and ("--- " in text or "diff --git" in text)


def extract_diff(text: str) -> str:
    """Encontra o diff dentro da resposta do modelo (bloco ```diff ou texto puro)."""
    text = text.replace("\r\n", "\n")
    for block in _FENCE_RE.findall(text):
        if _looks_like_diff(block):
            return block
    lines = text.split("\n")
    for i, line in enumerate(lines):
        if line.startswith("diff --git ") or (
            line.startswith("--- ") and i + 1 < len(lines) and lines[i + 1].startswith("+++ ")
        ):
            return "\n".join(lines[i:])
    raise DiffValidationError("a resposta não contém um diff unificado")


def extract_commit_message(text: str) -> str | None:
    match = _COMMIT_RE.search(text or "")
    if not match:
        return None
    message = match.group(1).strip().strip("`\"'")
    return message[:200] or None


def _strip_prefix(raw: str) -> str | None:
    raw = raw.split("\t", 1)[0].strip()
    if raw.startswith('"') and raw.endswith('"'):
        raise DiffValidationError(f"caminho com caracteres especiais não suportado: {raw}")
    if raw == "/dev/null":
        return None
    if raw.startswith(("a/", "b/")):
        raw = raw[2:]
    return raw


def validate_diff(text: str) -> ParsedDiff:
    """Valida um diff: tamanho, formato, caminhos seguros, sem binários/symlinks."""
    if not text or not text.strip():
        raise DiffValidationError("diff vazio")
    if len(text.encode("utf-8", errors="replace")) > MAX_DIFF_BYTES:
        raise DiffValidationError(f"diff maior que {MAX_DIFF_BYTES // 1000} KB")
    if "\x00" in text:
        raise DiffValidationError("diff contém bytes nulos")
    text = text.replace("\r\n", "\n")
    if not text.endswith("\n"):
        text += "\n"

    files: list[str] = []
    additions = deletions = hunks = 0
    in_hunk = False
    for line in text.split("\n"):
        if line.startswith("GIT binary patch") or line.startswith("Binary files "):
            raise DiffValidationError("diffs binários não são aceitos")
        if re.match(r"^(new|deleted) file mode 120000|^(old|new) mode 120000", line):
            raise DiffValidationError("links simbólicos não são aceitos")
        if line.startswith("diff --git "):
            in_hunk = False
            continue
        if not in_hunk and line.startswith(("--- ", "+++ ")):
            path = _strip_prefix(line[4:])
            if path is not None:
                try:
                    path = check_repo_relative_path(path)
                except UnsafePathError as exc:
                    raise DiffValidationError(str(exc)) from exc
                if path not in files:
                    files.append(path)
            continue
        if line.startswith(("rename from ", "rename to ", "copy from ", "copy to ")):
            raw = line.split(" ", 2)[2]
            try:
                path = check_repo_relative_path(raw)
            except UnsafePathError as exc:
                raise DiffValidationError(str(exc)) from exc
            if path not in files:
                files.append(path)
            continue
        if _HUNK_RE.match(line):
            hunks += 1
            in_hunk = True
            continue
        if in_hunk:
            if line.startswith("+"):
                additions += 1
            elif line.startswith("-"):
                deletions += 1
            elif line.startswith((" ", "\\")) or line == "":
                pass
            else:
                in_hunk = False

    if not files:
        raise DiffValidationError("o diff não indica nenhum arquivo (faltam linhas ---/+++)")
    if hunks == 0:
        raise DiffValidationError("o diff não tem nenhum bloco @@")
    if len(files) > MAX_FILES:
        raise DiffValidationError(f"o diff altera mais de {MAX_FILES} arquivos")
    return ParsedDiff(text=text, files=files, additions=additions, deletions=deletions)
