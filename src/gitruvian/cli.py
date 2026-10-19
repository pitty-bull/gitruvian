"""Interface de linha de comando (Typer). Códigos de saída: 0 ok · 1 falha · 2 entrada inválida · 3 sem modelo."""

from __future__ import annotations

import functools
import platform
import sys
from pathlib import Path
from typing import Annotated, Any, Callable, Optional

import typer
from rich import box
from rich.markup import escape
from rich.prompt import Confirm, Prompt
from rich.table import Table
from rich.text import Text

from gitruvian import __version__, gitops, ui
from gitruvian.app import NotInitialized, open_app
from gitruvian.config import ConfigError, load_settings
from gitruvian.diffs import DiffValidationError
from gitruvian.indexer import index_repo, search
from gitruvian.llm import ModelError, ModelUnavailable, describe_provider
from gitruvian.models import STATUS_LABELS, AmbiguousId, InvalidTransition, NotFound, Proposal, Status
from gitruvian.safety import UnsafePathError
from gitruvian.sample import SampleError, create_sample_repo, remove_sample_repo
from gitruvian.testrunner import detect_test_command
from gitruvian.transfer import ImportValidationError, export_to_file, import_data, load_export_file
from gitruvian.workflow import (
    TEST_COMMAND_KEY,
    WorkflowError,
    apply_proposal,
    commit_proposal,
    propose,
    reject_proposal,
    revert_proposal,
    get_test_command,
    run_proposal_tests,
)

app = typer.Typer(
    name="gitruvian",
    help="Assistente Git local e privado: indexa o código, propõe diffs, roda testes e registra cada alteração.",
    invoke_without_command=True,
    rich_markup_mode=None,
    pretty_exceptions_enable=False,
    add_completion=False,
)
config_app = typer.Typer(help="Configurações do repositório (guardadas no banco local).", rich_markup_mode=None)
sample_app = typer.Typer(help="Dados de exemplo fictícios (fáceis de apagar).", rich_markup_mode=None)
app.add_typer(config_app, name="config")
app.add_typer(sample_app, name="sample")

CONFIG_KEYS = {TEST_COMMAND_KEY: "Comando de testes (ex.: python -m pytest -q). {python} = interpretador atual."}

STATUS_STYLE = {
    Status.PROPOSED: "warn",
    Status.APPLIED: "info",
    Status.TESTS_PASSED: "ok",
    Status.TESTS_FAILED: "err",
    Status.COMMITTED: "ok",
    Status.REVERTED: "muted",
    Status.REJECTED: "muted",
}


class _State:
    repo: Path | None = None


state = _State()


# ------------------------------------------------------------------ utilidades
def handle_errors(func: Callable[..., Any]) -> Callable[..., Any]:
    """Converte exceções conhecidas em mensagens claras e códigos de saída estáveis."""

    @functools.wraps(func)
    def wrapper(*args: Any, **kwargs: Any) -> Any:
        try:
            return func(*args, **kwargs)
        except typer.Exit:
            raise
        except NotInitialized as exc:
            ui.error(str(exc), "gitruvian init")
            raise typer.Exit(2) from exc
        except ConfigError as exc:
            ui.error(f"configuração inválida: {exc}", "revise o arquivo .env (veja .env.example)")
            raise typer.Exit(2) from exc
        except (NotFound, AmbiguousId) as exc:
            ui.error(str(exc.args[0] if exc.args else exc), "liste as propostas com: gitruvian proposals")
            raise typer.Exit(2) from exc
        except (DiffValidationError, UnsafePathError, ImportValidationError, InvalidTransition) as exc:
            ui.error(f"entrada rejeitada: {exc}")
            raise typer.Exit(2) from exc
        except ModelUnavailable as exc:
            ui.error(f"modelo indisponível: {exc}",
                     "use --patch-file para trazer um diff próprio, ou configure GITRUVIAN_PROVIDER no .env")
            raise typer.Exit(3) from exc
        except ModelError as exc:
            ui.error(f"falha do modelo: {exc}")
            raise typer.Exit(1) from exc
        except (WorkflowError, gitops.GitError, SampleError) as exc:
            ui.error(str(exc))
            raise typer.Exit(1) from exc
        except ValueError as exc:
            ui.error(f"entrada inválida: {exc}")
            raise typer.Exit(2) from exc
        except KeyboardInterrupt as exc:
            ui.warn("operação cancelada pelo usuário")
            raise typer.Exit(130) from exc

    return wrapper


def _app(create: bool = False):  # noqa: ANN202
    return open_app(state.repo, create=create)


def _status_text(p: Proposal) -> Text:
    return Text(STATUS_LABELS[p.status], style=STATUS_STYLE[p.status])


def _proposal_table(proposals: list[Proposal]) -> Table:
    table = Table(box=box.SIMPLE_HEAD, show_lines=False, header_style="label")
    table.add_column("ID")
    table.add_column("Estado")
    table.add_column("Origem")
    table.add_column("Arquivos", justify="right")
    table.add_column("Pedido")
    table.add_column("Criada em (UTC)")
    for p in proposals:
        instruction = p.instruction.split("\n", 1)[0]
        if p.is_sample and not instruction.startswith("[EXEMPLO]"):
            instruction = "[EXEMPLO] " + instruction
        table.add_row(
            Text(p.short_id), _status_text(p), Text(p.provider), str(len(p.files)),
            Text(instruction[:70] + ("…" if len(instruction) > 70 else "")), Text(p.created_at[:19].replace("T", " ")),
        )
    return table


