"""Modelo de dados do Gitruvian, persistido em SQLite.

Tabelas:
- settings:   preferências por repositório (ex.: comando de testes).
- files:      metadados dos arquivos indexados (caminho, hash, tamanho).
- chunks:     índice FTS5 com trechos do código (apenas para busca local).
- proposals:  cada diff proposto, com origem, estado e commit resultante.
- test_runs:  cada execução de testes (comando, código de saída, resumo).
- events:     trilha de auditoria — uma linha por alteração de estado.
"""

from __future__ import annotations

import hashlib
import json
import re
import sqlite3
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import StrEnum
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1


class Status(StrEnum):
    PROPOSED = "proposed"
    APPLIED = "applied"
    TESTS_PASSED = "tests_passed"
    TESTS_FAILED = "tests_failed"
    COMMITTED = "committed"
    REVERTED = "reverted"
    REJECTED = "rejected"


STATUS_LABELS: dict[Status, str] = {
    Status.PROPOSED: "proposta",
    Status.APPLIED: "aplicada",
    Status.TESTS_PASSED: "testes ok",
    Status.TESTS_FAILED: "testes falharam",
    Status.COMMITTED: "commitada",
    Status.REVERTED: "revertida",
    Status.REJECTED: "rejeitada",
}

TRANSITIONS: dict[Status, frozenset[Status]] = {
    Status.PROPOSED: frozenset({Status.APPLIED, Status.REJECTED}),
    Status.APPLIED: frozenset(
        {Status.TESTS_PASSED, Status.TESTS_FAILED, Status.COMMITTED, Status.REVERTED}
    ),
    Status.TESTS_PASSED: frozenset(
        {Status.TESTS_PASSED, Status.TESTS_FAILED, Status.COMMITTED, Status.REVERTED}
    ),
    Status.TESTS_FAILED: frozenset({Status.TESTS_PASSED, Status.TESTS_FAILED, Status.REVERTED}),
    Status.COMMITTED: frozenset({Status.REVERTED}),
    Status.REVERTED: frozenset({Status.APPLIED, Status.REJECTED}),
    Status.REJECTED: frozenset(),
}

# Campos que podem ser atualizados junto com uma transição de estado.
TRANSITION_FIELDS = frozenset({"base_commit", "branch", "commit_sha", "files"})

ID_RE = re.compile(r"^[0-9a-f]{32}$")
ID_PREFIX_RE = re.compile(r"^[0-9a-f]{4,32}$")


class InvalidTransition(ValueError):
    pass


class NotFound(LookupError):
    pass


class AmbiguousId(LookupError):
    pass


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def new_id() -> str:
    return uuid.uuid4().hex


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


@dataclass
class Proposal:
    id: str
    created_at: str
    updated_at: str
    instruction: str
    provider: str
    model: str
    diff: str
    diff_sha256: str
    files: list[str]
    commit_message: str
    status: Status
    base_commit: str | None = None
    branch: str | None = None
    commit_sha: str | None = None
    check_error: str | None = None
    context: list[dict[str, Any]] = field(default_factory=list)
    latency_ms: int | None = None
    is_sample: bool = False

    @property
    def short_id(self) -> str:
        return self.id[:8]

    @property
    def status_label(self) -> str:
        return STATUS_LABELS[self.status]


@dataclass
class TestRun:
    id: str
    proposal_id: str | None
    command: str
    started_at: str
    duration_ms: int
    exit_code: int
    passed: bool
    output_tail: str
    is_sample: bool = False


@dataclass
class Event:
    id: str
    ts: str
    kind: str
    summary: str
    proposal_id: str | None = None
    data: dict[str, Any] = field(default_factory=dict)
    is_sample: bool = False


_STATUS_SQL = ", ".join(f"'{s.value}'" for s in Status)

