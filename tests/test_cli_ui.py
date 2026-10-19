"""Menu interativo e opções da CLI chamados em processo (sem TTY real)."""

from __future__ import annotations

import pytest
from conftest import FIX_DIFF
from typer.testing import CliRunner

from gitruvian import cli


@pytest.fixture
def isolated_env(monkeypatch, tmp_path):
    monkeypatch.setenv("GITRUVIAN_ENV_FILE", str(tmp_path / "nao-existe.env"))
    monkeypatch.setenv("NO_COLOR", "1")


def test_menu_lists_proposals_and_exits(app, isolated_env, monkeypatch, capsys):
    from gitruvian.workflow import propose

    p = propose(app, "Desconto percentual", patch_text=FIX_DIFF).proposal
    answers = iter(["7", "8", p.short_id, "0"])
    monkeypatch.setattr(cli.sys.stdin, "isatty", lambda: True)
    monkeypatch.setattr(cli.Prompt, "ask", classmethod(lambda cls, *a, **k: next(answers)))
    cli.ui.configure(no_color=True)
    monkeypatch.setattr(cli.state, "repo", app.repo)
    cli.ui_cmd()
    out = capsys.readouterr().out
    assert "Menu" in out and p.short_id in out and "Desconto percentual" in out
    assert "subtotal * discount" in out  # ação 8 mostrou o diff


def test_menu_requires_terminal(isolated_env, monkeypatch):
    monkeypatch.setattr(cli.sys.stdin, "isatty", lambda: False)
    with pytest.raises(cli.typer.Exit) as exc:
        cli.ui_cmd()
    assert exc.value.exit_code == 2


def test_propose_with_file_option_and_patch(app, isolated_env, tmp_path):
    patch = tmp_path / "fix.diff"
    patch.write_text(FIX_DIFF, encoding="utf-8")
    result = CliRunner().invoke(cli.app, ["-C", str(app.repo), "--no-color", "propose", "Desconto", "-f", "calc.py",
                                          "-p", str(patch)])
    assert result.exit_code == 0, result.output
    assert "aplica de forma limpa" in result.output
    raw = CliRunner().invoke(cli.app, ["-C", str(app.repo), "show", app.store.list_proposals()[0].short_id, "--raw"])
    assert raw.output.replace("\r\n", "\n") == FIX_DIFF


def test_invalid_input_exit_codes(app, isolated_env):
    runner = CliRunner()
    assert runner.invoke(cli.app, ["-C", str(app.repo), "propose", "x"]).exit_code == 2  # pedido curto demais
    assert runner.invoke(cli.app, ["-C", str(app.repo), "proposals", "--status", "nada"]).exit_code == 2
    assert runner.invoke(cli.app, ["-C", str(app.repo), "config", "set", "chave_x", "1"]).exit_code == 2
