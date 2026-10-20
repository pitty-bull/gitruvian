using System.Text.Json.Nodes;
using static Gitruvian.Core.Tests.TestSupport;

namespace Gitruvian.Core.Tests;

/// <summary>
/// Ciclo de ponta a ponta chamando Services/Workflow diretamente.
/// Sem rede: o "modelo" é o script tests/fixtures/fake_model.ps1.
/// </summary>
public sealed class SmokeE2ETests
{
    [Fact]
    public void CoreLoopEndToEnd()
    {
        RequireTestShell();
        using var tmp = new TempDir();
        var repo = MakeRepo(tmp["proj"]);
        var envFile = tmp["gitruvian.env"];
        Write(envFile,
            "GITRUVIAN_PROVIDER=command\n" +
            $"GITRUVIAN_MODEL_COMMAND={PowerShellCommand(FakeModelPath())}\n" +
            "GITRUVIAN_MODEL=modelo-falso-de-teste\n");
        var settings = Config.Load(repo, new Dictionary<string, string> { ["GITRUVIAN_ENV_FILE"] = envFile });
        Assert.Equal("command", settings.Provider);

        Assert.False(Services.InspectRepo(repo, settings).Initialized); // estado vazio: ainda não inicializado
        Assert.Throws<NotInitializedException>(() => Services.TakeSnapshot(repo, settings));
        Services.InitRepo(repo, settings);
        Services.SetTestCommand(repo, settings, TestCommand);
        var (before, _) = Services.RunTests(repo, settings, null);
        Assert.False(before.Passed); // o bug existe antes da alteração

        var index = Services.Reindex(repo, settings);
        Assert.Equal(1, index.Skipped["sensível"]);
        using (var app = AppSession.Open(repo, settings: settings))
            Assert.Contains("calc.ps1", Indexer.Search(app.Store, "desconto").Select(h => h.Path));

        var outcome = Services.Propose(repo, settings, "corrigir o desconto percentual em total", null, []);
        Assert.True(outcome.Applies);
        Assert.Equal("command", outcome.Proposal.Provider);
        Assert.Equal("modelo-falso-de-teste", outcome.Proposal.Model);
        var pid = outcome.Proposal.Id;
        Services.ApplyProposal(repo, settings, pid, null);
        var (run, _) = Services.RunTests(repo, settings, pid);
        Assert.True(run.Passed, run.OutputTail);
        var committed = Services.AcceptProposal(repo, settings, pid, outcome.Proposal.CommitMessage, allowUntested: false);
        Assert.Equal(Status.Committed, committed.Status);

        var log = RunGit(repo, "log", "-1", "--format=%B");
        Assert.StartsWith("Aplica o desconto como percentual", log);
        Assert.Contains("Gitruvian-Proposal:", log);
        Assert.Equal("", RunGit(repo, "status", "--porcelain", "--", "calc.ps1"));
        Assert.DoesNotContain(".gitruvian", RunGit(repo, "status", "--porcelain"));

        var snap = Services.TakeSnapshot(repo, settings);
        var kinds = snap.Events.Select(e => e.Kind).ToList();
        foreach (var kind in new[] { "propose", "apply", "test", "commit" })
            Assert.Contains(kind, kinds);
        Assert.Equal(Status.Committed, snap.Proposals[0].Status);

        var backup = tmp["backup.json"];
        Services.ExportData(repo, settings, backup, includeSamples: true);
        var data = JsonNode.Parse(ReadText(backup))!.AsObject();
        Assert.Equal("committed", data["proposals"]![0]!["status"]!.GetValue<string>());

        var other = MakeRepo(tmp["outro"]);
        Services.InitRepo(other, settings);
        Assert.Equal(1, Services.ImportFile(other, settings, backup).Inserted["proposals"]);
        Assert.Equal(Status.Committed, Services.TakeSnapshot(other, settings).Proposals[0].Status);
    }

    [Fact]
    public void DegradedModeAndValidationErrors()
    {
        using var tmp = new TempDir();
        var repo = MakeRepo(tmp["proj"]);
        var settings = Config.Load(repo, new Dictionary<string, string> { ["GITRUVIAN_ENV_FILE"] = tmp["vazio.env"] });
        Services.InitRepo(repo, settings);
        Assert.Throws<ModelUnavailableException>(() =>
            Services.Propose(repo, settings, "corrigir o desconto", null, []));
        var evil = "--- a/.env\n+++ b/.env\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.Throws<DiffValidationException>(() => Services.Propose(repo, settings, "xxxxx", evil, []));
        Assert.Throws<NotFoundException>(() => Services.GetProposalDetail(repo, settings, "abcd1234"));
        Assert.Empty(Services.TakeSnapshot(repo, settings).Proposals);
    }

    [Fact]
    public void SampleDataLifecycle()
    {
        RequireDotnet(); // os testes do exemplo rodam com dotnet run (compila o projeto; leva alguns segundos)
        using var tmp = new TempDir();
        var settings = new Settings();
        var sample = Services.CreateSample(tmp["loja"], settings);
        Assert.True(File.Exists(Path.Combine(sample, Sample.MarkerFile)));
        Assert.Equal(Sample.TestCommand, Services.TakeSnapshot(sample, settings).TestCommand);
        var proposal = Services.TakeSnapshot(sample, settings).Proposals.Single(p => p.Instruction.Contains("[EXEMPLO]"));
        Assert.Equal([Sample.CarrinhoPath], proposal.Files);
        var (before, _) = Services.RunTests(sample, settings, null);
        Assert.False(before.Passed, before.OutputTail); // o bug do frete grátis existe antes da correção
        Services.ApplyProposal(sample, settings, proposal.ShortId, null);
        Assert.Contains(">= FreteGratisAPartirDe", ReadText(Path.Combine(sample, Sample.CarrinhoPath)));
        var (run, _) = Services.RunTests(sample, settings, proposal.Id);
        Assert.True(run.Passed, run.OutputTail);
        Assert.Contains("PASSOU", run.OutputTail);
        var committed = Services.AcceptProposal(sample, settings, proposal.Id, proposal.CommitMessage, allowUntested: false);
        Assert.Equal(Status.Committed, committed.Status);
        Assert.Equal(1, Services.ClearSamples(sample, settings)["proposals"]);
        Services.RemoveSample(sample);
        Assert.False(Directory.Exists(sample));
    }
}
