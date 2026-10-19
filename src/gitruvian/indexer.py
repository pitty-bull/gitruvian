"""Indexação local do código (SQLite FTS5) e montagem de contexto para o modelo."""

from __future__ import annotations

import fnmatch
import hashlib
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

from gitruvian import gitops
from gitruvian.models import Store, now_iso
from gitruvian.safety import UnsafePathError, check_repo_relative_path, is_sensitive_path, redact

MAX_FILE_BYTES = 512 * 1024
CHUNK_LINES = 60
IGNORE_FILE = ".gitruvianignore"

LANGS = {
    ".py": "python", ".js": "javascript", ".jsx": "javascript", ".ts": "typescript", ".tsx": "typescript",
    ".go": "go", ".rs": "rust", ".java": "java", ".kt": "kotlin", ".rb": "ruby", ".php": "php",
    ".cs": "csharp", ".c": "c", ".h": "c", ".cpp": "cpp", ".hpp": "cpp", ".swift": "swift",
    ".sh": "shell", ".ps1": "powershell", ".sql": "sql", ".html": "html", ".css": "css",
    ".md": "markdown", ".toml": "toml", ".yaml": "yaml", ".yml": "yaml", ".json": "json",
}


@dataclass
class IndexResult:
    scanned: int = 0
    added: int = 0
    updated: int = 0
    unchanged: int = 0
    removed: int = 0
    skipped: dict[str, int] = field(default_factory=lambda: {"sensível": 0, "binário": 0, "grande": 0, "ignorado": 0})
    chunks: int = 0


@dataclass
class Hit:
    path: str
    start_line: int
    end_line: int
    snippet: str
    score: float


def load_ignore_patterns(repo: Path) -> list[str]:
    path = repo / IGNORE_FILE
    if not path.is_file():
        return []
    patterns = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            patterns.append(line)
    return patterns


def _ignored(path: str, patterns: list[str]) -> bool:
    name = path.rsplit("/", 1)[-1]
    for pattern in patterns:
        if pattern.endswith("/") and (path.startswith(pattern) or f"/{pattern}" in f"/{path}"):
            return True
        if fnmatch.fnmatchcase(path, pattern) or fnmatch.fnmatchcase(name, pattern):
            return True
    return False


def read_text_file(path: Path) -> str | None:
    """Lê arquivo de texto; retorna None se for binário ou ilegível."""
    try:
        data = path.read_bytes()
    except OSError:
        return None
    if b"\x00" in data[:8192]:
        return None
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("latin-1")


def chunk_lines(text: str, size: int = CHUNK_LINES) -> list[tuple[int, int, str]]:
    lines = text.replace("\r\n", "\n").split("\n")
    chunks = []
    for start in range(0, len(lines), size):
        part = lines[start : start + size]
        if any(line.strip() for line in part):
            chunks.append((start + 1, start + len(part), "\n".join(part)))
    return chunks


def index_repo(
    store: Store,
    repo: Path,
    full: bool = False,
    progress: Callable[[int, int], None] | None = None,
) -> IndexResult:
    """Indexa os arquivos rastreados/não ignorados do repositório, de forma incremental."""
    result = IndexResult()
    patterns = load_ignore_patterns(repo)
    paths = gitops.ls_files(repo)
    known = {r["path"]: (r["id"], r["sha256"]) for r in store.conn.execute("SELECT id, path, sha256 FROM files")}
    seen: set[str] = set()
    conn = store.conn
    if full:
        with conn:
            conn.execute("DELETE FROM chunks")
            conn.execute("DELETE FROM files")
        known = {}

    total = len(paths)
    for i, rel in enumerate(paths, start=1):
        if progress:
            progress(i, total)
        result.scanned += 1
        try:
            rel = check_repo_relative_path(rel)
        except UnsafePathError:
            result.skipped["sensível"] += 1
            continue
        if is_sensitive_path(rel):
            result.skipped["sensível"] += 1
            continue
        if _ignored(rel, patterns):
            result.skipped["ignorado"] += 1
            continue
        full_path = repo / rel
        try:
            size = full_path.stat().st_size
        except OSError:
            continue
        if size > MAX_FILE_BYTES:
            result.skipped["grande"] += 1
            continue
        text = read_text_file(full_path)
        if text is None:
            result.skipped["binário"] += 1
            continue
        seen.add(rel)
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
        previous = known.get(rel)
        if previous and previous[1] == digest:
            result.unchanged += 1
            continue
        chunks = chunk_lines(text)
        lang = LANGS.get(Path(rel).suffix.lower(), "texto")
        with conn:
            if previous:
                conn.execute("DELETE FROM chunks WHERE file_id = ?", (previous[0],))
                conn.execute(
                    "UPDATE files SET sha256 = ?, size = ?, lang = ?, chunk_count = ?, indexed_at = ? WHERE id = ?",
                    (digest, size, lang, len(chunks), now_iso(), previous[0]),
                )
                file_id = previous[0]
                result.updated += 1
            else:
                cur = conn.execute(
                    "INSERT INTO files (path, sha256, size, lang, chunk_count, indexed_at) VALUES (?, ?, ?, ?, ?, ?)",
                    (rel, digest, size, lang, len(chunks), now_iso()),
                )
                file_id = cur.lastrowid
                result.added += 1
            conn.executemany(
                "INSERT INTO chunks (path, content, file_id, start_line, end_line) VALUES (?, ?, ?, ?, ?)",
                [(rel, content, file_id, start, end) for start, end, content in chunks],
            )

    for rel, (file_id, _sha) in known.items():
        if rel not in seen:
            with conn:
                conn.execute("DELETE FROM chunks WHERE file_id = ?", (file_id,))
                conn.execute("DELETE FROM files WHERE id = ?", (file_id,))
            result.removed += 1
    result.chunks = store.index_stats()["chunks"]
    return result


