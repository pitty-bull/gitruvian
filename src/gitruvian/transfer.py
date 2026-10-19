"""Exportação e importação em JSON aberto, para o usuário não ficar preso ao app."""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

from gitruvian import __version__
from gitruvian.diffs import DiffValidationError, validate_diff
from gitruvian.models import ID_RE, Event, Proposal, Status, Store, TestRun, now_iso, sha256_text

FORMAT = "gitruvian-export"
FORMAT_VERSION = 1
MAX_IMPORT_BYTES = 50 * 1024 * 1024
MAX_RECORDS = 100_000
# Configurações que nunca são importadas: executam comandos na máquina.
NON_IMPORTABLE_SETTINGS = frozenset({"test_command"})


class ImportValidationError(ValueError):
    pass


@dataclass
class ImportReport:
    inserted: dict[str, int] = field(default_factory=lambda: {"proposals": 0, "test_runs": 0, "events": 0})
    duplicates: int = 0
    skipped: list[str] = field(default_factory=list)


def export_data(store: Store, include_samples: bool = True) -> dict[str, Any]:
    proposals = [p for p in store.list_proposals(limit=MAX_RECORDS) if include_samples or not p.is_sample]
    runs = [r for r in store.list_test_runs(limit=MAX_RECORDS) if include_samples or not r.is_sample]
    events = [e for e in store.list_events(limit=MAX_RECORDS) if include_samples or not e.is_sample]
    return {
        "format": FORMAT,
        "version": FORMAT_VERSION,
        "exported_at": now_iso(),
        "app_version": __version__,
        "note": "Contém diffs propostos (podem incluir trechos de código). Não contém o índice nem segredos.",
        "settings": {k: v for k, v in store.settings().items() if k not in NON_IMPORTABLE_SETTINGS},
        "proposals": [{**asdict(p), "status": p.status.value} for p in reversed(proposals)],
        "test_runs": [asdict(r) for r in reversed(runs)],
        "events": [asdict(e) for e in reversed(events)],
    }


def export_to_file(store: Store, path: Path, include_samples: bool = True) -> dict[str, int]:
    data = export_data(store, include_samples)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(path)
    return {k: len(data[k]) for k in ("proposals", "test_runs", "events")}


# ------------------------------------------------------------------ validação
def _str(obj: dict, key: str, max_len: int, *, optional: bool = False) -> str | None:
    value = obj.get(key)
    if value is None and optional:
        return None
    if not isinstance(value, str):
        raise ImportValidationError(f"campo '{key}' deve ser texto")
    if len(value) > max_len:
        raise ImportValidationError(f"campo '{key}' longo demais")
    return value


def _int(obj: dict, key: str, *, optional: bool = False) -> int | None:
    value = obj.get(key)
    if value is None and optional:
        return None
    if not isinstance(value, int) or isinstance(value, bool):
        raise ImportValidationError(f"campo '{key}' deve ser inteiro")
    return value


def _bool(obj: dict, key: str) -> bool:
    value = obj.get(key, False)
    if not isinstance(value, bool):
        raise ImportValidationError(f"campo '{key}' deve ser booleano")
    return value


def _id(obj: dict, key: str, *, optional: bool = False) -> str | None:
    value = obj.get(key)
    if value is None and optional:
        return None
    if not isinstance(value, str) or not ID_RE.match(value):
        raise ImportValidationError(f"campo '{key}' não é um id válido")
    return value


def _ts(obj: dict, key: str) -> str:
    value = _str(obj, key, 40)
    try:
        datetime.fromisoformat(value)  # type: ignore[arg-type]
    except ValueError as exc:
        raise ImportValidationError(f"campo '{key}' não é uma data ISO") from exc
    return value  # type: ignore[return-value]


def _sha(obj: dict, key: str, *, optional: bool = True) -> str | None:
    value = _str(obj, key, 64, optional=optional)
    if value is not None and not all(c in "0123456789abcdef" for c in value):
        raise ImportValidationError(f"campo '{key}' não é um hash válido")
    return value


def _parse_proposal(obj: Any) -> Proposal:
    if not isinstance(obj, dict):
        raise ImportValidationError("proposta não é um objeto")
    try:
        status = Status(obj.get("status"))
    except ValueError as exc:
        raise ImportValidationError("estado de proposta inválido") from exc
    diff = _str(obj, "diff", 400_000)
    try:
        parsed = validate_diff(diff or "")
    except DiffValidationError as exc:
        raise ImportValidationError(f"diff inválido: {exc}") from exc
    context = obj.get("context", [])
    if not isinstance(context, list) or len(context) > 500 or not all(
        isinstance(c, dict) and all(isinstance(v, str) and len(v) < 1100 for v in c.values()) for c in context
    ):
        raise ImportValidationError("campo 'context' inválido")
    return Proposal(
        id=_id(obj, "id"),  # type: ignore[arg-type]
        created_at=_ts(obj, "created_at"),
        updated_at=_ts(obj, "updated_at"),
        instruction=_str(obj, "instruction", 4000),  # type: ignore[arg-type]
        provider=_str(obj, "provider", 50),  # type: ignore[arg-type]
        model=_str(obj, "model", 200),  # type: ignore[arg-type]
        diff=parsed.text,
        diff_sha256=sha256_text(parsed.text),
        files=parsed.files,
        commit_message=_str(obj, "commit_message", 2000),  # type: ignore[arg-type]
        status=status,
        base_commit=_sha(obj, "base_commit"),
        branch=_str(obj, "branch", 300, optional=True),
        commit_sha=_sha(obj, "commit_sha"),
        check_error=_str(obj, "check_error", 2000, optional=True),
        context=context,
        latency_ms=_int(obj, "latency_ms", optional=True),
        is_sample=_bool(obj, "is_sample"),
    )


