using static Gitruvian.Core.Tests.TestSupport;

namespace Gitruvian.Core.Tests;

/// <summary>Fluxo principal: propor → aplicar → testar → commitar/reverter, com registro.</summary>
public sealed class WorkflowTests : IDisposable
{
    private readonly TestApp _t = new();
    private AppSession App => _t.App;

    public void Dispose() => _t.Dispose();

    private string Calc() => ReadText(_t.File("calc.ps1")).Replace("\r\n", "\n");

    [Fact]
    public void FullCycleWithManualPatch()
    {
        RequireTestShell();
        var outcome = Workflow.Propose(App, "Aplicar desconto percentual", FixDiff);
        var p = outcome.Proposal;
        Assert.True(outcome.Applies);
        Assert.Equal(Status.Proposed, p.Status);
        Assert.Equal(["calc.ps1"], p.Files);
        Assert.Equal("manual", p.Provider);

        p = Workflow.Apply(App, p.ShortId);
        Assert.Equal(Status.Applied, p.Status);
        Assert.False(string.IsNullOrEmpty(p.BaseCommit));
        Assert.Contains("$subtotal * $Discount", Calc());

        var (run, after) = Workflow.RunTests(App, p.Id);
        Assert.True(run.Passed, run.OutputTail);
        Assert.Equal(Status.TestsPassed, after!.Status);

        p = Workflow.Commit(App, p.Id);
        Assert.Equal(Status.Committed, p.Status);
        var message = RunGit(_t.Repo, "log", "-1", "--format=%B");
        Assert.Contains("Aplicar desconto percentual", message);
        Assert.Contains($"Gitruvian-Proposal: {p.Id}", message);
        Assert.Equal(p.CommitSha, RunGit(_t.Repo, "rev-parse", "HEAD").Trim());
        Assert.Equal(["propose", "apply", "test", "commit"], _t.Kinds());

        p = Workflow.Revert(App, p.Id);
        Assert.Equal(Status.Reverted, p.Status);
        Assert.Equal(CalcBuggy, Calc());
    }

    [Fact]
    public void FailingTestsBlockCommitAndRevertRestores()
    {
        RequireTestShell();
        var p = Workflow.Propose(App, "Mudança errada", BadDiff).Proposal;
        Workflow.Apply(App, p.Id);
        var (run, after) = Workflow.RunTests(App, p.Id);
        Assert.False(run.Passed);
        Assert.Equal(Status.TestsFailed, after!.Status);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("FALHA desconto percentual", run.OutputTail);
        var exc = Assert.Throws<WorkflowException>(() => Workflow.Commit(App, p.Id));
        Assert.Contains("falharam", exc.Message);
        Workflow.Revert(App, p.Id);
        Assert.Equal(CalcBuggy, Calc());
        Assert.Equal(Status.Reverted, _t.Store.GetProposal(p.Id).Status);
    }

    [Fact]
    public void CommitRequiresTestsUnlessExplicit()
    {
        var p = Workflow.Propose(App, "Desconto", FixDiff).Proposal;
        Workflow.Apply(App, p.Id);
        var exc = Assert.Throws<WorkflowException>(() => Workflow.Commit(App, p.Id));
        Assert.Contains("testes", exc.Message);
        Assert.Equal(Status.Committed, Workflow.Commit(App, p.Id, allowUntested: true).Status);
    }

    [Fact]
    public void ApplyRefusesDirtyFiles()
    {
        var p = Workflow.Propose(App, "Desconto", FixDiff).Proposal;
        Write(_t.File("calc.ps1"), CalcBuggy + "# edição local\n");
        var exc = Assert.Throws<WorkflowException>(() => Workflow.Apply(App, p.Id));
        Assert.Contains("não commitadas", exc.Message);
        Assert.Equal(Status.Proposed, _t.Store.GetProposal(p.Id).Status);
    }

    [Fact]
    public void ProposalThatDoesNotApplyIsRecordedButFlagged()
    {
        var stale = FixDiff.Replace("($Prices | Measure-Object -Sum).Sum", "($Prices | Measure-Object -Sum -Average).Sum");
        var outcome = Workflow.Propose(App, "Desconto", stale);
        Assert.False(outcome.Applies);
        Assert.False(string.IsNullOrEmpty(outcome.Proposal.CheckError));
        Assert.Throws<WorkflowException>(() => Workflow.Apply(App, outcome.Proposal.Id));
        Assert.Contains("apply_failed", _t.Kinds());
    }

    [Fact]
    public void RejectAndStateGuards()
    {
        var p = Workflow.Propose(App, "Desconto", FixDiff).Proposal;
        Workflow.Reject(App, p.Id);
        Assert.Throws<WorkflowException>(() => Workflow.Apply(App, p.Id));
    }