def _print_test_result(run) -> None:  # noqa: ANN001
    if run.passed:
        ui.ok(f"testes passaram em {run.duration_ms} ms ({run.command})")
    else:
        ui.error(f"testes falharam (código {run.exit_code}) em {run.duration_ms} ms")
        ui.console.print(Text(run.output_tail))


def _next_hint(p: Proposal) -> str:
    return {
        Status.PROPOSED: f"gitruvian apply {p.short_id}",
        Status.APPLIED: f"gitruvian test {p.short_id}",
        Status.TESTS_PASSED: f"gitruvian accept {p.short_id}",
        Status.TESTS_FAILED: f"gitruvian revert {p.short_id}",
        Status.COMMITTED: "gitruvian history",
        Status.REVERTED: f"gitruvian reject {p.short_id}  (ou apply de novo)",
        Status.REJECTED: 'gitruvian propose "..."',
    }[p.status]


# ------------------------------------------------------------------- callback
@app.callback()
def main_callback(
    ctx: typer.Context,
    repo: Annotated[Optional[Path], typer.Option("--repo", "-C", help="Pasta do repositório (padrão: pasta atual).")] = None,
    no_color: Annotated[bool, typer.Option("--no-color", help="Desativa cores (também respeita NO_COLOR).")] = False,
    version: Annotated[bool, typer.Option("--version", help="Mostra a versão e sai.")] = False,
) -> None:
    ui.configure(no_color=no_color)
    state.repo = repo
    if version:
        ui.console.print(f"gitruvian {__version__}")
        raise typer.Exit()
    if ctx.invoked_subcommand is None:
        dashboard()


@handle_errors
def dashboard() -> None:
    """Painel inicial: estado do repositório, do índice, do modelo e das propostas."""
    try:
        a = _app()
    except gitops.GitError:
        ui.empty("Esta pasta não é um repositório git.",
                 "gitruvian sample create ./loja-exemplo   (cria um repositório de exemplo)")
        return
    except NotInitialized:
        ui.empty("Repositório encontrado, mas o Gitruvian ainda não foi inicializado aqui.", "gitruvian init")
        return
    try:
        stats = a.store.index_stats()
        changes = gitops.status(a.repo)
        label, explanation = describe_provider(a.settings)
        ui.console.print(f"[label]Gitruvian {__version__}[/label] — {escape(str(a.repo))}")
        ui.console.print(f"  Branch: {escape(gitops.current_branch(a.repo))} · alterações locais: {len(changes)}")
        ui.console.print(f"  Índice: {stats['files']} arquivos, {stats['chunks']} trechos"
                         + (f" (atualizado {stats['last_indexed'][:19].replace('T', ' ')} UTC)" if stats["last_indexed"] else " (vazio)"))
        ui.console.print(f"  Modelo: {escape(label)}")
        if a.settings.provider == "none":
            ui.console.print(f"  {escape(explanation)}")
        ui.console.print(f"  Testes: {escape(get_test_command(a) or 'não configurado')}")
        proposals = a.store.list_proposals(limit=5)
        ui.heading("Propostas recentes")
        if not proposals:
            if stats["files"] == 0:
                ui.empty("Nenhuma proposta ainda e o índice está vazio.", "gitruvian index")
            else:
                ui.empty("Nenhuma proposta ainda.", 'gitruvian go "descreva a alteração"')
        else:
            ui.console.print(_proposal_table(proposals))
            ui.console.print(f"  Próximo passo sugerido: [key]{escape(_next_hint(proposals[0]))}[/key]")
        ui.console.print("\nAjuda completa: gitruvian --help · menu interativo: gitruvian ui")
    finally:
        a.close()


# ------------------------------------------------------------------ setup/info
@app.command()
@handle_errors
def init(
    test_command: Annotated[Optional[str], typer.Option("--test-command", "-t", help="Comando de testes do projeto.")] = None,
) -> None:
    """Inicializa o Gitruvian no repositório atual (cria .gitruvian/ local e ignorado pelo git)."""
    a = _app(create=True)
    try:
        command = test_command or a.store.get_setting(TEST_COMMAND_KEY) or detect_test_command(a.repo)
        if command:
            a.store.set_setting(TEST_COMMAND_KEY, command)
        a.store.add_event("init", "Gitruvian inicializado", data={"test_command_set": bool(command)})
        ui.ok(f"inicializado em {a.repo}")
        ui.console.print(f"  Dados locais: {escape(str(a.db_path))} (fora do controle de versão)")
        ui.console.print(f"  Testes: {escape(command or 'não detectado — gitruvian config set test_command \"...\"')}")
        label, explanation = describe_provider(a.settings)
        ui.console.print(f"  Modelo: {escape(label)}")
        ui.console.print(f"  {escape(explanation)}")
        ui.console.print("  Próximo passo: [key]gitruvian index[/key]")
    finally:
        a.close()