SCHEMA = f"""
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS files (
    id INTEGER PRIMARY KEY,
    path TEXT NOT NULL UNIQUE,
    sha256 TEXT NOT NULL,
    size INTEGER NOT NULL,
    lang TEXT NOT NULL,
    chunk_count INTEGER NOT NULL,
    indexed_at TEXT NOT NULL
);
CREATE VIRTUAL TABLE IF NOT EXISTS chunks USING fts5(
    path, content, file_id UNINDEXED, start_line UNINDEXED, end_line UNINDEXED,
    tokenize = 'unicode61'
);
CREATE TABLE IF NOT EXISTS proposals (
    id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    instruction TEXT NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    diff TEXT NOT NULL,
    diff_sha256 TEXT NOT NULL,
    files_json TEXT NOT NULL,
    commit_message TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ({_STATUS_SQL})),
    base_commit TEXT,
    branch TEXT,
    commit_sha TEXT,
    check_error TEXT,
    context_json TEXT NOT NULL DEFAULT '[]',
    latency_ms INTEGER,
    is_sample INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS proposals_created ON proposals(created_at);
CREATE TABLE IF NOT EXISTS test_runs (
    id TEXT PRIMARY KEY,
    proposal_id TEXT REFERENCES proposals(id) ON DELETE SET NULL,
    command TEXT NOT NULL,
    started_at TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    exit_code INTEGER NOT NULL,
    passed INTEGER NOT NULL,
    output_tail TEXT NOT NULL,
    is_sample INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS events (
    id TEXT PRIMARY KEY,
    ts TEXT NOT NULL,
    kind TEXT NOT NULL,
    summary TEXT NOT NULL,
    proposal_id TEXT REFERENCES proposals(id) ON DELETE SET NULL,
    data_json TEXT NOT NULL DEFAULT '{{}}',
    is_sample INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
"""


def connect(db_path: Path | str) -> sqlite3.Connection:
    conn = sqlite3.connect(str(db_path), timeout=10)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA foreign_keys = ON")
    if str(db_path) != ":memory:":
        conn.execute("PRAGMA journal_mode = WAL")
    return conn