    [Fact]
    public void ModelProposalSendsContextWithoutSecrets()
    {
        Indexer.IndexRepo(_t.Store, _t.Repo);
        var provider = new RecordingProvider($"COMMIT: Corrige desconto percentual\n```diff\n{FixDiff}```\n");
        var outcome = Workflow.Propose(App, "corrigir o desconto percentual em total", provider: provider);
        Assert.True(outcome.Applies);
        Assert.Equal("Corrige desconto percentual", outcome.Proposal.CommitMessage);
        Assert.Equal("teste", outcome.Proposal.Provider);
        Assert.Equal(5, outcome.Proposal.LatencyMs);
        var prompt = provider.Prompts[0];
        Assert.Contains("### ARQUIVO: calc.ps1", prompt);
        Assert.DoesNotContain(SecretValue, prompt);
        var ev = _t.Store.ListEvents(proposalId: outcome.Proposal.Id)[0];
        Assert.NotEmpty(ev.Data["context_files"]!.AsArray());
        Assert.DoesNotContain("subtotal", ev.Data.ToJsonString());
    }

    [Fact]
    public void ModelReplyWithoutDiffIsRecordedAsFailure()
    {
        var provider = new RecordingProvider("Desculpe, não consigo.");
        Assert.Throws<DiffValidationException>(() => Workflow.Propose(App, "algo impossível", provider: provider));
        Assert.Empty(_t.Store.ListProposals());
        Assert.Equal(["propose_failed"], _t.Kinds());
    }

    [Fact]
    public void ModelCannotTouchProtectedPaths()
    {
        var evil = FixDiff.Replace("calc.ps1", ".git/hooks/post-checkout");
        Assert.Throws<DiffValidationException>(() =>
            Workflow.Propose(App, "xxxxx", provider: new RecordingProvider($"```diff\n{evil}```")));
    }

    [Fact]
    public void DegradedModeWithoutModel()
    {
        // Sem modelo configurado, a mensagem explica o modo manual da interface.
        var exc = Assert.Throws<ModelUnavailableException>(() => Workflow.Propose(App, "corrigir algo"));
        Assert.Contains("modo manual", exc.Message);
    }

    [Fact]
    public void RemoteProviderRequiresExplicitConsent()
    {
        var remote = new AppSession
        {
            Repo = App.Repo, DataDir = App.DataDir, Store = App.Store,
            Settings = new Settings { Provider = "anthropic", Model = "claude-opus-5-5", AnthropicApiKey = "k" },
        };
        var exc = Assert.Throws<ModelUnavailableException>(() => Workflow.Propose(remote, "corrigir algo"));
        Assert.Contains("GITRUVIAN_ALLOW_REMOTE", exc.Message);
    }

    [Fact]
    public void LogHasMetadataOnly()
    {
        RequireTestShell();
        Indexer.IndexRepo(_t.Store, _t.Repo);
        var provider = new RecordingProvider($"```diff\n{FixDiff}```");
        var p = Workflow.Propose(App, "corrigir o desconto percentual", provider: provider).Proposal;
        Workflow.Apply(App, p.Id);
        Workflow.RunTests(App, p.Id);
        var log = ReadText(Path.Combine(App.DataDir, "gitruvian.log"));
        Assert.Contains("proposal_created", log);
        Assert.Contains("tests_run", log);
        foreach (var forbidden in new[] { "subtotal", "Get-Total", SecretValue, "desconto percentual" })
            Assert.DoesNotContain(forbidden, log);
    }

    [Fact]
    public void ApplyAndRevertWithCrlfWorktree()
    {
        // Clones no Windows costumam ter CRLF na árvore de trabalho; diffs de modelos vêm em LF.
        RequireTestShell();
        RunGit(_t.Repo, "config", "core.autocrlf", "true");
        File.Delete(_t.File("calc.ps1"));
        RunGit(_t.Repo, "checkout", "--", "calc.ps1");
        Assert.Contains("\r\n", ReadText(_t.File("calc.ps1")));
        var outcome = Workflow.Propose(App, "Desconto", FixDiff);
        Assert.True(outcome.Applies);
        var p = Workflow.Apply(App, outcome.Proposal.Id);
        var (run, _) = Workflow.RunTests(App, p.Id);
        Assert.True(run.Passed, run.OutputTail);
        Assert.Equal(["1", "1", "calc.ps1"],
            RunGit(_t.Repo, "diff", "--numstat").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Workflow.Revert(App, p.Id);
        Assert.Equal("", RunGit(_t.Repo, "status", "--porcelain", "--", "calc.ps1"));
    }
}
