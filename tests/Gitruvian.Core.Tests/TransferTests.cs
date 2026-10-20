using System.Text.Json.Nodes;
using static Gitruvian.Core.Tests.TestSupport;

namespace Gitruvian.Core.Tests;

/// <summary>Exportação e importação JSON.</summary>
public sealed class TransferTests
{
    [Fact]
    public void ExportImportRoundtrip()
    {
        RequireTestShell(); // a contagem de test_runs depende de uma execução real dos testes
        using var t = new TestApp();
        var p = Workflow.Propose(t.App, "Desconto", FixDiff).Proposal;
        Workflow.Apply(t.App, p.Id);
        Workflow.RunTests(t.App, p.Id);
        var output = t.Tmp["backup.json"];
        var counts = Transfer.ExportToFile(t.Store, output);
        Assert.Equal(1, counts["proposals"]);
        Assert.Equal(1, counts["test_runs"]);
        var data = JsonNode.Parse(ReadText(output))!.AsObject();
        Assert.False(data["settings"]!.AsObject().ContainsKey("test_command"));

        using var other = AppSession.Open(MakeRepo(t.Tmp["outro"]), create: true, settings: new Settings());
        var report = Transfer.Import(other.Store, Transfer.LoadExportFile(output));
        Assert.Equal(new Dictionary<string, int> { ["proposals"] = 1, ["test_runs"] = 1, ["events"] = counts["events"] },
            report.Inserted);
        var imported = other.Store.GetProposal(p.Id);
        Assert.Equal(Status.TestsPassed, imported.Status);
        Assert.Equal(p.Diff, imported.Diff);
        var again = Transfer.Import(other.Store, Transfer.LoadExportFile(output));
        Assert.Equal(0, again.Inserted["proposals"]);
        Assert.True(again.Duplicates >= 2);
    }

    private static JsonObject Record(Action<JsonObject>? change = null)
    {
        var ts = Clock.NowIso();
        var record = new JsonObject
        {
            ["id"] = Clock.NewId(), ["created_at"] = ts, ["updated_at"] = ts, ["instruction"] = "x",
            ["provider"] = "manual", ["model"] = "-", ["diff"] = FixDiff, ["files"] = new JsonArray("calc.ps1"),
            ["commit_message"] = "m", ["status"] = "proposed", ["context"] = new JsonArray(), ["is_sample"] = false,
        };
        change?.Invoke(record);
        return record;
    }

    [Fact]
    public void ImportSkipsMaliciousRecords()
    {
        using var t = new TestApp();
        var data = new JsonObject
        {
            ["format"] = "gitruvian-export",
            ["version"] = 1,
            ["settings"] = new JsonObject { ["test_command"] = "rm -rf /" },
            ["proposals"] = new JsonArray(
                Record(),
                Record(r => r["diff"] = FixDiff.Replace("calc.ps1", "../../etc/passwd")),
                Record(r => r["status"] = "hacked"),
                Record(r => r["id"] = "'; DROP TABLE proposals; --"),
                Record(r => r["instruction"] = 123)),
            ["events"] = new JsonArray(new JsonObject
            {
                ["id"] = Clock.NewId(), ["ts"] = Clock.NowIso(), ["kind"] = "x y", ["summary"] = "s",
            }),
        };
        var report = Transfer.Import(t.Store, data);
        Assert.Equal(1, report.Inserted["proposals"]);
        Assert.Equal(6, report.Skipped.Count);
        Assert.Equal(TestCommand, t.Store.GetSetting("test_command"));
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("{\"format\": \"outro\"}")]
    [InlineData("{\"format\": \"gitruvian-export\", \"version\": 99}")]
    public void LoadRejectsInvalidFiles(string content)
    {
        using var tmp = new TempDir();
        var path = tmp["x.json"];
        Write(path, content);
        Assert.Throws<ImportValidationException>(() => Transfer.LoadExportFile(path));
    }
}
