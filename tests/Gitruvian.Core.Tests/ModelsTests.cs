using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Gitruvian.Core.Tests;

/// <summary>Modelo de dados, Store SQLite e máquina de estados.</summary>
public sealed class ModelsTests : IDisposable
{
    private const string Diff = "--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-a\n+b\n";
    private readonly Store _store = Store.Open(":memory:");

    public void Dispose() => _store.Dispose();

    private Proposal Add(string instruction = "mudar x", bool isSample = false) =>
        _store.AddProposal(instruction, "manual", "-", Diff, ["x.cs"], "Muda x", isSample: isSample);

    private static Dictionary<string, object?> Fields(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => i.Value);

    [Fact]
    public void SchemaVersionAndTables()
    {
        Assert.Equal((long)Store.SchemaVersion, _store.Scalar("PRAGMA user_version"));
        var tables = new HashSet<string>();
        using (var cmd = _store.Command("SELECT name FROM sqlite_master WHERE type IN ('table')"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                tables.Add(r.GetString(0));
        Assert.Superset(new HashSet<string> { "settings", "files", "chunks", "proposals", "test_runs", "events" }, tables);
    }

    [Fact]
    public void RefusesNewerSchema()
    {
        using var tmp = new TempDir();
        var path = tmp["db.sqlite"];
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {Store.SchemaVersion + 1}";
            cmd.ExecuteNonQuery();
        }
        var exc = Assert.Throws<InvalidOperationException>(() => Store.Open(path));
        Assert.Contains("mais nova", exc.Message);
    }

    [Fact]
    public void AddAndGetProposalByPrefix()
    {
        var p = Add();
        Assert.Equal(Status.Proposed, p.Status);
        Assert.Equal(Clock.Sha256(Diff), p.DiffSha256);
        var got = _store.GetProposal(p.Id[..6]);
        Assert.Equal(p.Id, got.Id);
        Assert.Equal(["x.cs"], got.Files);
        Assert.Equal("mudar x", got.Instruction);
    }

    [Fact]
    public void GetProposalValidatesId()
    {
        Assert.Throws<NotFoundException>(() => _store.GetProposal("xyz"));
        Assert.Throws<NotFoundException>(() => _store.GetProposal("' OR 1=1 --"));
        Assert.Throws<NotFoundException>(() => _store.GetProposal("abcd"));
    }

    [Fact]
    public void AmbiguousPrefix()
    {
        foreach (var suffix in new[] { "1", "2" })
        {
            var ts = Clock.NowIso();
            _store.InsertProposal(new Proposal
            {
                Id = "abcd" + new string(suffix[0], 28), CreatedAt = ts, UpdatedAt = ts, Instruction = "i",
                Provider = "manual", Model = "-", Diff = Diff, DiffSha256 = Clock.Sha256(Diff), Files = ["x.cs"],
                CommitMessage = "m", Status = Status.Proposed,
            });
        }
        Assert.Throws<AmbiguousIdException>(() => _store.GetProposal("abcd"));
        Assert.StartsWith("abcd1", _store.GetProposal("abcd1").Id);
    }

    [Fact]
    public void InsertIsIdempotent()
    {
        var p = Add();
        Assert.False(_store.InsertProposal(p));
    }

    [Fact]
    public void HappyPathTransitions()
    {
        var p = Add();
        p = _store.Transition(p.Id, Status.Applied, Fields(("base_commit", new string('a', 40)), ("branch", "main")));
        Assert.Equal(Status.Applied, p.Status);
        Assert.Equal(new string('a', 40), p.BaseCommit);
        Assert.Equal("main", p.Branch);
        p = _store.Transition(p.Id, Status.TestsPassed);
        p = _store.Transition(p.Id, Status.Committed, Fields(("commit_sha", new string('b', 40))));
        Assert.Equal(new string('b', 40), p.CommitSha);
        p = _store.Transition(p.Id, Status.Reverted);
        Assert.Equal(Status.Reverted, p.Status);
    }

    public static TheoryData<Status[]> InvalidPaths => new()
    {
        new[] { Status.Committed },
        new[] { Status.TestsPassed },
        new[] { Status.Rejected, Status.Applied },
        new[] { Status.Applied, Status.TestsFailed, Status.Committed },
    };

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void InvalidTransitions(Status[] path)
    {
        var p = Add();
        Assert.Throws<InvalidTransitionException>(() =>
        {
            foreach (var status in path)
                _store.Transition(p.Id, status);
        });
    }

    [Fact]
    public void EveryStatusHasTransitionRules()
    {
        Assert.Equal(Enum.GetValues<Status>().ToHashSet(), StatusInfo.Transitions.Keys.ToHashSet());
        Assert.Empty(StatusInfo.Transitions[Status.Rejected]);
    }

    [Fact]
    public void TransitionRejectsUnknownFields()
    {
        var p = Add();
        Assert.Throws<ArgumentException>(() => _store.Transition(p.Id, Status.Applied, Fields(("diff", "hack"))));
    }

    [Fact]
    public void ListAndCounts()
    {
        var a = Add("a");
        Add("b");
        _store.Transition(a.Id, Status.Rejected);
        Assert.Equal(["b"], _store.ListProposals(Status.Proposed).Select(p => p.Instruction));
        var counts = _store.StatusCounts();
        Assert.Equal(1, counts[Status.Proposed]);
        Assert.Equal(1, counts[Status.Rejected]);
    }

    [Fact]
    public void SettingsCrud()
    {
        Assert.Null(_store.GetSetting("test_command"));
        _store.SetSetting("test_command", "dotnet test");
        _store.SetSetting("test_command", "dotnet test --no-build");
        Assert.Equal(new Dictionary<string, string> { ["test_command"] = "dotnet test --no-build" }, _store.Settings());
        Assert.True(_store.DeleteSetting("test_command"));
        Assert.False(_store.DeleteSetting("test_command"));
    }

    [Fact]
    public void TestRunsAndEvents()
    {
        var p = Add();
        var run = _store.AddTestRun("dotnet test", 10, 1, "F", p.Id);
        Assert.False(run.Passed);
        _store.AddTestRun("dotnet test", 5, 0, ".", p.Id);
        Assert.Equal([true, false], _store.ListTestRuns(p.Id).Select(r => r.Passed));
        _store.AddEvent("apply", "aplicado", p.Id, new JsonObject { ["files"] = new JsonArray("x.cs") });
        var events = _store.ListEvents(proposalId: p.Id);
        Assert.Equal("apply", events[0].Kind);
        Assert.Equal("{\"files\":[\"x.cs\"]}", events[0].Data.ToJsonString());
    }

    [Fact]
    public void DeleteSamplesKeepsUserData()
    {
        var real = Add();
        var sample = Add(isSample: true);
        _store.AddEvent("sample", "ex", sample.Id, isSample: true);
        _store.AddEvent("propose", "real", real.Id);
        _store.AddTestRun("t", 1, 0, "", sample.Id, isSample: true);
        var counts = _store.DeleteSamples();
        Assert.Equal(new Dictionary<string, int> { ["events"] = 1, ["test_runs"] = 1, ["proposals"] = 1 }, counts);
        Assert.Equal([real.Id], _store.ListProposals().Select(p => p.Id));
        Assert.Single(_store.ListEvents());
    }

    [Fact]
    public void StatusCheckConstraint()
    {
        var p = Add();
        var exc = Assert.Throws<SqliteException>(() =>
            _store.Execute("UPDATE proposals SET status = 'bogus' WHERE id = $id", ("$id", p.Id)));
        Assert.Contains("CHECK", exc.Message, StringComparison.OrdinalIgnoreCase);
    }
}
