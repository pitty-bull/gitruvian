using static Gitruvian.Core.Tests.TestSupport;

namespace Gitruvian.Core.Tests;

/// <summary>Indexação, busca e contexto para o modelo.</summary>
public sealed class IndexerTests : IDisposable
{
    private readonly TestApp _t = new();

    public void Dispose() => _t.Dispose();

    private HashSet<string> IndexedPaths()
    {
        var paths = new HashSet<string>();
        using var cmd = _t.Store.Command("SELECT path FROM files");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            paths.Add(r.GetString(0));
        return paths;
    }

    [Fact]
    public void IndexSkipsSensitiveBinaryAndIgnored()
    {
        File.WriteAllBytes(_t.File("logo.png"), "\x89PNG\0\0binary"u8.ToArray());
        Write(_t.File("vendor/lib.js"), "var x = 1;\n");
        Write(_t.File(".gitruvianignore"), "vendor/\n");
        var result = Indexer.IndexRepo(_t.Store, _t.Repo);
        var paths = IndexedPaths();
        Assert.Contains("calc.ps1", paths);
        Assert.Contains("tests/Testes.ps1", paths);
        Assert.DoesNotContain(".env", paths);
        Assert.DoesNotContain("logo.png", paths);
        Assert.DoesNotContain("vendor/lib.js", paths);
        Assert.Equal(1, result.Skipped["sensível"]);
        Assert.Equal(1, result.Skipped["binário"]);
        Assert.Equal(1, result.Skipped["ignorado"]);
        var contents = new List<string>();
        using (var cmd = _t.Store.Command("SELECT content FROM chunks"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                contents.Add(r.GetString(0));
        Assert.DoesNotContain(SecretValue, string.Join(" ", contents));
    }

    [Fact]
    public void IndexIsIncremental()
    {
        var first = Indexer.IndexRepo(_t.Store, _t.Repo);
        Assert.True(first.Added >= 3);
        var second = Indexer.IndexRepo(_t.Store, _t.Repo);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(first.Added, second.Unchanged);
        Write(_t.File("calc.ps1"), "function Get-Total([double[]]$Prices) {\n    return ($Prices | Measure-Object -Sum).Sum\n}\n");
        File.Delete(_t.File("tests/Testes.ps1"));
        var third = Indexer.IndexRepo(_t.Store, _t.Repo);
        Assert.Equal(1, third.Updated);
        Assert.Equal(1, third.Removed);
        Assert.DoesNotContain("tests/Testes.ps1", IndexedPaths());
    }

    [Fact]
    public void SearchRanksRelevantFile()
    {
        Indexer.IndexRepo(_t.Store, _t.Repo);
        var hits = Indexer.Search(_t.Store, "subtotal prices");
        Assert.NotEmpty(hits);
        Assert.Equal("calc.ps1", hits[0].Path);
        Assert.Contains("calc.ps1", Indexer.Search(_t.Store, "desconto percentual").Select(h => h.Path));
        Assert.Empty(Indexer.Search(_t.Store, "palavrainexistente"));
    }

    [Fact]
    public void FtsQueryIsSanitized()
    {
        Indexer.IndexRepo(_t.Store, _t.Repo);
        Assert.Equal("\"drop\" OR \"table\" OR \"files\"", Indexer.BuildFtsQuery("\"; DROP TABLE files; --"));
        Assert.Null(Indexer.BuildFtsQuery("!!! ??"));
        Assert.NotNull(Indexer.Search(_t.Store, "NEAR(total \"x")); // sem erro de sintaxe FTS
    }

    [Fact]
    public void BuildContextRespectsBudgetAndPrivacy()
    {
        Indexer.IndexRepo(_t.Store, _t.Repo);
        var ctx = Indexer.BuildContext(_t.Store, _t.Repo, "corrigir desconto no total", 4000);
        Assert.Contains("### ARQUIVO: calc.ps1", ctx.Text);
        Assert.DoesNotContain(SecretValue, ctx.Text);
        Assert.DoesNotContain(".env", ctx.Entries.Select(e => e["path"]));
        Assert.True(ctx.Chars <= 4000);
        Assert.All(ctx.Entries, e => Assert.Equal(new HashSet<string> { "path", "lines" }, e.Keys.ToHashSet())); // metadados, nunca conteúdo
    }
}