@app.command()
@handle_errors
def doctor() -> None:
    """Verifica o ambiente e explica o modo de operação (incluindo o modo degradado)."""
    problems = 0
    py_ok = sys.version_info[:2] == (3, 12)
    (ui.ok if py_ok else ui.warn)(f"Python {platform.python_version()}" + ("" if py_ok else " (recomendado: 3.12)"))
    if gitops.git_available():
        ui.ok(gitops.run_git(Path.cwd(), "--version").stdout.strip())
    else:
        ui.error("git não encontrado no PATH", "instale o git")
        problems += 1
    settings = load_settings()
    ui.info("arquivos .env lidos: " + (", ".join(settings.env_files) or "nenhum (usando só variáveis de ambiente)"))
    label, explanation = describe_provider(settings)
    (ui.warn if "indisponível" in label or settings.provider == "none" else ui.ok)(f"modelo: {label}")
    ui.console.print(f"  {escape(explanation)}")
    try:
        a = _app()
    except (gitops.GitError, NotInitialized) as exc:
        ui.warn(str(exc))
        raise typer.Exit(1 if problems else 0)
    try:
        ui.ok(f"banco: {a.db_path}")
        stats = a.store.index_stats()
        (ui.ok if stats["files"] else ui.warn)(f"índice: {stats['files']} arquivos, {stats['chunks']} trechos")
        command = get_test_command(a)
        (ui.ok if command else ui.warn)(f"testes: {command or 'não configurado'}")
    finally:
        a.close()
    raise typer.Exit(1 if problems else 0)


# ---------------------------------------------------------------- índice/busca
@app.command()
@handle_errors
def index(full: Annotated[bool, typer.Option("--full", help="Reconstrói o índice do zero.")] = False) -> None:
    """Indexa o código (incremental). Arquivos sensíveis (.env, chaves) nunca são indexados."""
    a = _app()
    try:
        with ui.loading("Indexando arquivos"):
            result = index_repo(a.store, a.repo, full=full)
        a.store.add_event("index", f"Índice atualizado: +{result.added} ~{result.updated} -{result.removed}",
                          data={"added": result.added, "updated": result.updated, "removed": result.removed,
                                "unchanged": result.unchanged, "skipped": result.skipped})
        if result.scanned == 0:
            ui.empty("Nenhum arquivo encontrado no repositório.", "adicione arquivos e rode gitruvian index")
            return
        ui.ok(f"{result.scanned} arquivos verificados: {result.added} novos, {result.updated} atualizados, "
              f"{result.unchanged} sem mudança, {result.removed} removidos; {result.chunks} trechos no índice")
        skipped = ", ".join(f"{v} {k}" for k, v in result.skipped.items() if v)
        if skipped:
            ui.info(f"ignorados: {skipped}")
    finally:
        a.close()


@app.command("search")
@handle_errors
def search_cmd(
    query: Annotated[str, typer.Argument(help="Termos a buscar no código.")],
    limit: Annotated[int, typer.Option("--limit", "-n", min=1, max=100)] = 10,
) -> None:
    """Busca local no índice (BM25)."""
    a = _app()
    try:
        if a.store.index_stats()["files"] == 0:
            ui.empty("O índice está vazio.", "gitruvian index")
            raise typer.Exit(0)
        hits = search(a.store, query, limit)
        if not hits:
            ui.empty(f"Nada encontrado para: {query}", "tente outros termos ou rode gitruvian index")
            return
        table = Table(box=box.SIMPLE_HEAD, header_style="label")
        table.add_column("Arquivo")
        table.add_column("Linhas")
        table.add_column("Trecho")
        for h in hits:
            table.add_row(Text(h.path), f"{h.start_line}-{h.end_line}", Text(" ".join(h.snippet.split())[:120]))
        ui.console.print(table)
    finally:
        a.close()


# ------------------------------------------------------------------ git básico
@app.command()
@handle_errors
def status() -> None:
    """Mostra o branch atual e os arquivos alterados."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    ui.console.print(f"Branch: [label]{escape(gitops.current_branch(repo))}[/label]")
    entries = gitops.status(repo)
    if not entries:
        ui.ok("árvore de trabalho limpa — nada para commitar")
        return
    table = Table(box=box.SIMPLE_HEAD, header_style="label")
    table.add_column("Código")
    table.add_column("Estado")
    table.add_column("Arquivo")
    for e in entries:
        table.add_row(Text(e.code), Text(e.label), Text(e.path))
    ui.console.print(table)


@app.command("log")
@handle_errors
def log_cmd(
    limit: Annotated[int, typer.Option("--limit", "-n", min=1, max=500)] = 20,
    graph: Annotated[bool, typer.Option("--graph", "-g", help="Mostra o grafo de todos os branches.")] = False,
) -> None:
    """Histórico de commits (lista ou grafo)."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    if graph:
        text = gitops.graph(repo, limit)
        if not text:
            ui.empty("Ainda não há commits.", 'gitruvian commit -a -m "primeiro commit"')
        ui.console.print(Text(text))
        return
    commits = gitops.log(repo, limit)
    if not commits:
        ui.empty("Ainda não há commits.", 'gitruvian commit -a -m "primeiro commit"')
        return
    table = Table(box=box.SIMPLE_HEAD, header_style="label")
    for col in ("Commit", "Data", "Autor", "Mensagem", "Refs"):
        table.add_column(col)
    for c in commits:
        table.add_row(c.sha[:10], c.date, Text(c.author), Text(c.subject), Text(c.refs))
    ui.console.print(table)


