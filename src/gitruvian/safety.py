"""Regras de segurança: caminhos sensíveis, redação de segredos e validação de entradas."""

from __future__ import annotations

import fnmatch
import re
import unicodedata
from pathlib import PurePosixPath

# Arquivos que nunca são indexados, enviados ao modelo ou alterados por um diff.
SENSITIVE_PATTERNS: tuple[str, ...] = (
    ".env",
    ".env.*",
    "*.env",
    "*.pem",
    "*.key",
    "*.p12",
    "*.pfx",
    "*.jks",
    "*.keystore",
    "*.kdbx",
    "*.tfstate",
    "*.tfstate.*",
    "id_rsa*",
    "id_dsa*",
    "id_ecdsa*",
    "id_ed25519*",
    ".netrc",
    ".npmrc",
    ".pypirc",
    ".git-credentials",
    "credentials",
    "credentials.*",
    "*secret*",
    "*.sqlite",
    "*.db",
)

# Modelos de configuração sem segredos reais podem ser indexados.
SAFE_TEMPLATE_NAMES: frozenset[str] = frozenset(
    {".env.example", ".env.sample", ".env.template", ".env.dist"}
)

# Diretórios internos que um diff nunca pode tocar.
PROTECTED_DIRS: tuple[str, ...] = (".git", ".gitruvian")

_SECRET_RES: tuple[re.Pattern[str], ...] = (
    re.compile(
        r"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        re.DOTALL,
    ),
    re.compile(r"sk-ant-[A-Za-z0-9_\-]{10,}"),
    re.compile(r"\bsk-[A-Za-z0-9_\-]{20,}"),
    re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    re.compile(r"\bgh[pousr]_[A-Za-z0-9]{30,}\b"),
    re.compile(r"\bgithub_pat_[A-Za-z0-9_]{20,}\b"),
    re.compile(r"\bxox[abprs]-[A-Za-z0-9\-]{10,}"),
    re.compile(r"\bAIza[0-9A-Za-z\-_]{35}\b"),
    re.compile(r"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}"),
)
_ASSIGNMENT_RE = re.compile(
    r"(?i)\b([A-Za-z0-9_]*(?:api[_-]?key|secret|passw(?:or)?d|token|access[_-]?key|private[_-]?key)[A-Za-z0-9_]*)"
    r"(\s*[:=]\s*)(['\"]?)([^\s'\"]{6,})(['\"]?)"
)

REDACTED = "[REDIGIDO]"
_CONTROL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


def redact(text: str) -> str:
    """Substitui padrões que parecem segredos por um marcador."""
    for pattern in _SECRET_RES:
        text = pattern.sub(REDACTED, text)
    return _ASSIGNMENT_RE.sub(lambda m: f"{m.group(1)}{m.group(2)}{m.group(3)}{REDACTED}{m.group(5)}", text)


def is_sensitive_path(path: str) -> bool:
    """True se o caminho (relativo ao repositório) parece conter segredos."""
    parts = PurePosixPath(path.replace("\\", "/")).parts
    if not parts:
        return False
    name = parts[-1].lower()
    if name in SAFE_TEMPLATE_NAMES:
        return False
    if any(part.lower() in {".ssh", ".gnupg", ".aws"} for part in parts[:-1]):
        return True
    return any(fnmatch.fnmatchcase(name, pattern) for pattern in SENSITIVE_PATTERNS)


class UnsafePathError(ValueError):
    """Caminho rejeitado por sair do repositório ou tocar áreas protegidas."""


def check_repo_relative_path(path: str) -> str:
    """Valida e normaliza um caminho relativo vindo de fonte não confiável.

    Retorna o caminho normalizado com barras '/'. Levanta UnsafePathError se o
    caminho for absoluto, contiver '..', apontar para diretórios internos ou para
    arquivos sensíveis.
    """
    if not path or len(path) > 1024:
        raise UnsafePathError("caminho vazio ou longo demais")
    if "\x00" in path or _CONTROL_RE.search(path):
        raise UnsafePathError("caminho contém caracteres de controle")
    if "\\" in path:
        raise UnsafePathError(f"use '/' como separador: {path!r}")
    if path.startswith("/") or re.match(r"^[A-Za-z]:", path):
        raise UnsafePathError(f"caminho absoluto não é permitido: {path!r}")
    parts = PurePosixPath(path).parts
    if any(part in ("..", "") for part in parts):
        raise UnsafePathError(f"caminho sai do repositório: {path!r}")
    normalized = "/".join(part for part in parts if part != ".")
    if not normalized:
        raise UnsafePathError("caminho vazio")
    if normalized.split("/")[0].lower() in PROTECTED_DIRS:
        raise UnsafePathError(f"diretório protegido: {normalized!r}")
    if is_sensitive_path(normalized):
        raise UnsafePathError(f"arquivo sensível (possível segredo): {normalized!r}")
    return normalized


MAX_INSTRUCTION_CHARS = 4000


def clean_instruction(text: str) -> str:
    """Normaliza e valida o pedido do usuário."""
    text = unicodedata.normalize("NFC", text or "")
    text = _CONTROL_RE.sub("", text).replace("\r\n", "\n").strip()
    if len(text) < 3:
        raise ValueError("descreva a alteração com pelo menos 3 caracteres")
    if len(text) > MAX_INSTRUCTION_CHARS:
        raise ValueError(f"pedido longo demais (máximo {MAX_INSTRUCTION_CHARS} caracteres)")
    return text


def truncate(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    return text[: max(0, limit - 1)] + "…"