def _parse_test_run(obj: Any) -> TestRun:
    if not isinstance(obj, dict):
        raise ImportValidationError("execução de teste não é um objeto")
    exit_code = _int(obj, "exit_code")
    return TestRun(
        id=_id(obj, "id"),  # type: ignore[arg-type]
        proposal_id=_id(obj, "proposal_id", optional=True),
        command=_str(obj, "command", 2000),  # type: ignore[arg-type]
        started_at=_ts(obj, "started_at"),
        duration_ms=_int(obj, "duration_ms"),  # type: ignore[arg-type]
        exit_code=exit_code,  # type: ignore[arg-type]
        passed=exit_code == 0,
        output_tail=_str(obj, "output_tail", 20_000),  # type: ignore[arg-type]
        is_sample=_bool(obj, "is_sample"),
    )


def _parse_event(obj: Any) -> Event:
    if not isinstance(obj, dict):
        raise ImportValidationError("evento não é um objeto")
    data = obj.get("data", {})
    if not isinstance(data, dict) or len(json.dumps(data)) > 50_000:
        raise ImportValidationError("campo 'data' do evento inválido")
    kind = _str(obj, "kind", 40)
    if not kind or not kind.replace("_", "").isalnum():
        raise ImportValidationError("tipo de evento inválido")
    return Event(
        id=_id(obj, "id"),  # type: ignore[arg-type]
        ts=_ts(obj, "ts"),
        kind=kind,
        summary=_str(obj, "summary", 1000),  # type: ignore[arg-type]
        proposal_id=_id(obj, "proposal_id", optional=True),
        data=data,
        is_sample=_bool(obj, "is_sample"),
    )


def load_export_file(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise ImportValidationError(f"arquivo não encontrado: {path}")
    if path.stat().st_size > MAX_IMPORT_BYTES:
        raise ImportValidationError("arquivo maior que 50 MB")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ImportValidationError(f"JSON inválido: {exc}") from exc
    if not isinstance(data, dict) or data.get("format") != FORMAT:
        raise ImportValidationError("não é uma exportação do Gitruvian")
    if data.get("version") != FORMAT_VERSION:
        raise ImportValidationError(f"versão de exportação não suportada: {data.get('version')!r}")
    for key in ("proposals", "test_runs", "events"):
        if not isinstance(data.get(key, []), list) or len(data.get(key, [])) > MAX_RECORDS:
            raise ImportValidationError(f"seção '{key}' inválida")
    return data


def import_data(store: Store, data: dict[str, Any]) -> ImportReport:
    """Importa registros válidos de forma idempotente (ids existentes são ignorados)."""
    report = ImportReport()
    known_ids = {r["id"] for r in store.conn.execute("SELECT id FROM proposals")}
    for index, raw in enumerate(data.get("proposals", [])):
        try:
            proposal = _parse_proposal(raw)
        except ImportValidationError as exc:
            report.skipped.append(f"proposta #{index + 1}: {exc}")
            continue
        if store.insert_proposal(proposal):
            report.inserted["proposals"] += 1
        else:
            report.duplicates += 1
        known_ids.add(proposal.id)
    for index, raw in enumerate(data.get("test_runs", [])):
        try:
            run = _parse_test_run(raw)
        except ImportValidationError as exc:
            report.skipped.append(f"teste #{index + 1}: {exc}")
            continue
        if run.proposal_id and run.proposal_id not in known_ids:
            run.proposal_id = None
        if store.insert_test_run(run):
            report.inserted["test_runs"] += 1
        else:
            report.duplicates += 1
    for index, raw in enumerate(data.get("events", [])):
        try:
            event = _parse_event(raw)
        except ImportValidationError as exc:
            report.skipped.append(f"evento #{index + 1}: {exc}")
            continue
        if event.proposal_id and event.proposal_id not in known_ids:
            event.proposal_id = None
        if store.insert_event(event):
            report.inserted["events"] += 1
        else:
            report.duplicates += 1
    settings = data.get("settings", {})
    if isinstance(settings, dict):
        for key, value in settings.items():
            if key in NON_IMPORTABLE_SETTINGS:
                report.skipped.append(f"configuração '{key}' não importada por segurança (executa comandos)")
            else:
                report.skipped.append(f"configuração desconhecida '{str(key)[:40]}' ignorada")
    store.add_event("import", "Importação concluída: " + ", ".join(f"{v} {k}" for k, v in report.inserted.items()),
                    data={**report.inserted, "duplicates": report.duplicates, "skipped": len(report.skipped)})
    return report