@app.command("diff")
@handle_errors
def diff_cmd(
    paths: Annotated[Optional[list[str]], typer.Argument(help="Limita a estes caminhos.")] = None,
    staged: Annotated[bool, typer.Option("--staged", help="Mostra o que já está preparado.")] = False,
) -> None:
    """Mostra o diff das alterações locais."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    text = gitops.diff_text(repo, staged=staged, paths=paths)
    if not text.strip():
        ui.info("nenhuma diferença" + (" preparada" if staged else " não preparada"))
        return
    ui.show_diff(text)


@app.command()
@handle_errors
def stage(paths: Annotated[list[str], typer.Argument(help="Arquivos a preparar ('.' para todos).")]) -> None:
    """Prepara arquivos para o próximo commit (git add)."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    gitops.stage(repo, paths)
    ui.ok(f"preparado(s): {', '.join(paths)}")


@app.command()
@handle_errors
def unstage(paths: Annotated[list[str], typer.Argument(help="Arquivos a tirar da preparação.")]) -> None:
    """Remove arquivos da preparação, mantendo as alterações."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    gitops.unstage(repo, paths)
    ui.ok(f"removido(s) da preparação: {', '.join(paths)}")


@app.command()
@handle_errors
def commit(
    message: Annotated[str, typer.Option("--message", "-m", help="Mensagem do commit.")],
    all_: Annotated[bool, typer.Option("--all", "-a", help="Prepara todas as alterações antes.")] = False,
) -> None:
    """Cria um commit com o que está preparado."""
    if not message.strip():
        raise ValueError("a mensagem do commit não pode ser vazia")
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    if all_:
        gitops.stage(repo, ["."])
    if not any(e.staged for e in gitops.status(repo)):
        ui.empty("Nada preparado para commit.", "gitruvian stage <arquivo>  ou  gitruvian commit -a -m ...")
        raise typer.Exit(1)
    sha = gitops.commit(repo, message.strip() + "\n")
    ui.ok(f"commit {sha[:10]} criado")


@app.command("branches")
@handle_errors
def branches_cmd() -> None:
    """Lista os branches locais (o atual é marcado com '*' e 'atual')."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    items = gitops.branches(repo)
    if not items:
        ui.empty("Nenhum branch ainda (repositório sem commits).", 'gitruvian commit -a -m "primeiro commit"')
        return
    for name, current in items:
        ui.console.print(Text(f"{'*' if current else ' '} {name}" + ("  (atual)" if current else "")))


@app.command()
@handle_errors
def switch(
    name: Annotated[str, typer.Argument(help="Nome do branch.")],
    create: Annotated[bool, typer.Option("--create", "-c", help="Cria o branch.")] = False,
) -> None:
    """Troca de branch (ou cria um novo com --create)."""
    repo = gitops.find_repo_root(state.repo or Path.cwd())
    (gitops.create_branch if create else gitops.switch)(repo, name)
    ui.ok(f"agora no branch {name}")


# ----------------------------------------------------------- ciclo principal
def _read_patch_file(path: Path) -> str:
    if not path.is_file():
        raise ValueError(f"arquivo não encontrado: {path}")
    if path.stat().st_size > 400_000:
        raise ValueError("arquivo de patch maior que 400 KB")
    return path.read_text(encoding="utf-8", errors="replace")


def _show_proposal_created(outcome) -> None:  # noqa: ANN001
    p = outcome.proposal
    ui.show_diff(p.diff)
    ui.ok(f"proposta {p.short_id} registrada ({len(p.files)} arquivo(s): {', '.join(p.files)})")
    if outcome.context is not None:
        ui.info(f"contexto enviado ao modelo: {len(outcome.context.entries)} arquivo(s), "
                f"{outcome.context.chars} caracteres; resposta em {p.latency_ms} ms")
    if outcome.applies:
        ui.ok("o diff aplica de forma limpa no estado atual")
    else:
        ui.warn(f"o diff NÃO aplica no estado atual: {p.check_error}")


@app.command("propose")
@handle_errors
def propose_cmd(
    instruction: Annotated[str, typer.Argument(help="O que deve ser alterado (em linguagem natural).")],
    files: Annotated[Optional[list[str]], typer.Option("--file", "-f", help="Arquivo a incluir no contexto (repetível).")] = None,
    patch_file: Annotated[Optional[Path], typer.Option("--patch-file", "-p", help="Usa este diff em vez de chamar o modelo.")] = None,
) -> None:
    """Gera (ou importa de --patch-file) um diff, valida e registra como proposta."""
    a = _app()
    try:
        patch_text = _read_patch_file(patch_file) if patch_file else None
        with ui.loading("Validando diff" if patch_text is not None else "Consultando o modelo"):
            outcome = propose(a, instruction, patch_text=patch_text, files=files)
        _show_proposal_created(outcome)
        ui.console.print(f"Próximo passo: [key]gitruvian apply {outcome.proposal.short_id}[/key]")
    finally:
        a.close()


