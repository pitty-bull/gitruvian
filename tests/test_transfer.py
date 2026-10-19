from __future__ import annotations

import json

import pytest
from conftest import FIX_DIFF, make_repo

from gitruvian.app import open_app
from gitruvian.config import Settings
from gitruvian.models import Status, new_id, now_iso
from gitruvian.transfer import ImportValidationError, export_to_file, import_data, load_export_file
from gitruvian.workflow import apply_proposal, propose, run_proposal_tests


def test_export_import_roundtrip(app, tmp_path):
    p = propose(app, "Desconto", patch_text=FIX_DIFF).proposal
    apply_proposal(app, p.id)
    run_proposal_tests(app, p.id)
    out = tmp_path / "backup.json"
    counts = export_to_file(app.store, out)
    assert counts["proposals"] == 1 and counts["test_runs"] == 1
    data = json.loads(out.read_text(encoding="utf-8"))
    assert "test_command" not in data["settings"]

    other = open_app(make_repo(tmp_path / "outro"), create=True, settings=Settings())
    try:
        report = import_data(other.store, load_export_file(out))
        assert report.inserted == {"proposals": 1, "test_runs": 1, "events": counts["events"]}
        imported = other.store.get_proposal(p.id)
        assert imported.status is Status.TESTS_PASSED and imported.diff == p.diff
        again = import_data(other.store, load_export_file(out))
        assert again.inserted["proposals"] == 0 and again.duplicates >= 2
    finally:
        other.close()


def _record(**overrides):
    ts = now_iso()
    base = {"id": new_id(), "created_at": ts, "updated_at": ts, "instruction": "x", "provider": "manual",
            "model": "-", "diff": FIX_DIFF, "files": ["calc.py"], "commit_message": "m", "status": "proposed",
            "context": [], "is_sample": False}
    base.update(overrides)
    return base


def test_import_skips_malicious_records(app):
    data = {
        "format": "gitruvian-export",
        "version": 1,
        "settings": {"test_command": "rm -rf /"},
        "proposals": [
            _record(),
            _record(diff=FIX_DIFF.replace("calc.py", "../../etc/passwd")),
            _record(status="hacked"),
            _record(id="'; DROP TABLE proposals; --"),
            _record(instruction=123),
        ],
        "events": [{"id": new_id(), "ts": now_iso(), "kind": "x y", "summary": "s"}],
    }
    report = import_data(app.store, data)
    assert report.inserted["proposals"] == 1
    assert len(report.skipped) == 6
    assert app.store.get_setting("test_command") == "{python} -m unittest discover -s tests"


@pytest.mark.parametrize("content", ["não é json", json.dumps({"format": "outro"}),
                                     json.dumps({"format": "gitruvian-export", "version": 99})])
def test_load_rejects_invalid_files(tmp_path, content):
    path = tmp_path / "x.json"
    path.write_text(content, encoding="utf-8")
    with pytest.raises(ImportValidationError):
        load_export_file(path)
