"""Abertura do repositório e da pasta de dados local (.gitruvian/)."""

from __future__ import annotations

import hashlib
import os
from dataclasses import dataclass
from pathlib import Path

from gitruvian import gitops
from gitruvian.config import Settings, load_settings
from gitruvian.logs import setup_logging
from gitruvian.models import Store

DATA_DIR_NAME = ".gitruvian"
DB_NAME = "gitruvian.db"
EXCLUDE_LINE = f"/{DATA_DIR_NAME}/"


class NotInitialized(RuntimeError):
    pass


@dataclass
class App:
    repo: Path
    data_dir: Path
    store: Store
    settings: Settings

    @property
    def db_path(self) -> Path:
        return self.data_dir / DB_NAME

    def close(self) -> None:
        self.store.close()


def data_dir_for(repo: Path, settings: Settings) -> Path:
    if settings.data_dir:
        # Nome + hash do caminho: dois repositórios com o mesmo nome não colidem.
        suffix = hashlib.sha256(str(repo.resolve()).encode("utf-8")).hexdigest()[:8]
        return Path(settings.data_dir).expanduser().resolve() / f"{repo.name}-{suffix}"
    return repo / DATA_DIR_NAME


def _restrict_permissions(path: Path, mode: int) -> None:
    if os.name != "nt":
        try:
            path.chmod(mode)
        except OSError:
            pass


def _ensure_excluded(repo: Path) -> None:
    """Evita que .gitruvian/ seja commitado, sem mexer no .gitignore do usuário."""
    exclude = gitops.git_dir(repo) / "info" / "exclude"
    exclude.parent.mkdir(parents=True, exist_ok=True)
    current = exclude.read_text(encoding="utf-8") if exclude.exists() else ""
    if EXCLUDE_LINE not in current.splitlines():
        with exclude.open("a", encoding="utf-8") as fh:
            if current and not current.endswith("\n"):
                fh.write("\n")
            fh.write(f"# Gitruvian (dados locais)\n{EXCLUDE_LINE}\n")


def open_app(path: Path | None = None, create: bool = False, settings: Settings | None = None) -> App:
    settings = settings or load_settings()
    repo = gitops.find_repo_root(path or Path.cwd())
    data_dir = data_dir_for(repo, settings)
    db_path = data_dir / DB_NAME
    if not db_path.exists():
        if not create:
            raise NotInitialized(f"repositório ainda não inicializado. Rode: gitruvian init (em {repo})")
        data_dir.mkdir(parents=True, exist_ok=True)
        _restrict_permissions(data_dir, 0o700)
        if data_dir.is_relative_to(repo):
            _ensure_excluded(repo)
    store = Store.open(db_path)
    _restrict_permissions(db_path, 0o600)
    setup_logging(data_dir)
    return App(repo=repo, data_dir=data_dir, store=store, settings=settings)