@app.command()
@handle_errors
def proposals(
    status_filter: Annotated[Optional[str], typer.Option("--status", "-s", help="Filtra por estado: " + ", ".join(s.value for s in Status))] = None,
    limit: Annotated[int, typer.Option("--limit", "-n", min=1, max=1000)] = 30,
) -> None:
    """Lista as propostas registradas."""
    status_value = None
    if status_filter:
        try:
            status_value = Status(status_filter)
        except ValueError as exc:
            raise ValueError(f"estado inválido: use {', '.join(s.value for s in Status)}") from exc
    a = _app()
    try:
        items = a.store.list_proposals(status_value, limit)
        if not items:
            ui.empty("Nenhuma proposta" + (f" com estado {status_filter}." if status_filter else " ainda."),
                     'gitruvian propose "descreva a alteração"  (ou --patch-file arquivo.diff)')
            return
        ui.console.print(_proposal_table(items))
    finally:
        a.close()


@app.command()
@handle_errors
def show(
    proposal_id: Annotated[str, typer.Argument(help="ID (ou prefixo) da proposta.")],
    raw: Annotated[bool, typer.Option("--raw", help="Imprime só o diff (útil para salvar como .patch).")] = False,
) -> None:
    """Mostra detalhes, testes, eventos e o diff de uma proposta."""
    a = _app()
    try:
        p = a.store.get_proposal(proposal_id)
        if raw:
            # Bytes crus: evita que o Windows converta LF em CRLF e estrague o patch.
            sys.stdout.flush()
            sys.stdout.buffer.write(p.diff.encode("utf-8"))
            sys.stdout.buffer.flush()
            return
        ui.console.print(Text.assemble(("Proposta ", "label"), (p.id, "label"), "  ", _status_text(p)))
        if p.is_sample:
            ui.console.print("[warn]DADOS DE EXEMPLO[/warn] (fictícios — apague com: gitruvian sample clear)")
        rows = [
            ("Pedido", p.instruction), ("Origem", f"{p.provider} · {p.model}"), ("Criada", p.created_at),
            ("Atualizada", p.updated_at), ("Arquivos", ", ".join(p.files)), ("Mensagem de commit", p.commit_message),
            ("Base", p.base_commit or "-"), ("Branch", p.branch or "-"), ("Commit", p.commit_sha or "-"),
        ]
        if p.context:
            rows.append(("Contexto enviado", ", ".join(f"{c['path']} ({c['lines']})" for c in p.context)))
        if p.check_error:
            rows.append(("Verificação", p.check_error))
        table = Table(box=box.SIMPLE, show_header=False)
        table.add_column(style="label")
        table.add_column()
        for k, v in rows:
            table.add_row(k, Text(str(v)))
        ui.console.print(table)
        runs = a.store.list_test_runs(p.id, limit=5)
        if runs:
            ui.heading("Execuções de teste")
            for r in runs:
                verdict = Text("PASSOU", style="ok") if r.passed else Text(f"FALHOU ({r.exit_code})", style="err")
                ui.console.print(Text.assemble(f"  {r.started_at[:19].replace('T', ' ')}  ", verdict, f"  {r.duration_ms} ms"))
        ui.heading("Eventos")
        for e in reversed(a.store.list_events(limit=50, proposal_id=p.id)):
            ui.console.print(Text(f"  {e.ts[:19].replace('T', ' ')}  {e.kind:<14} {e.summary}"))
        ui.heading("Diff")
        ui.show_diff(p.diff)
        ui.console.print(f"Próximo passo: [key]{escape(_next_hint(p))}[/key]")
    finally:
        a.close()


@app.command("apply")
@handle_errors
def apply_cmd(
    proposal_id: Annotated[str, typer.Argument(help="ID (ou prefixo) da proposta.")],
    branch: Annotated[Optional[str], typer.Option("--branch", "-b", help="Cria este branch antes de aplicar.")] = None,
    run_tests: Annotated[bool, typer.Option("--test/--no-test", help="Roda os testes depois de aplicar.")] = True,
    do_commit: Annotated[bool, typer.Option("--commit", help="Commita se os testes passarem.")] = False,
) -> None:
    """Aplica o diff de uma proposta na árvore de trabalho e (por padrão) roda os testes."""
    a = _app()
    try:
        with ui.loading("Aplicando diff"):
            p = apply_proposal(a, proposal_id, branch=branch)
        ui.ok(f"proposta {p.short_id} aplicada em {', '.join(p.files)}")
        if run_tests and get_test_command(a):
            with ui.loading("Rodando testes"):
                run, p = run_proposal_tests(a, p.id)
            _print_test_result(run)
            if not run.passed:
                ui.console.print(f"Para desfazer: [key]gitruvian revert {p.short_id}[/key]")
                raise typer.Exit(1)
        elif run_tests:
            ui.warn('sem comando de testes configurado — use: gitruvian config set test_command "..."')
        if do_commit:
            p = commit_proposal(a, p.id, allow_untested=not run_tests)
            ui.ok(f"commit {p.commit_sha[:10]} criado")
        else:
            ui.console.print(f"Próximo passo: [key]{escape(_next_hint(p))}[/key]")
    finally:
        a.close()


