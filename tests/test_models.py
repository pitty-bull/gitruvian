from __future__ import annotations

import sqlite3

import pytest

from gitruvian.models import (
    SCHEMA_VERSION,
    TRANSITIONS,
    AmbiguousId,
    InvalidTransition,
    NotFound,
    Proposal,
    Status,
    Store,
    connect,
    now_iso,
    sha256_text,
)

DIFF = "--- a/x.py\n+++ b/x.py\n@@ -1 +1 @@\n-a\n+b\n"


@pytest.fixture
def store():
    s = Store(connect(":memory:"))
    yield s
    s.close()


def _proposal(store: Store, **kw) -> Proposal:
    defaults = dict(instruction="mudar x", provider="manual", model="-", diff=DIFF, files=["x.py"],
                    commit_message="Muda x")
    defaults.update(kw)
    return store.add_proposal(**defaults)


def test_schema_version_and_tables(store):
    assert store.conn.execute("PRAGMA user_version").fetchone()[0] == SCHEMA_VERSION
    tables = {r[0] for r in store.conn.execute("SELECT name FROM sqlite_master WHERE type IN ('table')")}
    assert {"settings", "files", "chunks", "proposals", "test_runs", "events"} <= tables


def test_refuses_newer_schema(tmp_path):
    path = tmp_path / "db.sqlite"
    conn = sqlite3.connect(path)
    conn.execute(f"PRAGMA user_version = {SCHEMA_VERSION + 1}")
    conn.close()
    with pytest.raises(RuntimeError, match="mais nova"):
        Store.open(path)


def test_add_and_get_proposal_by_prefix(store):
    p = _proposal(store)
    assert p.status is Status.PROPOSED
    assert p.diff_sha256 == sha256_text(DIFF)
    got = store.get_proposal(p.id[:6])
    assert got.id == p.id and got.files == ["x.py"] and got.instruction == "mudar x"


def test_get_proposal_validates_id(store):
    with pytest.raises(NotFound):
        store.get_proposal("xyz")
    with pytest.raises(NotFound):
        store.get_proposal("' OR 1=1 --")
    with pytest.raises(NotFound):
        store.get_proposal("abcd")


def test_ambiguous_prefix(store):
    for suffix in ("1", "2"):
        ts = now_iso()
        store.insert_proposal(Proposal(
            id="abcd" + suffix * 28, created_at=ts, updated_at=ts, instruction="i", provider="manual", model="-",
            diff=DIFF, diff_sha256=sha256_text(DIFF), files=["x.py"], commit_message="m", status=Status.PROPOSED))
    with pytest.raises(AmbiguousId):
        store.get_proposal("abcd")
    assert store.get_proposal("abcd1").id.startswith("abcd1")


def test_insert_is_idempotent(store):
    p = _proposal(store)
    assert store.insert_proposal(p) is False


def test_happy_path_transitions(store):
    p = _proposal(store)
    p = store.transition(p.id, Status.APPLIED, base_commit="a" * 40, branch="main")
    assert p.status is Status.APPLIED and p.base_commit == "a" * 40 and p.branch == "main"
    p = store.transition(p.id, Status.TESTS_PASSED)
    p = store.transition(p.id, Status.COMMITTED, commit_sha="b" * 40)
    assert p.commit_sha == "b" * 40
    p = store.transition(p.id, Status.REVERTED)
    assert p.status is Status.REVERTED


@pytest.mark.parametrize(
    "path",
    [
        [Status.COMMITTED],
        [Status.TESTS_PASSED],
        [Status.REJECTED, Status.APPLIED],
        [Status.APPLIED, Status.TESTS_FAILED, Status.COMMITTED],
    ],
)
def test_invalid_transitions(store, path):
    p = _proposal(store)
    with pytest.raises(InvalidTransition):
        for status in path:
            store.transition(p.id, status)


def test_every_status_has_transition_rules():
    assert set(TRANSITIONS) == set(Status)
    assert TRANSITIONS[Status.REJECTED] == frozenset()


def test_transition_rejects_unknown_fields(store):
    p = _proposal(store)
    with pytest.raises(ValueError):
        store.transition(p.id, Status.APPLIED, diff="hack")


def test_list_and_counts(store):
    a = _proposal(store, instruction="a")
    _proposal(store, instruction="b")
    store.transition(a.id, Status.REJECTED)
    assert [p.instruction for p in store.list_proposals(Status.PROPOSED)] == ["b"]
    counts = store.status_counts()
    assert counts[Status.PROPOSED] == 1 and counts[Status.REJECTED] == 1


def test_settings_crud(store):
    assert store.get_setting("test_command") is None
    store.set_setting("test_command", "pytest")
    store.set_setting("test_command", "pytest -q")
    assert store.settings() == {"test_command": "pytest -q"}
    assert store.delete_setting("test_command") is True
    assert store.delete_setting("test_command") is False


def test_test_runs_and_events(store):
    p = _proposal(store)
    run = store.add_test_run(command="pytest", duration_ms=10, exit_code=1, output_tail="F", proposal_id=p.id)
    assert run.passed is False
    store.add_test_run(command="pytest", duration_ms=5, exit_code=0, output_tail=".", proposal_id=p.id)
    assert [r.passed for r in store.list_test_runs(p.id)] == [True, False]
    store.add_event("apply", "aplicado", proposal_id=p.id, data={"files": ["x.py"]})
    events = store.list_events(proposal_id=p.id)
    assert events[0].kind == "apply" and events[0].data == {"files": ["x.py"]}


def test_delete_samples_keeps_user_data(store):
    real = _proposal(store)
    sample = _proposal(store, is_sample=True)
    store.add_event("sample", "ex", proposal_id=sample.id, is_sample=True)
    store.add_event("propose", "real", proposal_id=real.id)
    store.add_test_run(command="t", duration_ms=1, exit_code=0, output_tail="", proposal_id=sample.id, is_sample=True)
    counts = store.delete_samples()
    assert counts == {"events": 1, "test_runs": 1, "proposals": 1}
    assert [p.id for p in store.list_proposals()] == [real.id]
    assert len(store.list_events()) == 1


def test_status_check_constraint(store):
    p = _proposal(store)
    with pytest.raises(sqlite3.IntegrityError):
        with store.conn:
            store.conn.execute("UPDATE proposals SET status = 'bogus' WHERE id = ?", (p.id,))
