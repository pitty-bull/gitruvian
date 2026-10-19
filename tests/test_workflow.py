"""Fluxo mais importante: propor → aplicar → testar → commitar/reverter, com registro."""

from __future__ import annotations

from dataclasses import replace

import pytest
from conftest import BAD_DIFF, CALC_BUGGY, FIX_DIFF, SECRET_VALUE, git, write

from gitruvian.config import Settings
from gitruvian.diffs import DiffValidationError
from gitruvian.indexer import index_repo
from gitruvian.llm import ModelReply, ModelUnavailable
from gitruvian.models import Status
from gitruvian.workflow import (
    WorkflowError,
    apply_proposal,
    commit_proposal,
    propose,
    reject_proposal,
    revert_proposal,
    run_proposal_tests,
)


class RecordingProvider:
    """Provedor de teste: devolve uma resposta fixa e guarda o prompt recebido."""

    name = "teste"
    model = "modelo-de-teste"

    def __init__(self, reply: str):
        self.reply = reply
        self.prompts: list[str] = []

    def complete(self, system: str, user: str) -> ModelReply:
        self.prompts.append(user)
        return ModelReply(text=self.reply, model=self.model, latency_ms=5)


def _kinds(app) -> list[str]:
    return [e.kind for e in reversed(app.store.list_events())]


def test_full_cycle_with_manual_patch(app):
    outcome = propose(app, "Aplicar desconto percentual", patch_text=FIX_DIFF)
    p = outcome.proposal
    assert outcome.applies and p.status is Status.PROPOSED and p.files == ["calc.py"] and p.provider == "manual"

    p = apply_proposal(app, p.short_id)
    assert p.status is Status.APPLIED and p.base_commit
    assert "subtotal * discount" in (app.repo / "calc.py").read_text(encoding="utf-8")

    run, p = run_proposal_tests(app, p.id)
    assert run.passed and p.status is Status.TESTS_PASSED

    p = commit_proposal(app, p.id)
    assert p.status is Status.COMMITTED
    message = git(app.repo, "log", "-1", "--format=%B")
    assert "Aplicar desconto percentual" in message and f"Gitruvian-Proposal: {p.id}" in message
    assert git(app.repo, "rev-parse", "HEAD").strip() == p.commit_sha
    assert _kinds(app) == ["propose", "apply", "test", "commit"]

    p = revert_proposal(app, p.id)
    assert p.status is Status.REVERTED
    assert (app.repo / "calc.py").read_text(encoding="utf-8").replace("\r\n", "\n") == CALC_BUGGY


def test_failing_tests_block_commit_and_revert_restores(app):
    p = propose(app, "Mudança errada", patch_text=BAD_DIFF).proposal
    apply_proposal(app, p.id)
    run, p = run_proposal_tests(app, p.id)
    assert not run.passed and p.status is Status.TESTS_FAILED and run.exit_code != 0
    assert "FAIL" in run.output_tail or "Error" in run.output_tail
    with pytest.raises(WorkflowError, match="falharam"):
        commit_proposal(app, p.id)
    revert_proposal(app, p.id)
    assert (app.repo / "calc.py").read_text(encoding="utf-8").replace("\r\n", "\n") == CALC_BUGGY
    assert app.store.get_proposal(p.id).status is Status.REVERTED


def test_commit_requires_tests_unless_explicit(app):
    p = propose(app, "Desconto", patch_text=FIX_DIFF).proposal
    apply_proposal(app, p.id)
    with pytest.raises(WorkflowError, match="testes"):
        commit_proposal(app, p.id)
    assert commit_proposal(app, p.id, allow_untested=True).status is Status.COMMITTED


def test_apply_refuses_dirty_files(app):
    p = propose(app, "Desconto", patch_text=FIX_DIFF).proposal
    write(app.repo / "calc.py", CALC_BUGGY + "# edição local\n")
    with pytest.raises(WorkflowError, match="não commitadas"):
        apply_proposal(app, p.id)
    assert app.store.get_proposal(p.id).status is Status.PROPOSED


