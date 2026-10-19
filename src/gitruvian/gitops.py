"""Operações Git via subprocess (sempre com lista de argumentos, nunca shell)."""

from __future__ import annotations

import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

GIT_TIMEOUT = 120
APPLY_FLAGS = ("--recount", "--whitespace=nowarn")


class GitError(RuntimeError):
    """Falha ao executar um comando git."""


def git_available() -> bool:
    return shutil.which("git") is not None


def run_git(
    repo: Path,
    *args: str,
    input_text: str | None = None,
    check: bool = True,
    timeout: int = GIT_TIMEOUT,
) -> subprocess.CompletedProcess[str]:
    if not git_available():
        raise GitError("git não encontrado no PATH")
    cmd = ["git", "-c", "core.quotepath=off", "-c", "color.ui=never", *args]
    try:
        proc = subprocess.run(
            cmd,
            cwd=str(repo),
            input=input_text.encode("utf-8") if input_text is not None else None,
            capture_output=True,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired as exc:
        raise GitError(f"git {args[0]} excedeu {timeout}s") from exc
    result = subprocess.CompletedProcess(
        proc.args,
        proc.returncode,
        proc.stdout.decode("utf-8", errors="replace"),
        proc.stderr.decode("utf-8", errors="replace"),
    )
    if check and result.returncode != 0:
        message = (result.stderr or result.stdout).strip() or f"código {result.returncode}"
        raise GitError(f"git {args[0]} falhou: {message}")
    return result


def find_repo_root(path: Path) -> Path:
    path = path.resolve()
    if not path.exists():
        raise GitError(f"pasta não existe: {path}")
    result = run_git(path, "rev-parse", "--show-toplevel", check=False)
    if result.returncode != 0:
        raise GitError(f"{path} não está dentro de um repositório git (rode 'git init')")
    return Path(result.stdout.strip()).resolve()


def git_dir(repo: Path) -> Path:
    out = run_git(repo, "rev-parse", "--git-dir").stdout.strip()
    path = Path(out)
    return path if path.is_absolute() else (repo / path).resolve()


def head_sha(repo: Path) -> str | None:
    result = run_git(repo, "rev-parse", "--verify", "-q", "HEAD", check=False)
    return result.stdout.strip() or None if result.returncode == 0 else None


def current_branch(repo: Path) -> str:
    result = run_git(repo, "symbolic-ref", "--short", "-q", "HEAD", check=False)
    if result.returncode == 0 and result.stdout.strip():
        return result.stdout.strip()
    sha = head_sha(repo)
    return f"(HEAD destacado em {sha[:8]})" if sha else "(sem commits)"


@dataclass
class StatusEntry:
    code: str
    path: str

    @property
    def label(self) -> str:
        x, y = self.code[0], self.code[1]
        if self.code == "??":
            return "não rastreado"
        if "U" in self.code or self.code in {"AA", "DD"}:
            return "conflito"
        parts = []
        names = {"M": "modificado", "A": "adicionado", "D": "removido", "R": "renomeado", "C": "copiado", "T": "tipo alterado"}
        if x != " ":
            parts.append(f"{names.get(x, x)} (preparado)")
        if y != " ":
            parts.append(names.get(y, y))
        return ", ".join(parts)

    @property
    def staged(self) -> bool:
        return self.code != "??" and self.code[0] != " "


def status(repo: Path) -> list[StatusEntry]:
    out = run_git(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all").stdout
    entries: list[StatusEntry] = []
    items = out.split("\0")
    i = 0
    while i < len(items):
        item = items[i]
        i += 1
        if len(item) < 4:
            continue
        code, path = item[:2], item[3:]
        if code[0] in "RC":
            i += 1  # pula o caminho de origem do rename/cópia
        entries.append(StatusEntry(code, path))
    return entries


@dataclass
class Commit:
    sha: str
    author: str
    date: str
    subject: str
    refs: str


def log(repo: Path, limit: int = 20) -> list[Commit]:
    if head_sha(repo) is None:
        return []
    fmt = "%H%x1f%an%x1f%ad%x1f%s%x1f%D%x1e"
    out = run_git(repo, "log", f"-n{limit}", f"--format={fmt}", "--date=short").stdout
    commits = []
    for record in out.split("\x1e"):
        record = record.strip("\n")
        if not record:
            continue
        sha, author, date, subject, refs = (record.split("\x1f") + [""] * 5)[:5]
        commits.append(Commit(sha, author, date, subject, refs))
    return commits


def graph(repo: Path, limit: int = 30) -> str:
    if head_sha(repo) is None:
        return ""
    return run_git(repo, "log", "--graph", "--oneline", "--decorate", "--all", f"-n{limit}").stdout


def branches(repo: Path) -> list[tuple[str, bool]]:
    out = run_git(repo, "branch", "--format=%(HEAD)%00%(refname:short)").stdout
    result = []
    for line in out.splitlines():
        head, _, name = line.partition("\0")
        if name:
            result.append((name, head.strip() == "*"))
    return result


def ls_files(repo: Path) -> list[str]:
    out = run_git(repo, "ls-files", "-z", "--cached", "--others", "--exclude-standard").stdout
    return sorted({p for p in out.split("\0") if p})


def diff_text(repo: Path, staged: bool = False, paths: list[str] | None = None) -> str:
    args = ["diff", "--no-ext-diff"]
    if staged:
        args.append("--cached")
    if paths:
        args += ["--", *paths]
    return run_git(repo, *args).stdout


def apply_check(repo: Path, patch: str, reverse: bool = False) -> tuple[bool, str, tuple[str, ...]]:
    """Verifica se o patch aplica. Retorna (ok, erro, flags que funcionaram)."""
    base = ["apply", "--check", *APPLY_FLAGS] + (["-R"] if reverse else [])
    first = run_git(repo, *base, "-", input_text=patch, check=False)
    if first.returncode == 0:
        return True, "", ()
    # Tolera diferenças de espaço/fim de linha (comum com core.autocrlf no Windows).
    second = run_git(repo, *base, "--ignore-whitespace", "-", input_text=patch, check=False)
    if second.returncode == 0:
        return True, "", ("--ignore-whitespace",)
    return False, (first.stderr or first.stdout).strip(), ()


def apply_patch(repo: Path, patch: str, reverse: bool = False) -> None:
    ok, error, extra = apply_check(repo, patch, reverse=reverse)
    if not ok:
        raise GitError(f"o diff não aplica no estado atual: {error}")
    args = ["apply", *APPLY_FLAGS, *extra] + (["-R"] if reverse else []) + ["-"]
    run_git(repo, *args, input_text=patch)


def stage(repo: Path, paths: list[str]) -> None:
    run_git(repo, "add", "-A", "--", *paths)


def unstage(repo: Path, paths: list[str]) -> None:
    if head_sha(repo) is None:
        run_git(repo, "rm", "--cached", "-q", "--", *paths)
    else:
        run_git(repo, "reset", "-q", "HEAD", "--", *paths)


def commit(repo: Path, message: str, paths: list[str] | None = None) -> str:
    args = ["commit", "-q", "-F", "-"]
    if paths:
        stage(repo, paths)
        args += ["--", *paths]
    run_git(repo, *args, input_text=message)
    sha = head_sha(repo)
    if not sha:
        raise GitError("commit não foi criado")
    return sha


def revert_commit(repo: Path, sha: str) -> str:
    run_git(repo, "revert", "--no-edit", sha)
    new = head_sha(repo)
    assert new is not None
    return new


def create_branch(repo: Path, name: str) -> None:
    run_git(repo, "check-ref-format", "--branch", name)
    run_git(repo, "switch", "-c", name)


def switch(repo: Path, name: str) -> None:
    run_git(repo, "check-ref-format", "--branch", name)
    run_git(repo, "switch", name)


def dirty_paths(repo: Path, paths: list[str]) -> list[str]:
    wanted = set(paths)
    return [e.path for e in status(repo) if e.path in wanted]
