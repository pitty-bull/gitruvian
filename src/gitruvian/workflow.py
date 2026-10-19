"""Ciclo principal: propor → validar → aplicar → testar → commitar/reverter, registrando tudo."""

from __future__ import annotations

from dataclasses import dataclass

from gitruvian import gitops
from gitruvian.app import App
from gitruvian.diffs import DiffValidationError, extract_commit_message, extract_diff, validate_diff
from gitruvian.indexer import ContextPack, build_context
from gitruvian.llm import SYSTEM_PROMPT, ModelError, Provider, build_user_prompt, get_provider
from gitruvian.logs import get_logger
from gitruvian.models import STATUS_LABELS, Proposal, Status, TestRun
from gitruvian.safety import clean_instruction, truncate
from gitruvian.testrunner import run_tests

TEST_COMMAND_KEY = "test_command"


class WorkflowError(RuntimeError):
    """Erro de fluxo com mensagem pronta para o usuário."""


@dataclass
class ProposeOutcome:
    proposal: Proposal
    context: ContextPack | None
    applies: bool


def get_test_command(app: App) -> str | None:
    return app.settings.test_command or app.store.get_setting(TEST_COMMAND_KEY)


def default_commit_message(instruction: str) -> str:
    first = instruction.strip().split("\n", 1)[0]
    return truncate(first, 72)


def propose(
    app: App,
    instruction: str,
    *,
    patch_text: str | None = None,
    files: list[str] | None = None,
    provider: Provider | None = None,
) -> ProposeOutcome:
    """Cria uma proposta a partir de um diff manual ou de uma chamada ao modelo."""
    log = get_logger()
    instruction = clean_instruction(instruction)
    context: ContextPack | None = None
    latency: int | None = None
    commit_msg: str | None = None
    if patch_text is not None:
        provider_name, model_name = "manual", "-"
        try:
            diff = extract_diff(patch_text)
        except DiffValidationError:
            diff = patch_text
    else:
        provider = provider or get_provider(app.settings)
        provider_name, model_name = provider.name, provider.model
        context = build_context(app.store, app.repo, instruction, app.settings.max_context_chars, files)
        log.info("model_call provider=%s model=%s context_files=%d context_chars=%d",
                 provider_name, model_name, len(context.entries), context.chars)
        try:
            reply = provider.complete(SYSTEM_PROMPT, build_user_prompt(instruction, context.text))
        except ModelError as exc:
            app.store.add_event("propose_failed", f"Falha ao consultar o modelo: {exc}",
                                data={"provider": provider_name, "model": model_name})
            raise
        latency, model_name = reply.latency_ms, reply.model
        commit_msg = extract_commit_message(reply.text)
        try:
            diff = extract_diff(reply.text)
        except DiffValidationError as exc:
            app.store.add_event("propose_failed", "O modelo não devolveu um diff utilizável",
                                data={"provider": provider_name, "model": model_name, "latency_ms": latency})
            log.info("model_reply_without_diff provider=%s latency_ms=%s", provider_name, latency)
            raise DiffValidationError(f"{exc}. Tente reformular ou indicar arquivos com --file.") from exc

    try:
        parsed = validate_diff(diff)
    except DiffValidationError as exc:
        app.store.add_event("propose_failed", f"Diff rejeitado na validação: {exc}",
                            data={"provider": provider_name, "model": model_name})
        log.info("diff_rejected provider=%s reason=%s", provider_name, exc)
        raise
    applies, check_error, _ = gitops.apply_check(app.repo, parsed.text)
    if not commit_msg or commit_msg.lower() == "none":
        commit_msg = default_commit_message(instruction)
    proposal = app.store.add_proposal(
        instruction=instruction,
        provider=provider_name,
        model=model_name,
        diff=parsed.text,
        files=parsed.files,
        commit_message=commit_msg,
        context=context.entries if context else [],
        latency_ms=latency,
        check_error=None if applies else truncate(check_error, 1000),
    )
    app.store.add_event(
        "propose",
        f"Proposta criada via {provider_name}: {len(parsed.files)} arquivo(s), +{parsed.additions} -{parsed.deletions}",
        proposal_id=proposal.id,
        data={
            "files": parsed.files,
            "additions": parsed.additions,
            "deletions": parsed.deletions,
            "provider": provider_name,
            "model": model_name,
            "latency_ms": latency,
            "context_files": [e["path"] for e in (context.entries if context else [])],
            "context_chars": context.chars if context else 0,
            "applies_cleanly": applies,
        },
    )
    log.info("proposal_created id=%s provider=%s files=%d applies=%s", proposal.short_id, provider_name,
             len(parsed.files), applies)
    return ProposeOutcome(proposal=proposal, context=context, applies=applies)


def _require(proposal: Proposal, allowed: set[Status], action: str) -> None:
    if proposal.status not in allowed:
        raise WorkflowError(
            f"não é possível {action}: a proposta {proposal.short_id} está '{STATUS_LABELS[proposal.status]}'"
        )


