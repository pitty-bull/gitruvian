from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from gitruvian.app import open_app
from gitruvian.config import Settings
from gitruvian.workflow import TEST_COMMAND_KEY

CALC_BUGGY = '''def total(prices, discount=0.0):
    """Soma os preços e aplica um desconto percentual (0.1 = 10%)."""
    subtotal = sum(prices)
    return subtotal - discount
'''

CALC_TEST = '''import unittest

from calc import total


class TestTotal(unittest.TestCase):
    def test_sem_desconto(self):
        self.assertEqual(total([10, 20]), 30)

    def test_desconto_percentual(self):
        self.assertAlmostEqual(total([10, 20], 0.1), 27.0)


if __name__ == "__main__":
    unittest.main()
'''

FIX_DIFF = '''diff --git a/calc.py b/calc.py
--- a/calc.py
+++ b/calc.py
@@ -1,4 +1,4 @@
 def total(prices, discount=0.0):
     """Soma os preços e aplica um desconto percentual (0.1 = 10%)."""
     subtotal = sum(prices)
-    return subtotal - discount
+    return subtotal - subtotal * discount
'''

BAD_DIFF = '''diff --git a/calc.py b/calc.py
--- a/calc.py
+++ b/calc.py
@@ -1,4 +1,4 @@
 def total(prices, discount=0.0):
     """Soma os preços e aplica um desconto percentual (0.1 = 10%)."""
     subtotal = sum(prices)
-    return subtotal - discount
+    return subtotal + discount
'''

SECRET_VALUE = "supersecret-value-123456"
TEST_COMMAND = "{python} -m unittest discover -s tests"


@pytest.fixture(autouse=True)
def git_identity(monkeypatch: pytest.MonkeyPatch) -> None:
    for var in ("GIT_AUTHOR_NAME", "GIT_COMMITTER_NAME"):
        monkeypatch.setenv(var, "Teste Gitruvian")
    for var in ("GIT_AUTHOR_EMAIL", "GIT_COMMITTER_EMAIL"):
        monkeypatch.setenv(var, "teste@example.invalid")
    for var in list(__import__("os").environ):
        if var.startswith("GITRUVIAN_") or var == "ANTHROPIC_API_KEY":
            monkeypatch.delenv(var, raising=False)


def git(repo: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True, check=True).stdout


def write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(content)


def make_repo(root: Path) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    write(root / "calc.py", CALC_BUGGY)
    write(root / "tests" / "__init__.py", "")
    write(root / "tests" / "test_calc.py", CALC_TEST)
    write(root / ".gitignore", "__pycache__/\n")
    git(root, "init", "-q")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "inicial")
    # Arquivo sensível não rastreado: nunca pode ser indexado nem enviado ao modelo.
    write(root / ".env", f"API_KEY={SECRET_VALUE}\n")
    return root


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    return make_repo(tmp_path / "proj")


@pytest.fixture
def app(repo: Path):
    application = open_app(repo, create=True, settings=Settings())
    application.store.set_setting(TEST_COMMAND_KEY, TEST_COMMAND)
    yield application
    application.close()