@app.command("test")
@handle_errors
def test_cmd(proposal_id: Annotated[Optional[str], typer.Argument(help="Associa o resultado a esta proposta.")] = None) -> None:
    """Roda o comando de testes do repositório e registra o resultado."""
    a = _app()
    try:
        with ui.loading("Rodando testes"):
            run, p = run_proposal_tests(a, proposal_id)
        _print_test_result(run)
        if p:
            ui.console.print(f"Proposta {p.short_id}: {escape(p.status_label)} · próximo passo: [key]{escape(_next_hint(p))}[/key]")
        if not run.passed:
            raise typer.Exit(1)
    finally:
        a.close()


@app.command()
@handle_errors
def accept(
    proposal_id: Annotated[str, typer.Argument(help="ID (ou prefixo) da proposta.")],
    message: Annotated[Optional[str], typer.Option("--message", "-m", help="Substitui a mensagem sugerida.")] = None,
    untested: Annotated[bool, typer.Option("--sem-testes", help="Permite commitar sem rodar os testes.")] = False,
) -> None:
    """Commita somente os arquivos da proposta (com o id dela no rodapé do commit)."""
    a = _app()
    try:
        p = commit_proposal(a, proposal_id, message=message, allow_untested=untested)
        ui.ok(f"commit {p.commit_sha[:10]} criado para a proposta {p.short_id}")
    finally:
        a.close()


@app.command()
@handle_errors
def revert(proposal_id: Annotated[str, typer.Argument(help="ID (ou prefixo) da proposta.")]) -> None:
    """Desfaz uma proposta aplicada (ou cria um commit de reversão se já foi commitada)."""
    a = _app()
    try:
        p = revert_proposal(a, proposal_id)
        ui.ok(f"proposta {p.short_id} revertida")
    finally:
        a.close()


@app.command()
@handle_errors
def reject(proposal_id: Annotated[str, typer.Argument(help="ID (ou prefixo) da proposta.")]) -> None:
    """Marca uma proposta pendente como rejeitada (o registro é mantido)."""
    a = _app()
    try:
        p = reject_proposal(a, proposal_id)
        ui.ok(f"proposta {p.short_id} rejeitada")
    finally:
        a.close()


@app.command()
@handle_errors
def go(
    instruction: Annotated[str, typer.Argument(help="O que deve ser alterado.")],
    files: Annotated[Optional[list[str]], typer.Option("--file", "-f", help="Arquivo a incluir no contexto (repetível).")] = None,
    patch_file: Annotated[Optional[Path], typer.Option("--patch-file", "-p", help="Usa este diff em vez do modelo.")] = None,
    yes: Annotated[bool, typer.Option("--yes", "-y", help="Não pergunta: aplica e, se falhar, reverte.")] = False,
    do_commit: Annotated[bool, typer.Option("--commit", help="Commita automaticamente se os testes passarem.")] = False,
    branch: Annotated[Optional[str], typer.Option("--branch", "-b", help="Cria este branch antes de aplicar.")] = None,
) -> None:
    """Ciclo completo: indexar → propor → revisar → aplicar → testar → commitar ou reverter."""
    a = _app()
    try:
        total = 5
        ui.step(1, total, "atualizando o índice")
        with ui.loading("Indexando"):
            result = index_repo(a.store, a.repo)
        ui.ok(f"{result.added + result.updated} arquivo(s) reindexado(s), {result.chunks} trechos")

        ui.step(2, total, "gerando a proposta" if patch_file is None else "validando o diff informado")
        patch_text = _read_patch_file(patch_file) if patch_file else None
        with ui.loading("Consultando o modelo" if patch_text is None else "Validando diff"):
            outcome = propose(a, instruction, patch_text=patch_text, files=files)
        _show_proposal_created(outcome)
        p = outcome.proposal
        if not outcome.applies:
            ui.console.print(f"A proposta ficou registrada. Veja com: [key]gitruvian show {p.short_id}[/key]")
            raise typer.Exit(1)

        ui.step(3, total, "revisão")
        if not yes and not Confirm.ask("Aplicar esta proposta na árvore de trabalho?", default=False, console=ui.console):
            ui.info(f"nada foi alterado. A proposta {p.short_id} continua pendente.")
            return
        p = apply_proposal(a, p.id, branch=branch)
        ui.ok(f"aplicada em {', '.join(p.files)}")

        ui.step(4, total, "testes")
        passed = True
        if get_test_command(a):
            with ui.loading("Rodando testes"):
                run, p = run_proposal_tests(a, p.id)
            _print_test_result(run)
            passed = run.passed
        else:
            ui.warn("sem comando de testes configurado — etapa pulada")

        ui.step(5, total, "decisão")
        if passed:
            if do_commit or (not yes and Confirm.ask("Criar o commit agora?", default=False, console=ui.console)):
                p = commit_proposal(a, p.id, allow_untested=True)
                ui.ok(f"commit {p.commit_sha[:10]} criado")
            else:
                ui.info(f"alterações aplicadas sem commit. Depois: gitruvian accept {p.short_id}")
            return
        if yes or Confirm.ask("Os testes falharam. Reverter as alterações?", default=True, console=ui.console):
            revert_proposal(a, p.id)
            ui.ok("alterações revertidas; a proposta e o resultado dos testes ficaram registrados")
        raise typer.Exit(1)
    finally:
        a.close()