def apply_proposal(app: App, proposal_id: str, *, branch: str | None = None) -> Proposal:
    store, repo = app.store, app.repo
    p = store.get_proposal(proposal_id)
    _require(p, {Status.PROPOSED, Status.REVERTED}, "aplicar")
    dirty = gitops.dirty_paths(repo, p.files)
    if dirty:
        raise WorkflowError(
            "estes arquivos têm alterações não commitadas e seriam misturados ao diff: "
            + ", ".join(dirty) + ". Faça commit ou 'git stash' antes."
        )
    try:
        if branch:
            gitops.create_branch(repo, branch)
        gitops.apply_patch(repo, p.diff)
    except gitops.GitError as exc:
        store.add_event("apply_failed", f"Falha ao aplicar: {truncate(str(exc), 300)}", proposal_id=p.id,
                        is_sample=p.is_sample)
        raise WorkflowError(str(exc)) from exc
    p = store.transition(p.id, Status.APPLIED, base_commit=gitops.head_sha(repo),
                         branch=gitops.current_branch(repo))
    store.add_event("apply", f"Diff aplicado em {len(p.files)} arquivo(s) no branch {p.branch}",
                    proposal_id=p.id, data={"files": p.files, "base_commit": p.base_commit, "branch": p.branch},
                    is_sample=p.is_sample)
    get_logger().info("proposal_applied id=%s files=%d", p.short_id, len(p.files))
    return p


def run_proposal_tests(app: App, proposal_id: str | None = None) -> tuple[TestRun, Proposal | None]:
    store = app.store
    p = store.get_proposal(proposal_id) if proposal_id else None
    command = get_test_command(app)
    if not command:
        raise WorkflowError(
            'nenhum comando de testes configurado. Use: gitruvian config set test_command "python -m pytest -q"'
        )
    try:
        result = run_tests(command, app.repo, app.settings.test_timeout)
    except ValueError as exc:
        raise WorkflowError(str(exc)) from exc
    is_sample = bool(p and p.is_sample)
    run = store.add_test_run(command=command, duration_ms=result.duration_ms, exit_code=result.exit_code,
                             output_tail=result.output_tail, proposal_id=p.id if p else None, is_sample=is_sample)
    verdict = "passaram" if run.passed else f"falharam (código {run.exit_code})"
    if p and p.status in {Status.APPLIED, Status.TESTS_PASSED, Status.TESTS_FAILED}:
        p = store.transition(p.id, Status.TESTS_PASSED if run.passed else Status.TESTS_FAILED)
    store.add_event("test", f"Testes {verdict} em {run.duration_ms} ms", proposal_id=p.id if p else None,
                    data={"command": command, "exit_code": run.exit_code, "duration_ms": run.duration_ms,
                          "test_run": run.id}, is_sample=is_sample)
    get_logger().info("tests_run proposal=%s exit=%d duration_ms=%d", p.short_id if p else "-",
                      run.exit_code, run.duration_ms)
    return run, p


def commit_proposal(app: App, proposal_id: str, *, message: str | None = None,
                    allow_untested: bool = False) -> Proposal:
    store, repo = app.store, app.repo
    p = store.get_proposal(proposal_id)
    if p.status == Status.TESTS_FAILED:
        raise WorkflowError("os testes desta proposta falharam. Corrija e rode 'gitruvian test', ou reverta.")
    _require(p, {Status.APPLIED, Status.TESTS_PASSED}, "commitar")
    if p.status == Status.APPLIED and get_test_command(app) and not allow_untested:
        raise WorkflowError(f"rode os testes antes (gitruvian test {p.short_id}) ou use --sem-testes.")
    body = (message or p.commit_message).strip()
    if not body:
        raise WorkflowError("mensagem de commit vazia")
    full_message = f"{truncate(body, 2000)}\n\nGitruvian-Proposal: {p.id}\n"
    try:
        sha = gitops.commit(repo, full_message, p.files)
    except gitops.GitError as exc:
        store.add_event("commit_failed", f"Falha no commit: {truncate(str(exc), 300)}", proposal_id=p.id,
                        is_sample=p.is_sample)
        raise WorkflowError(str(exc)) from exc
    p = store.transition(p.id, Status.COMMITTED, commit_sha=sha)
    store.add_event("commit", f"Commit {sha[:10]} criado", proposal_id=p.id,
                    data={"commit_sha": sha, "files": p.files}, is_sample=p.is_sample)
    get_logger().info("proposal_committed id=%s sha=%s", p.short_id, sha[:10])
    return p


def revert_proposal(app: App, proposal_id: str) -> Proposal:
    store, repo = app.store, app.repo
    p = store.get_proposal(proposal_id)
    _require(p, {Status.APPLIED, Status.TESTS_PASSED, Status.TESTS_FAILED, Status.COMMITTED}, "reverter")
    data: dict[str, object] = {"files": p.files}
    try:
        if p.status == Status.COMMITTED and p.commit_sha:
            data["revert_commit"] = gitops.revert_commit(repo, p.commit_sha)
            summary = f"Commit {p.commit_sha[:10]} revertido por {str(data['revert_commit'])[:10]}"
        else:
            gitops.apply_patch(repo, p.diff, reverse=True)
            summary = "Diff desfeito na árvore de trabalho"
    except gitops.GitError as exc:
        store.add_event("revert_failed", f"Falha ao reverter: {truncate(str(exc), 300)}", proposal_id=p.id,
                        is_sample=p.is_sample)
        raise WorkflowError(str(exc)) from exc
    p = store.transition(p.id, Status.REVERTED)
    store.add_event("revert", summary, proposal_id=p.id, data=data, is_sample=p.is_sample)
    get_logger().info("proposal_reverted id=%s", p.short_id)
    return p


def reject_proposal(app: App, proposal_id: str) -> Proposal:
    p = app.store.get_proposal(proposal_id)
    _require(p, {Status.PROPOSED, Status.REVERTED}, "rejeitar")
    p = app.store.transition(p.id, Status.REJECTED)
    app.store.add_event("reject", "Proposta rejeitada", proposal_id=p.id, is_sample=p.is_sample)
    get_logger().info("proposal_rejected id=%s", p.short_id)
    return p
