using System.Text.Json.Nodes;
using static Gitruvian.Core.Tests.TestSupport;

namespace Gitruvian.Core.Tests;

/// <summary>Camada usada pela interface gráfica.</summary>
public sealed class ServicesTests
{
    private static readonly Settings S = new();

    [Fact]
    public void GraphLogReadsParentsRefsAndMerges()
    {
        using var tmp = new TempDir();
        var repo = MakeRepo(tmp["proj"]);
        RunGit(repo, "switch", "-q", "-c", "feature");
        Write(Path.Combine(repo, "feature.txt"), "x\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "feature");
        RunGit(repo, "switch", "-q", "-");
        Write(Path.Combine(repo, "main.txt"), "y\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "main");
        RunGit(repo, "merge", "-q", "--no-ff", "-m", "merge feature", "feature");
        RunGit(repo, "-c", "tag.gpgSign=false", "tag", "v1");
        var commits = Git.GraphLog(repo);
        Assert.Equal(4, commits.Count);
        var top = commits[0];
        Assert.Equal("merge feature", top.Subject);
        Assert.Equal(2, top.Parents.Count);
        Assert.Contains("HEAD", top.Refs);
        Assert.Contains("tag:v1", top.Refs);
        Assert.Contains(commits, c => c.Refs.SequenceEqual(["feature"]));
        var detail = Git.CommitDetail(repo, commits[^1].Sha);
        Assert.Empty(detail.Parents);
        Assert.Contains(new CommitFile("A", "calc.ps1"), detail.Files);
        Assert.Throws<GitException>(() => Git.CommitDetail(repo, "--output=x"));
    }

    [Fact]
    public void GraphLogEmptyRepository()
    {
        using var tmp = new TempDir();
        InitGit(tmp.Path);
        Assert.Empty(Git.GraphLog(tmp.Path));
    }

    [Fact]
    public void WorkingFileDiffAndDiscard()
    {
        using var tmp = new TempDir();
        var repo = MakeRepo(tmp["proj"]);
        Write(Path.Combine(repo, "calc.ps1"), "mudado\n");
        Write(Path.Combine(repo, "novo.txt"), "linha 1\nlinha 2\n");
        Assert.Contains("+mudado", Services.FileDiff(repo, "calc.ps1", staged: false, untracked: false));
        var created = Services.FileDiff(repo, "novo.txt", staged: false, untracked: true);
        Assert.Contains("+linha 2", created);
        Assert.Contains("novo arquivo", created);
        Assert.Throws<UnsafePathException>(() => Services.FileDiff(repo, "../fora.txt", staged: false, untracked: true));
        Services.Discard(repo, ["calc.ps1"], untracked: false);
        Services.Discard(repo, ["novo.txt"], untracked: true);
        Assert.False(File.Exists(Path.Combine(repo, "novo.txt")));
        Assert.Equal([".env"], Git.Status(repo).Select(e => e.Path));
    }

    [Fact]
    public void SensitiveFileContentIsNeverShown()
    {
        using var tmp = new TempDir();
        var repo = MakeRepo(tmp["proj"]);
        var shown = Services.FileDiff(repo, ".env", staged: false, untracked: true);
        Assert.DoesNotContain("supersecret", shown);
        Assert.Contains("oculto", shown);
        Services.Stage(repo, [".env"]); // o usuário ainda pode operar o arquivo pelo git
        Assert.Equal(["A "], Git.Status(repo).Where(e => e.Path == ".env").Select(e => e.Code));
        RunGit(repo, "commit", "-q", "-m", "commit com segredo");
        var sha = Git.HeadSha(repo)!;
        var full = Services.CommitDiff(repo, sha);
        Assert.DoesNotContain("supersecret", full);
        Assert.Contains("conteúdo oculto: .env", full);
        Assert.DoesNotContain("supersecret", Services.CommitDiff(repo, sha, ".env"));
    }

    [Fact]
    public void StageCommitAndMessageValidation()
    {
        using var t = new TestApp();
        var repo = t.Repo;
        Write(t.File("calc.ps1"), "mudado\n");
        var exc = Assert.Throws<ArgumentException>(() => Services.CommitStaged(repo, S, "msg"));
        Assert.Contains("preparada", exc.Message);
        Services.Stage(repo, ["calc.ps1"]);
        exc = Assert.Throws<ArgumentException>(() => Services.CommitStaged(repo, S, "   \n "));
        Assert.Contains("mensagem", exc.Message);
        var sha = Services.CommitStaged(repo, S, "Altera calc\r\n\r\nDetalhe");
        Assert.Equal(sha, Git.HeadSha(repo));
        Assert.Equal("Altera calc\n\nDetalhe", RunGit(repo, "log", "-1", "--format=%B").Trim());
        var snap = Services.TakeSnapshot(repo, S);
        Assert.Equal("git_commit", snap.Events[0].Kind);
        Assert.Throws<ArgumentException>(() => Services.Stage(repo, []));
    }

    [Fact]
    public void SnapshotAndProposalCycle()
    {
        RequireTestShell();
        using var t = new TestApp();
        var repo = t.Repo;
        var outcome = Services.Propose(repo, S, "corrige desconto", FixDiff, []);
        var pid = outcome.Proposal.Id;
        var snap = Services.TakeSnapshot(repo, S);
        Assert.Equal(pid, snap.Proposals[0].Id);
        Assert.False(snap.ProviderReady);
        Assert.Contains("manual", snap.ProviderLabel);
        Assert.NotEmpty(snap.Rows);
        Assert.Equal(snap.Commits[0].Sha, snap.Rows[0].Sha);
        Services.ApplyProposal(repo, S, pid, "");
        var (run, p) = Services.RunTests(repo, S, pid);
        Assert.True(run.Passed, run.OutputTail);
        Assert.Equal(Status.TestsPassed, p!.Status);
        p = Services.AcceptProposal(repo, S, pid, "Corrige desconto", allowUntested: false);
        Assert.Equal(Status.Committed, p.Status);
        var detail = Services.GetProposalDetail(repo, S, pid[..8]);
        Assert.Equal("commit", detail.Events[0].Kind);
        Assert.True(detail.TestRuns[0].Passed);
    }

    [Fact]
    public void ContextFilesAndTestCommandValidation()
    {
        using var t = new TestApp();
        Assert.Equal(["calc.ps1", "tests/Testes.ps1"], Services.ParseContextFiles("calc.ps1, tests/Testes.ps1;\n"));
        Assert.Throws<UnsafePathException>(() => Services.ParseContextFiles("../etc/passwd"));
        Assert.Throws<ArgumentException>(() => Services.SetTestCommand(t.Repo, S, "a\nb"));
        Services.SetTestCommand(t.Repo, S, "");
        Assert.Null(Services.TakeSnapshot(t.Repo, S).TestCommand);
    }

    [Fact]
    public void InspectAndInitRepo()
    {
        using var tmp = new TempDir();
        var repo = MakeRepo(tmp["proj"]);
        Write(Path.Combine(repo, "package.json"), "{\"scripts\": {\"test\": \"node teste.js\"}}\n");
        var info = Services.InspectRepo(Path.Combine(repo, "tests"), S);
        Assert.Equal(Path.GetFullPath(repo), info.Root, ignoreCase: true);
        Assert.False(info.Initialized);
        var detected = Services.InitRepo(repo, S);
        Assert.Equal("npm test", detected);
        Assert.Equal("npm test", Services.TakeSnapshot(repo, S).TestCommand);
        Assert.True(Services.InspectRepo(repo, S).Initialized);
        Assert.Throws<GitException>(() => Services.InspectRepo(tmp.Path, S));
    }

    [Fact]
    public void DetectTestCommandByProjectFiles()
    {
        using var tmp = new TempDir();
        Assert.Null(TestRunner.DetectTestCommand(tmp.Path));
        var cases = new (string File, string Expected)[]
        {
            ("Calc.csproj", "dotnet test"), ("Calc.slnx", "dotnet test"), ("package.json", "npm test"),
            ("Cargo.toml", "cargo test"), ("go.mod", "go test ./..."),
        };
        foreach (var (file, expected) in cases)
        {
            var dir = tmp[Path.GetFileNameWithoutExtension(file) + Path.GetExtension(file).TrimStart('.')];
            Write(Path.Combine(dir, file), "\n");
            Assert.Equal(expected, TestRunner.DetectTestCommand(dir));
        }
    }

    [Fact]
    public void TestCommandIsSplitWithoutShellAndRunsWithoutTelemetry()
    {
        Assert.Equal(["dotnet", "run", "--project", "a b/Testes"],
            TestRunner.ExpandCommand("dotnet run --project \"a b/Testes\""));
        Assert.Equal(["{exe}", "-x"], TestRunner.ExpandCommand("{exe} -x")); // não há placeholders: só divide
        Assert.Throws<ArgumentException>(() => TestRunner.ExpandCommand("   "));
        Assert.Equal("1", TestRunner.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    }

    [Fact]
    public void RecentReposAreValidated()
    {
        using var tmp = new TempDir();
        var store = Path.Combine(tmp["cfg"], "recent.json");
        var a = tmp["a"];
        var b = tmp["b"];
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        RecentRepos.Remember(a, store);
        Assert.Equal([b, a], RecentRepos.Remember(b, store));
        Assert.Equal([a, b], RecentRepos.Remember(a, store));
        Assert.Equal([a], RecentRepos.Forget(b, store));
        File.WriteAllText(store, new JsonObject
        {
            ["repos"] = new JsonArray(a, "relativo", 42, tmp["sumiu"], new string('x', 5000)),
        }.ToJsonString());
        Assert.Equal([a], RecentRepos.Load(store));
        File.WriteAllText(store, "{quebrado");
        Assert.Empty(RecentRepos.Load(store));
        File.WriteAllText(store, "[\"lista\"]");
        Assert.Empty(RecentRepos.Load(store));
    }

    [Fact]
    public void PreferencesRoundTrip()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp["cfg"], "preferences.json");
        Assert.Null(Preferences.Load(path).AutoUpdate);
        new Preferences { AutoUpdate = true }.Save(path);
        Assert.True(Preferences.Load(path).AutoUpdate);
        File.WriteAllText(path, "{\"auto_update\": \"sim\"}");
        Assert.Null(Preferences.Load(path).AutoUpdate);
    }

    [Fact]
    public void SampleLifecycleViaServices()
    {
        using var tmp = new TempDir();
        var root = Services.CreateSample(tmp["loja"], S);
        var snap = Services.TakeSnapshot(root, S);
        Assert.True(snap.IsSample);
        Assert.True(snap.Proposals[0].IsSample);
        var counts = Services.ClearSamples(root, S);
        Assert.Equal(1, counts["proposals"]);
        Services.RemoveSample(root);
        Assert.False(Directory.Exists(root));
    }
}