@app.command()
@handle_errors
def history(
    limit: Annotated[int, typer.Option("--limit", "-n", min=1, max=10000)] = 30,
    proposal_id: Annotated[Optional[str], typer.Option("--proposal", help="Somente eventos desta proposta.")] = None,
) -> None:
    """Trilha de auditoria: cada alteração registrada, da mais recente para a mais antiga."""
    a = _app()
    try:
        pid = a.store.get_proposal(proposal_id).id if proposal_id else None
        events = a.store.list_events(limit=limit, proposal_id=pid)
        if not events:
            ui.empty("Nenhum evento registrado ainda.", "gitruvian index")
            return
        table = Table(box=box.SIMPLE_HEAD, header_style="label")
        for col in ("Quando (UTC)", "Tipo", "Proposta", "Resumo"):
            table.add_column(col)
        for e in events:
            summary = ("[EXEMPLO] " if e.is_sample and not e.summary.startswith("[EXEMPLO]") else "") + e.summary
            table.add_row(e.ts[:19].replace("T", " "), Text(e.kind), (e.proposal_id or "-")[:8], Text(summary))
        ui.console.print(table)
    finally:
        a.close()


# ---------------------------------------------------------------- import/export
@app.command("export")
@handle_errors
def export_cmd(
    path: Annotated[Path, typer.Argument(help="Arquivo JSON de saída.")],
    no_samples: Annotated[bool, typer.Option("--no-samples", help="Não inclui dados de exemplo.")] = False,
    force: Annotated[bool, typer.Option("--force", help="Sobrescreve o arquivo se existir.")] = False,
) -> None:
    """Exporta propostas, testes e eventos em JSON aberto (sem índice e sem segredos)."""
    if path.exists() and not force:
        raise ValueError(f"{path} já existe (use --force para sobrescrever)")
    a = _app()
    try:
        counts = export_to_file(a.store, path, include_samples=not no_samples)
        a.store.add_event("export", f"Exportação para {path.name}", data=counts)
        ui.ok(f"exportado para {path}: " + ", ".join(f"{v} {k}" for k, v in counts.items()))
        ui.info("o arquivo contém os diffs propostos; guarde-o como guardaria o próprio código")
    finally:
        a.close()


@app.command("import")
@handle_errors
def import_cmd(path: Annotated[Path, typer.Argument(help="Arquivo JSON gerado por 'gitruvian export'.")]) -> None:
    """Importa uma exportação (idempotente: registros já existentes são ignorados)."""
    data = load_export_file(path)
    a = _app()
    try:
        report = import_data(a.store, data)
        ui.ok("importado: " + ", ".join(f"{v} {k}" for k, v in report.inserted.items())
              + f"; {report.duplicates} já existiam")
        for item in report.skipped[:20]:
            ui.warn(item)
        if len(report.skipped) > 20:
            ui.warn(f"... e mais {len(report.skipped) - 20} itens ignorados")
    finally:
        a.close()


# ---------------------------------------------------------------------- config
@config_app.command("list")
@handle_errors
def config_list() -> None:
    """Lista as configurações do repositório."""
    a = _app()
    try:
        values = a.store.settings()
        for key, description in CONFIG_KEYS.items():
            ui.console.print(Text.assemble((f"{key}", "label"), f" = {values.get(key, '(não definido)')}"))
            ui.console.print(Text(f"  {description}"))
        if a.settings.test_command:
            ui.info("GITRUVIAN_TEST_COMMAND no ambiente tem prioridade sobre test_command")
    finally:
        a.close()


def _check_key(key: str) -> None:
    if key not in CONFIG_KEYS:
        raise ValueError(f"chave desconhecida: {key}. Válidas: {', '.join(CONFIG_KEYS)}")


@config_app.command("get")
@handle_errors
def config_get(key: str) -> None:
    """Mostra o valor de uma configuração."""
    _check_key(key)
    a = _app()
    try:
        value = a.store.get_setting(key)
        if value is None:
            ui.empty(f"{key} não está definido.", f'gitruvian config set {key} "..."')
        else:
            ui.console.print(Text(value))
    finally:
        a.close()


@config_app.command("set")
@handle_errors
def config_set(key: str, value: str) -> None:
    """Define uma configuração."""
    _check_key(key)
    if not value.strip() or len(value) > 1000:
        raise ValueError("valor vazio ou longo demais")
    a = _app()
    try:
        a.store.set_setting(key, value.strip())
        a.store.add_event("config", f"Configuração {key} alterada")
        ui.ok(f"{key} definido")
    finally:
        a.close()


@config_app.command("unset")
@handle_errors
def config_unset(key: str) -> None:
    """Remove uma configuração."""
    _check_key(key)
    a = _app()
    try:
        removed = a.store.delete_setting(key)
        if removed:
            a.store.add_event("config", f"Configuração {key} removida")
        (ui.ok if removed else ui.info)(f"{key} removido" if removed else f"{key} já não estava definido")
    finally:
        a.close()


