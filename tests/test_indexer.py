from __future__ import annotations

from conftest import SECRET_VALUE, write

from gitruvian.indexer import build_context, build_fts_query, index_repo, search


def _indexed_paths(app) -> set[str]:
    return {r["path"] for r in app.store.conn.execute("SELECT path FROM files")}


def test_index_skips_sensitive_binary_and_ignored(app):
    (app.repo / "logo.png").write_bytes(b"\x89PNG\x00\x00binary")
    write(app.repo / "vendor" / "lib.js", "var x = 1;\n")
    write(app.repo / ".gitruvianignore", "vendor/\n")
    result = index_repo(app.store, app.repo)
    paths = _indexed_paths(app)
    assert "calc.py" in paths and "tests/test_calc.py" in paths
    assert ".env" not in paths and "logo.png" not in paths and "vendor/lib.js" not in paths
    assert result.skipped["sensível"] == 1 and result.skipped["binário"] == 1 and result.skipped["ignorado"] == 1
    contents = " ".join(r[0] for r in app.store.conn.execute("SELECT content FROM chunks"))
    assert SECRET_VALUE not in contents


def test_index_is_incremental(app):
    first = index_repo(app.store, app.repo)
    assert first.added >= 3
    second = index_repo(app.store, app.repo)
    assert second.added == 0 and second.updated == 0 and second.unchanged == first.added
    write(app.repo / "calc.py", "def total(prices):\n    return sum(prices)\n")
    (app.repo / "tests" / "test_calc.py").unlink()
    third = index_repo(app.store, app.repo)
    assert third.updated == 1 and third.removed == 1
    assert "tests/test_calc.py" not in _indexed_paths(app)


def test_search_ranks_relevant_file(app):
    index_repo(app.store, app.repo)
    hits = search(app.store, "subtotal prices")
    assert hits and hits[0].path == "calc.py"
    assert "calc.py" in [h.path for h in search(app.store, "desconto percentual")]
    assert search(app.store, "palavrainexistente") == []


def test_fts_query_is_sanitized(app):
    index_repo(app.store, app.repo)
    assert build_fts_query('"; DROP TABLE files; --') == '"drop" OR "table" OR "files"'
    assert build_fts_query("!!! ??") is None
    assert isinstance(search(app.store, 'NEAR(total "x'), list)  # sem erro de sintaxe FTS


def test_build_context_respects_budget_and_privacy(app):
    index_repo(app.store, app.repo)
    ctx = build_context(app.store, app.repo, "corrigir desconto no total", max_chars=4000)
    assert "### ARQUIVO: calc.py" in ctx.text
    assert SECRET_VALUE not in ctx.text and ".env" not in [e["path"] for e in ctx.entries]
    assert ctx.chars <= 4000
    assert all(set(e) == {"path", "lines"} for e in ctx.entries)  # metadados, nunca conteúdo