class Store:
    """Acesso ao banco SQLite. Todas as consultas são parametrizadas."""

    def __init__(self, conn: sqlite3.Connection):
        self.conn = conn
        self.migrate()

    @classmethod
    def open(cls, db_path: Path | str) -> "Store":
        return cls(connect(db_path))

    def close(self) -> None:
        self.conn.close()

    def migrate(self) -> None:
        version = self.conn.execute("PRAGMA user_version").fetchone()[0]
        if version > SCHEMA_VERSION:
            raise RuntimeError(
                f"banco criado por uma versão mais nova do Gitruvian (schema {version})"
            )
        with self.conn:
            self.conn.executescript(SCHEMA)
            self.conn.execute(f"PRAGMA user_version = {SCHEMA_VERSION}")

    # ---------------------------------------------------------------- settings
    def get_setting(self, key: str, default: str | None = None) -> str | None:
        row = self.conn.execute("SELECT value FROM settings WHERE key = ?", (key,)).fetchone()
        return row["value"] if row else default

    def set_setting(self, key: str, value: str) -> None:
        with self.conn:
            self.conn.execute(
                "INSERT INTO settings(key, value, updated_at) VALUES (?, ?, ?) "
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                (key, value, now_iso()),
            )

    def delete_setting(self, key: str) -> bool:
        with self.conn:
            return self.conn.execute("DELETE FROM settings WHERE key = ?", (key,)).rowcount > 0

    def settings(self) -> dict[str, str]:
        return {r["key"]: r["value"] for r in self.conn.execute("SELECT key, value FROM settings ORDER BY key")}

    # --------------------------------------------------------------- proposals
    def add_proposal(
        self,
        *,
        instruction: str,
        provider: str,
        model: str,
        diff: str,
        files: list[str],
        commit_message: str,
        context: list[dict[str, Any]] | None = None,
        latency_ms: int | None = None,
        check_error: str | None = None,
        is_sample: bool = False,
    ) -> Proposal:
        ts = now_iso()
        proposal = Proposal(
            id=new_id(),
            created_at=ts,
            updated_at=ts,
            instruction=instruction,
            provider=provider,
            model=model,
            diff=diff,
            diff_sha256=sha256_text(diff),
            files=list(files),
            commit_message=commit_message,
            status=Status.PROPOSED,
            check_error=check_error,
            context=list(context or []),
            latency_ms=latency_ms,
            is_sample=is_sample,
        )
        self.insert_proposal(proposal)
        return proposal

    def insert_proposal(self, p: Proposal) -> bool:
        """Insere uma proposta já montada. Retorna False se o id já existia."""
        with self.conn:
            cur = self.conn.execute(
                "INSERT OR IGNORE INTO proposals (id, created_at, updated_at, instruction, provider, model, "
                "diff, diff_sha256, files_json, commit_message, status, base_commit, branch, commit_sha, "
                "check_error, context_json, latency_ms, is_sample) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    p.id,
                    p.created_at,
                    p.updated_at,
                    p.instruction,
                    p.provider,
                    p.model,
                    p.diff,
                    p.diff_sha256,
                    json.dumps(p.files),
                    p.commit_message,
                    Status(p.status).value,
                    p.base_commit,
                    p.branch,
                    p.commit_sha,
                    p.check_error,
                    json.dumps(p.context),
                    p.latency_ms,
                    int(p.is_sample),
                ),
            )
            return cur.rowcount > 0

    @staticmethod
    def _row_to_proposal(row: sqlite3.Row) -> Proposal:
        return Proposal(
            id=row["id"],
            created_at=row["created_at"],
            updated_at=row["updated_at"],
            instruction=row["instruction"],
            provider=row["provider"],
            model=row["model"],
            diff=row["diff"],
            diff_sha256=row["diff_sha256"],
            files=json.loads(row["files_json"]),
            commit_message=row["commit_message"],
            status=Status(row["status"]),
            base_commit=row["base_commit"],
            branch=row["branch"],
            commit_sha=row["commit_sha"],
            check_error=row["check_error"],
            context=json.loads(row["context_json"]),
            latency_ms=row["latency_ms"],
            is_sample=bool(row["is_sample"]),
        )

    def get_proposal(self, id_or_prefix: str) -> Proposal:
        key = (id_or_prefix or "").strip().lower()
        if not ID_PREFIX_RE.match(key):
            raise NotFound("id inválido: use de 4 a 32 caracteres hexadecimais")
        rows = self.conn.execute(
            "SELECT * FROM proposals WHERE id LIKE ? ORDER BY created_at LIMIT 2", (key + "%",)
        ).fetchall()
        if not rows:
            raise NotFound(f"nenhuma proposta com id {key}")
        if len(rows) > 1:
            raise AmbiguousId(f"o prefixo {key} corresponde a mais de uma proposta; use mais caracteres")
        return self._row_to_proposal(rows[0])

    def list_proposals(self, status: Status | None = None, limit: int = 50) -> list[Proposal]:
        if status is None:
            rows = self.conn.execute(
                "SELECT * FROM proposals ORDER BY created_at DESC, rowid DESC LIMIT ?", (limit,)
            )
        else:
            rows = self.conn.execute(
                "SELECT * FROM proposals WHERE status = ? ORDER BY created_at DESC, rowid DESC LIMIT ?",
                (Status(status).value, limit),
            )
        return [self._row_to_proposal(r) for r in rows]

    def transition(self, proposal_id: str, new_status: Status, **fields: Any) -> Proposal:
        """Muda o estado respeitando TRANSITIONS e atualiza campos permitidos."""
        unknown = set(fields) - TRANSITION_FIELDS
        if unknown:
            raise ValueError(f"campos não permitidos: {', '.join(sorted(unknown))}")
        current = self.get_proposal(proposal_id)
        new_status = Status(new_status)
        if new_status not in TRANSITIONS[current.status]:
            raise InvalidTransition(
                f"não é possível ir de '{current.status_label}' para '{STATUS_LABELS[new_status]}'"
            )
        assignments = ["status = ?", "updated_at = ?"]
        values: list[Any] = [new_status.value, now_iso()]
        for key, value in fields.items():
            column = "files_json" if key == "files" else key
            assignments.append(f"{column} = ?")
            values.append(json.dumps(value) if key == "files" else value)
        values.append(current.id)
        with self.conn:
            self.conn.execute(f"UPDATE proposals SET {', '.join(assignments)} WHERE id = ?", values)
        return self.get_proposal(current.id)

    def status_counts(self) -> dict[Status, int]:
        counts = {s: 0 for s in Status}
        for row in self.conn.execute("SELECT status, COUNT(*) AS n FROM proposals GROUP BY status"):
            counts[Status(row["status"])] = row["n"]
        return counts

    # --------------------------------------------------------------- test runs
    def add_test_run(
        self,
        *,
        command: str,
        duration_ms: int,
        exit_code: int,
        output_tail: str,
        proposal_id: str | None = None,
        started_at: str | None = None,
        is_sample: bool = False,
    ) -> TestRun:
        run = TestRun(
            id=new_id(),
            proposal_id=proposal_id,
            command=command,
            started_at=started_at or now_iso(),
            duration_ms=duration_ms,
            exit_code=exit_code,
            passed=exit_code == 0,
            output_tail=output_tail,
            is_sample=is_sample,
        )
        self.insert_test_run(run)
        return run

    def insert_test_run(self, run: TestRun) -> bool:
        with self.conn:
            cur = self.conn.execute(
                "INSERT OR IGNORE INTO test_runs (id, proposal_id, command, started_at, duration_ms, "
                "exit_code, passed, output_tail, is_sample) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    run.id,
                    run.proposal_id,
                    run.command,
                    run.started_at,
                    run.duration_ms,
                    run.exit_code,
                    int(run.passed),
                    run.output_tail,
                    int(run.is_sample),
                ),
            )
            return cur.rowcount > 0

    def list_test_runs(self, proposal_id: str | None = None, limit: int = 50) -> list[TestRun]:
        if proposal_id:
            rows = self.conn.execute(
                "SELECT * FROM test_runs WHERE proposal_id = ? ORDER BY started_at DESC, rowid DESC LIMIT ?",
                (proposal_id, limit),
            )
        else:
            rows = self.conn.execute(
                "SELECT * FROM test_runs ORDER BY started_at DESC, rowid DESC LIMIT ?", (limit,)
            )
        return [
            TestRun(
                id=r["id"],
                proposal_id=r["proposal_id"],
                command=r["command"],
                started_at=r["started_at"],
                duration_ms=r["duration_ms"],
                exit_code=r["exit_code"],
                passed=bool(r["passed"]),
                output_tail=r["output_tail"],
                is_sample=bool(r["is_sample"]),
            )
            for r in rows
        ]

    # ------------------------------------------------------------------ events
    def add_event(
        self,
        kind: str,
        summary: str,
        *,
        proposal_id: str | None = None,
        data: dict[str, Any] | None = None,
        is_sample: bool = False,
    ) -> Event:
        event = Event(
            id=new_id(),
            ts=now_iso(),
            kind=kind,
            summary=summary,
            proposal_id=proposal_id,
            data=dict(data or {}),
            is_sample=is_sample,
        )
        self.insert_event(event)
        return event

    def insert_event(self, event: Event) -> bool:
        with self.conn:
            cur = self.conn.execute(
                "INSERT OR IGNORE INTO events (id, ts, kind, summary, proposal_id, data_json, is_sample) "
                "VALUES (?, ?, ?, ?, ?, ?, ?)",
                (
                    event.id,
                    event.ts,
                    event.kind,
                    event.summary,
                    event.proposal_id,
                    json.dumps(event.data),
                    int(event.is_sample),
                ),
            )
            return cur.rowcount > 0

    def list_events(self, limit: int = 50, proposal_id: str | None = None) -> list[Event]:
        if proposal_id:
            rows = self.conn.execute(
                "SELECT * FROM events WHERE proposal_id = ? ORDER BY ts DESC, rowid DESC LIMIT ?",
                (proposal_id, limit),
            )
        else:
            rows = self.conn.execute("SELECT * FROM events ORDER BY ts DESC, rowid DESC LIMIT ?", (limit,))
        return [
            Event(
                id=r["id"],
                ts=r["ts"],
                kind=r["kind"],
                summary=r["summary"],
                proposal_id=r["proposal_id"],
                data=json.loads(r["data_json"]),
                is_sample=bool(r["is_sample"]),
            )
            for r in rows
        ]

    # ------------------------------------------------------------------- index
    def index_stats(self) -> dict[str, Any]:
        row = self.conn.execute(
            "SELECT COUNT(*) AS files, COALESCE(SUM(chunk_count), 0) AS chunks, MAX(indexed_at) AS last "
            "FROM files"
        ).fetchone()
        return {"files": row["files"], "chunks": row["chunks"], "last_indexed": row["last"]}

    # ----------------------------------------------------------------- samples
    def delete_samples(self) -> dict[str, int]:
        """Apaga todos os registros marcados como dados de exemplo."""
        with self.conn:
            counts = {
                "events": self.conn.execute("DELETE FROM events WHERE is_sample = 1").rowcount,
                "test_runs": self.conn.execute("DELETE FROM test_runs WHERE is_sample = 1").rowcount,
                "proposals": self.conn.execute("DELETE FROM proposals WHERE is_sample = 1").rowcount,
            }
        return counts