# ---------------------------------------------------------------------- sample
@sample_app.command("create")
@handle_errors
def sample_create(path: Annotated[Path, typer.Argument(help="Pasta nova para o repositório de exemplo.")] = Path("loja-exemplo")) -> None:
    """Cria um repositório FICTÍCIO com um bug, testes e uma proposta de exemplo."""
    with ui.loading("Criando repositório de exemplo"):
        a, p = create_sample_repo(path)
    try:
        ui.ok(f"repositório de exemplo criado em {a.repo}")
        ui.console.print("  Tudo nele é fictício e marcado com [EXEMPLO]. Experimente:")
        ui.console.print(f"    cd {escape(str(path))}")
        ui.console.print("    gitruvian test                 (1 teste falha: o bug do frete)")
        ui.console.print(f"    gitruvian show {p.short_id}        (veja a proposta de correção)")
        ui.console.print(f"    gitruvian apply {p.short_id}       (aplica e roda os testes)")
        ui.console.print(f"    gitruvian accept {p.short_id}      (commita)")
        ui.console.print(f"  Para apagar: gitruvian sample remove {escape(str(path))}")
    finally:
        a.close()


@sample_app.command("remove")
@handle_errors
def sample_remove(
    path: Annotated[Path, typer.Argument(help="Pasta criada por 'sample create'.")],
    yes: Annotated[bool, typer.Option("--yes", "-y", help="Não pede confirmação.")] = False,
) -> None:
    """Apaga a pasta de exemplo (somente se tiver o marcador de dados de exemplo)."""
    if not yes and not Confirm.ask(f"Apagar a pasta de exemplo {path}?", default=False, console=ui.console):
        ui.info("nada foi apagado")
        return
    remove_sample_repo(path)
    ui.ok(f"pasta de exemplo apagada: {path}")


@sample_app.command("clear")
@handle_errors
def sample_clear() -> None:
    """Apaga do banco deste repositório todos os registros marcados como exemplo."""
    a = _app()
    try:
        counts = a.store.delete_samples()
        ui.ok("registros de exemplo apagados: " + ", ".join(f"{v} {k}" for k, v in counts.items()))
    finally:
        a.close()


# ------------------------------------------------------------- menu interativo
MENU: list[tuple[str, str]] = [
    ("1", "Painel"),
    ("2", "Status do repositório"),
    ("3", "Atualizar índice"),
    ("4", "Buscar no código"),
    ("5", "Propor alteração com o modelo"),
    ("6", "Propor a partir de um arquivo .diff/.patch"),
    ("7", "Listar propostas"),
    ("8", "Ver proposta"),
    ("9", "Aplicar proposta e testar"),
    ("10", "Commitar proposta (aceitar)"),
    ("11", "Reverter proposta"),
    ("12", "Rodar testes"),
    ("13", "Histórico"),
    ("0", "Sair"),
]


def _run_quietly(func: Callable[..., Any], *args: Any, **kwargs: Any) -> None:
    try:
        func(*args, **kwargs)
    except typer.Exit:
        pass


@app.command("ui")
def ui_cmd() -> None:
    """Menu interativo navegável só pelo teclado (digite o número e Enter; 0 ou Ctrl+C sai)."""
    if not sys.stdin.isatty():
        ui.error("o menu interativo precisa de um terminal", "use os comandos diretamente (gitruvian --help)")
        raise typer.Exit(2)
    _run_quietly(dashboard)
    while True:
        ui.heading("Menu — digite o número da ação e pressione Enter")
        for key, label in MENU:
            ui.console.print(f"  [key]{key:>2}[/key]  {label}")
        try:
            choice = Prompt.ask("Ação", choices=[k for k, _ in MENU], show_choices=False, console=ui.console)
            if choice == "0":
                return
            if choice == "1":
                _run_quietly(dashboard)
            elif choice == "2":
                _run_quietly(status)
            elif choice == "3":
                _run_quietly(index)
            elif choice == "4":
                _run_quietly(search_cmd, Prompt.ask("Termos de busca", console=ui.console))
            elif choice == "5":
                _run_quietly(propose_cmd, Prompt.ask("Descreva a alteração", console=ui.console))
            elif choice == "6":
                instruction = Prompt.ask("Descreva a alteração", console=ui.console)
                patch = Prompt.ask("Caminho do arquivo .diff/.patch", console=ui.console)
                _run_quietly(propose_cmd, instruction, patch_file=Path(patch))
            elif choice == "7":
                _run_quietly(proposals)
            elif choice in {"8", "9", "10", "11"}:
                pid = Prompt.ask("ID da proposta (ou prefixo)", console=ui.console)
                action = {"8": show, "9": apply_cmd, "10": accept, "11": revert}[choice]
                _run_quietly(action, pid)
            elif choice == "12":
                _run_quietly(test_cmd)
            elif choice == "13":
                _run_quietly(history)
        except (KeyboardInterrupt, EOFError):
            ui.console.print()
            return


def main() -> None:
    # Garante acentos corretos em consoles/pipes do Windows (cp1252 por padrão).
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure") and (stream.encoding or "").lower() not in {"utf-8", "utf8"}:
            stream.reconfigure(encoding="utf-8", errors="replace")
    app()


if __name__ == "__main__":
    main()
