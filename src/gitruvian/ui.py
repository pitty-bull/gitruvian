"""Saída no terminal: rótulos textuais (não só cor), alto contraste e respeito a NO_COLOR."""

from __future__ import annotations

import os
import sys
from contextlib import contextmanager
from typing import Iterator

from rich.console import Console
from rich.markup import escape
from rich.syntax import Syntax
from rich.theme import Theme

THEME = Theme(
    {
        "ok": "bold green",
        "err": "bold red",
        "warn": "bold yellow",
        "info": "bold cyan",
        "label": "bold",
        "muted": "default",
        "key": "bold underline",
    }
)

console = Console(theme=THEME, highlight=False, soft_wrap=False)
err_console = Console(theme=THEME, highlight=False, stderr=True)


def configure(no_color: bool = False) -> None:
    global console, err_console
    plain = no_color or bool(os.environ.get("NO_COLOR"))
    console = Console(theme=THEME, highlight=False, no_color=plain)
    err_console = Console(theme=THEME, highlight=False, stderr=True, no_color=plain)


def ok(message: str) -> None:
    console.print(f"[ok]OK:[/ok] {escape(message)}")


def info(message: str) -> None:
    console.print(f"[info]INFO:[/info] {escape(message)}")


def warn(message: str) -> None:
    console.print(f"[warn]AVISO:[/warn] {escape(message)}")


def error(message: str, hint: str | None = None) -> None:
    err_console.print(f"[err]ERRO:[/err] {escape(message)}")
    if hint:
        err_console.print(f"  [label]Como resolver:[/label] {escape(hint)}")


def empty(title: str, hint: str) -> None:
    console.print(f"[label]{escape(title)}[/label]")
    console.print(f"  Próximo passo: [key]{escape(hint)}[/key]")


def heading(text: str) -> None:
    console.print(f"\n[label]{escape(text)}[/label]")


def step(n: int, total: int, text: str) -> None:
    console.print(f"[info]Etapa {n}/{total}:[/info] {escape(text)}")


@contextmanager
def loading(message: str) -> Iterator[None]:
    """Indicador de carregamento; em terminais não interativos imprime uma linha simples."""
    if console.is_terminal and sys.stdout.isatty():
        with console.status(f"{escape(message)} (Ctrl+C cancela)", spinner="line"):
            yield
    else:
        console.print(f"... {escape(message)}")
        yield


def show_diff(diff: str) -> None:
    console.print(Syntax(diff, "diff", theme="ansi_dark", word_wrap=True, background_color="default"))