_WORD_RE = re.compile(r"[\w]{2,}", re.UNICODE)


def build_fts_query(text: str, max_terms: int = 24) -> str | None:
    """Transforma texto livre em consulta FTS5 segura (termos entre aspas, unidos por OR)."""
    terms: list[str] = []
    for word in _WORD_RE.findall(text.lower()):
        for part in word.split("_"):
            if len(part) >= 2 and part not in terms:
                terms.append(part)
    terms = terms[:max_terms]
    if not terms:
        return None
    return " OR ".join(f'"{t}"' for t in terms)


def search(store: Store, query: str, limit: int = 10) -> list[Hit]:
    fts = build_fts_query(query)
    if not fts:
        return []
    rows = store.conn.execute(
        "SELECT path, start_line, end_line, bm25(chunks, 4.0, 1.0) AS score, "
        "snippet(chunks, 1, '«', '»', ' … ', 16) AS snip "
        "FROM chunks WHERE chunks MATCH ? ORDER BY score LIMIT ?",
        (fts, limit),
    ).fetchall()
    return [
        Hit(r["path"], int(r["start_line"]), int(r["end_line"]), redact(r["snip"]), float(r["score"]))
        for r in rows
    ]


@dataclass
class ContextPack:
    text: str
    entries: list[dict] = field(default_factory=list)  # só caminhos e linhas — nunca conteúdo

    @property
    def chars(self) -> int:
        return len(self.text)


def build_context(
    store: Store,
    repo: Path,
    instruction: str,
    max_chars: int,
    explicit_files: list[str] | None = None,
) -> ContextPack:
    """Monta o contexto enviado ao modelo: arquivos pedidos + melhores resultados da busca."""
    pieces: list[str] = []
    entries: list[dict] = []
    budget = max_chars
    per_file_cap = max(2000, max_chars // 2)

    listing = [r["path"] for r in store.conn.execute("SELECT path FROM files ORDER BY path LIMIT 300")]
    if listing:
        header = "Arquivos do repositório (lista parcial):\n" + "\n".join(listing) + "\n"
        header = header[: max_chars // 6]
        pieces.append(header)
        budget -= len(header)

    ordered: list[str] = []
    for rel in explicit_files or []:
        rel = check_repo_relative_path(rel)
        if rel not in ordered:
            ordered.append(rel)
    hits_by_file: dict[str, list[Hit]] = {}
    for hit in search(store, instruction, limit=40):
        hits_by_file.setdefault(hit.path, []).append(hit)
        if hit.path not in ordered:
            ordered.append(hit.path)

    for rel in ordered:
        if budget <= 200:
            break
        if is_sensitive_path(rel):
            continue
        full_path = (repo / rel).resolve()
        if not full_path.is_relative_to(repo.resolve()) or not full_path.is_file():
            continue
        text = read_text_file(full_path)
        if text is None:
            continue
        text = text.replace("\r\n", "\n")
        if len(text) <= min(per_file_cap, budget):
            block = f"\n### ARQUIVO: {rel}\n```\n{redact(text)}\n```\n"
            entries.append({"path": rel, "lines": "todas"})
        else:
            lines = text.split("\n")
            parts = []
            ranges = []
            for hit in sorted(hits_by_file.get(rel, []), key=lambda h: h.start_line)[:4]:
                start = max(1, hit.start_line - 5)
                end = min(len(lines), hit.end_line + 5)
                parts.append(f"# linhas {start}-{end}\n" + "\n".join(lines[start - 1 : end]))
                ranges.append(f"{start}-{end}")
            if not parts:
                parts.append("# linhas 1-80\n" + "\n".join(lines[:80]))
                ranges.append("1-80")
            block = f"\n### ARQUIVO: {rel} (trechos)\n```\n{redact(chr(10).join(parts))}\n```\n"
            entries.append({"path": rel, "lines": ",".join(ranges)})
        if len(block) > budget:
            if len(entries) > 1:
                entries.pop()  # não cabe: melhor omitir do que enviar um arquivo cortado
                continue
            block = block[: max(0, budget - 8)] + "\n```\n"
            entries[-1]["lines"] += " (cortado)"
        pieces.append(block)
        budget -= len(block)
    return ContextPack(text="".join(pieces), entries=entries)