def test_proposal_that_does_not_apply_is_recorded_but_flagged(app):
    stale = FIX_DIFF.replace("subtotal = sum(prices)", "subtotal = sum(p for p in prices)")
    outcome = propose(app, "Desconto", patch_text=stale)
    assert outcome.applies is False and outcome.proposal.check_error
    with pytest.raises(WorkflowError):
        apply_proposal(app, outcome.proposal.id)
    assert "apply_failed" in _kinds(app)


def test_reject_and_state_guards(app):
    p = propose(app, "Desconto", patch_text=FIX_DIFF).proposal
    reject_proposal(app, p.id)
    with pytest.raises(WorkflowError):
        apply_proposal(app, p.id)


def test_model_proposal_sends_context_without_secrets(app):
    index_repo(app.store, app.repo)
    provider = RecordingProvider(f"COMMIT: Corrige desconto percentual\n```diff\n{FIX_DIFF}```\n")
    outcome = propose(app, "corrigir o desconto percentual em total", provider=provider)
    assert outcome.applies and outcome.proposal.commit_message == "Corrige desconto percentual"
    assert outcome.proposal.provider == "teste" and outcome.proposal.latency_ms == 5
    prompt = provider.prompts[0]
    assert "### ARQUIVO: calc.py" in prompt and SECRET_VALUE not in prompt
    event = app.store.list_events(proposal_id=outcome.proposal.id)[0]
    assert event.data["context_files"] and "subtotal" not in str(event.data)


def test_model_reply_without_diff_is_recorded_as_failure(app):
    provider = RecordingProvider("Desculpe, não consigo.")
    with pytest.raises(DiffValidationError):
        propose(app, "algo impossível", provider=provider)
    assert app.store.list_proposals() == []
    assert _kinds(app) == ["propose_failed"]


def test_model_cannot_touch_protected_paths(app):
    evil = FIX_DIFF.replace("calc.py", ".git/hooks/post-checkout")
    with pytest.raises(DiffValidationError):
        propose(app, "x" * 5, provider=RecordingProvider(f"```diff\n{evil}```"))


def test_degraded_mode_without_model(app):
    with pytest.raises(ModelUnavailable, match="--patch-file"):
        propose(app, "corrigir algo")


def test_remote_provider_requires_explicit_consent(app):
    app.settings = replace(Settings(), provider="anthropic", model="claude-opus-5-5", anthropic_api_key="k")
    with pytest.raises(ModelUnavailable, match="GITRUVIAN_ALLOW_REMOTE"):
        propose(app, "corrigir algo")


def test_log_has_metadata_only(app):
    index_repo(app.store, app.repo)
    provider = RecordingProvider(f"```diff\n{FIX_DIFF}```")
    p = propose(app, "corrigir o desconto percentual", provider=provider).proposal
    apply_proposal(app, p.id)
    run_proposal_tests(app, p.id)
    for handler in __import__("logging").getLogger("gitruvian").handlers:
        handler.flush()
    log = (app.data_dir / "gitruvian.log").read_text(encoding="utf-8")
    assert "proposal_created" in log and "tests_run" in log
    for forbidden in ("subtotal", "def total", SECRET_VALUE, "desconto percentual"):
        assert forbidden not in log


def test_apply_and_revert_with_crlf_worktree(app):
    """Clones no Windows costumam ter CRLF na árvore de trabalho; diffs de modelos vêm em LF."""
    git(app.repo, "config", "core.autocrlf", "true")
    (app.repo / "calc.py").unlink()
    git(app.repo, "checkout", "--", "calc.py")
    assert b"\r\n" in (app.repo / "calc.py").read_bytes()
    outcome = propose(app, "Desconto", patch_text=FIX_DIFF)
    assert outcome.applies
    p = apply_proposal(app, outcome.proposal.id)
    run, p = run_proposal_tests(app, p.id)
    assert run.passed
    assert git(app.repo, "diff", "--numstat").split() == ["1", "1", "calc.py"]
    revert_proposal(app, p.id)
    assert git(app.repo, "status", "--porcelain", "--", "calc.py") == ""
