from __future__ import annotations

import pytest

from gitruvian.diffs import DiffValidationError, extract_commit_message, extract_diff, validate_diff
from gitruvian.safety import (
    UnsafePathError,
    check_repo_relative_path,
    clean_instruction,
    is_sensitive_path,
    redact,
)

GOOD = "diff --git a/src/a.py b/src/a.py\n--- a/src/a.py\n+++ b/src/a.py\n@@ -1,2 +1,2 @@\n x = 1\n-y = 2\n+y = 3\n"


@pytest.mark.parametrize("path", [".env", "config/.env.production", "id_rsa", "certs/server.pem", "app.key",
                                  "my_secrets.yaml", ".ssh/config", "terraform.tfstate", ".npmrc"])
def test_sensitive_paths(path):
    assert is_sensitive_path(path)


@pytest.mark.parametrize("path", [".env.example", "src/app.py", "README.md", "keyboard.py"])
def test_non_sensitive_paths(path):
    assert not is_sensitive_path(path)


@pytest.mark.parametrize("path", ["../x.py", "a/../../x", "/etc/passwd", "C:/Windows/x", ".git/config",
                                  ".gitruvian/gitruvian.db", "a\\b.py", "", "x\x00.py", ".env"])
def test_unsafe_paths_rejected(path):
    with pytest.raises(UnsafePathError):
        check_repo_relative_path(path)


def test_safe_path_normalized():
    assert check_repo_relative_path("./src/a.py") == "src/a.py"


def test_redact_known_secret_shapes():
    text = (
        "ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnop\n"
        "aws = AKIAABCDEFGHIJKLMNOP\n"
        'password: "hunter2hunter2"\n'
        "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----\n"
        "normal_code = compute(1, 2)\n"
    )
    out = redact(text)
    for secret in ("sk-ant-api03", "AKIAABCDEFGHIJKLMNOP", "hunter2hunter2", "MIIabc"):
        assert secret not in out
    assert "normal_code = compute(1, 2)" in out


def test_clean_instruction():
    assert clean_instruction("  corrigir\x07 bug  ") == "corrigir bug"
    with pytest.raises(ValueError):
        clean_instruction("  ")
    with pytest.raises(ValueError):
        clean_instruction("x" * 5000)


def test_extract_diff_from_fenced_reply():
    reply = f"COMMIT: Ajusta y\n\nAqui está:\n```diff\n{GOOD}```\nfim"
    assert extract_diff(reply) == GOOD
    assert extract_commit_message(reply) == "Ajusta y"


def test_extract_diff_from_raw_text():
    assert extract_diff("texto\n" + GOOD).startswith("diff --git")


def test_extract_diff_missing():
    with pytest.raises(DiffValidationError):
        extract_diff("não sei fazer isso")


def test_validate_good_diff_counts():
    parsed = validate_diff(GOOD)
    assert parsed.files == ["src/a.py"] and parsed.additions == 1 and parsed.deletions == 1


def test_validate_new_file():
    diff = "diff --git a/n.py b/n.py\nnew file mode 100644\n--- /dev/null\n+++ b/n.py\n@@ -0,0 +1 @@\n+x = 1\n"
    assert validate_diff(diff).files == ["n.py"]


@pytest.mark.parametrize(
    "diff",
    [
        GOOD.replace("src/a.py", "../outside.py"),
        GOOD.replace("src/a.py", ".git/hooks/pre-commit"),
        GOOD.replace("src/a.py", ".env"),
        GOOD.replace("src/a.py", "/etc/passwd"),
        "diff --git a/x b/x\nGIT binary patch\nliteral 1\n",
        "diff --git a/l b/l\nnew file mode 120000\n--- /dev/null\n+++ b/l\n@@ -0,0 +1 @@\n+/etc/passwd\n",
        "--- a/x.py\n+++ b/x.py\n",
        "",
        "@@ -1 +1 @@\n-a\n+b\n",
    ],
)
def test_validate_rejects_unsafe_or_malformed(diff):
    with pytest.raises(DiffValidationError):
        validate_diff(diff)


def test_validate_rejects_huge_diff():
    with pytest.raises(DiffValidationError):
        validate_diff(GOOD + "+" + "x" * 500_000 + "\n")
