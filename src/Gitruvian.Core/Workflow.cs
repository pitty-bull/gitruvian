using System.Text.Json.Nodes;

namespace Gitruvian.Core;

/// <summary>Erro de fluxo com mensagem pronta para o usuário.</summary>
public sealed class WorkflowException(string message) : InvalidOperationException(message);

public sealed record ProposeOutcome(Proposal Proposal, ContextPack? Context, bool Applies);

/// <summary>Ciclo principal: propor → validar → aplicar → testar → commitar/reverter, registrando tudo.</summary>
public static class Workflow
{
    public const string TestCommandKey = "test_command";

    public static string? GetTestCommand(AppSession app)
    {
        if (app.Settings.TestCommand.Length > 0)
            return app.Settings.TestCommand;
        return app.Store.GetSetting(TestCommandKey);
    }

    public static string DefaultCommitMessage(string instruction) =>
        Safety.Truncate(instruction.Trim().Split('\n', 2)[0], 72);

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    /// <summary>Cria uma proposta a partir de um diff manual ou de uma chamada ao modelo.</summary>
    public static ProposeOutcome Propose(AppSession app, string instruction, string? patchText = null,
        IReadOnlyList<string>? files = null, IModelProvider? provider = null)
    {
        instruction = Safety.CleanInstruction(instruction);
        ContextPack? context = null;
        long? latency = null;
        string? commitMessage = null;
        string providerName, modelName, diff;
        if (patchText is not null)
        {
            (providerName, modelName) = ("manual", "-");
            try
            {
                diff = Diffs.ExtractDiff(patchText);
            }
            catch (DiffValidationException)
            {
                diff = patchText;
            }
        }
        else
        {
            provider ??= Llm.GetProvider(app.Settings);
            (providerName, modelName) = (provider.Name, provider.Model);
            context = Indexer.BuildContext(app.Store, app.Repo, instruction, app.Settings.MaxContextChars, files);
            Log.Info($"model_call provider={providerName} model={modelName} context_files={context.Entries.Count} " +
                     $"context_chars={context.Chars}");
            ModelReply reply;
            try
            {
                reply = provider.Complete(Llm.SystemPrompt, Llm.BuildUserPrompt(instruction, context.Text));
            }
            catch (ModelException exc)
            {
                app.Store.AddEvent("propose_failed", $"Falha ao consultar o modelo: {exc.Message}",
                    data: new JsonObject { ["provider"] = providerName, ["model"] = modelName });
                throw;
            }
            (latency, modelName) = (reply.LatencyMs, reply.Model);
            commitMessage = Diffs.ExtractCommitMessage(reply.Text);
            try
            {
                diff = Diffs.ExtractDiff(reply.Text);
            }
            catch (DiffValidationException exc)
            {
                app.Store.AddEvent("propose_failed", "O modelo não devolveu um diff utilizável",
                    data: new JsonObject { ["provider"] = providerName, ["model"] = modelName, ["latency_ms"] = latency });
                Log.Info($"model_reply_without_diff provider={providerName} latency_ms={latency}");
                throw new DiffValidationException(
                    $"{exc.Message}. Tente reformular ou indicar arquivos no campo de contexto.");
            }
        }

        ParsedDiff parsed;
        try
        {
            parsed = Diffs.Validate(diff);
        }
        catch (DiffValidationException exc)
        {
            app.Store.AddEvent("propose_failed", $"Diff rejeitado na validação: {exc.Message}",
                data: new JsonObject { ["provider"] = providerName, ["model"] = modelName });
            Log.Info($"diff_rejected provider={providerName} reason={exc.Message}");
            throw;
        }
        var (applies, checkError, _) = Git.ApplyCheck(app.Repo, parsed.Text);
        if (string.IsNullOrEmpty(commitMessage) || commitMessage.Equals("none", StringComparison.OrdinalIgnoreCase))
            commitMessage = DefaultCommitMessage(instruction);
        var proposal = app.Store.AddProposal(instruction, providerName, modelName, parsed.Text, parsed.Files,
            commitMessage, context?.Entries ?? [], latency, applies ? null : Safety.Truncate(checkError, 1000));
        app.Store.AddEvent("propose",
            $"Proposta criada via {providerName}: {parsed.Files.Count} arquivo(s), +{parsed.Additions} -{parsed.Deletions}",
            proposal.Id, new JsonObject
            {
                ["files"] = Array(parsed.Files),
                ["additions"] = parsed.Additions,
                ["deletions"] = parsed.Deletions,
                ["provider"] = providerName,
                ["model"] = modelName,
                ["latency_ms"] = latency,
                ["context_files"] = Array((context?.Entries ?? []).Select(e => e["path"])),
                ["context_chars"] = context?.Chars ?? 0,
                ["applies_cleanly"] = applies,
            });
        Log.Info($"proposal_created id={proposal.ShortId} provider={providerName} files={parsed.Files.Count} applies={applies}");
        return new ProposeOutcome(proposal, context, applies);
    }

    private static void Require(Proposal proposal, IEnumerable<Status> allowed, string action)
    {
        if (!allowed.Contains(proposal.Status))
            throw new WorkflowException(
                $"não é possível {action}: a proposta {proposal.ShortId} está '{proposal.StatusLabel}'");
    }

    public static Proposal Apply(AppSession app, string proposalId, string? branch = null)
    {
        var (store, repo) = (app.Store, app.Repo);
        var p = store.GetProposal(proposalId);
        Require(p, [Status.Proposed, Status.Reverted], "aplicar");
        var dirty = Git.DirtyPaths(repo, p.Files);
        if (dirty.Count > 0)
            throw new WorkflowException(
                "estes arquivos têm alterações não commitadas e seriam misturados ao diff: " +
                string.Join(", ", dirty) + ". Faça commit ou descarte as alterações antes.");
        try
        {
            if (!string.IsNullOrWhiteSpace(branch))
                Git.CreateBranch(repo, branch.Trim());
            Git.ApplyPatch(repo, p.Diff);
        }
        catch (GitException exc)
        {
            store.AddEvent("apply_failed", $"Falha ao aplicar: {Safety.Truncate(exc.Message, 300)}", p.Id,
                isSample: p.IsSample);
            throw new WorkflowException(exc.Message);
        }
        p = store.Transition(p.Id, Status.Applied, new Dictionary<string, object?>
        {
            ["base_commit"] = Git.HeadSha(repo),
            ["branch"] = Git.CurrentBranch(repo),
        });
        store.AddEvent("apply", $"Diff aplicado em {p.Files.Count} arquivo(s) no branch {p.Branch}", p.Id,
            new JsonObject { ["files"] = Array(p.Files), ["base_commit"] = p.BaseCommit, ["branch"] = p.Branch },
            p.IsSample);
        Log.Info($"proposal_applied id={p.ShortId} files={p.Files.Count}");
        return p;
    }

    public static (TestRun Run, Proposal? Proposal) RunTests(AppSession app, string? proposalId = null)
    {
        var store = app.Store;
        var p = proposalId is not null ? store.GetProposal(proposalId) : null;
        var command = GetTestCommand(app);
        if (string.IsNullOrWhiteSpace(command))
            throw new WorkflowException(
                "nenhum comando de testes configurado. Defina um em Repositório › Configurações (ex.: dotnet test).");
        CommandResult result;
        try
        {
            result = TestRunner.Run(command, app.Repo, app.Settings.TestTimeout);
        }
        catch (ArgumentException exc)
        {
            throw new WorkflowException(exc.Message);
        }
        var isSample = p?.IsSample ?? false;
        var run = store.AddTestRun(command, result.DurationMs, result.ExitCode, result.OutputTail, p?.Id,
            isSample: isSample);
        var verdict = run.Passed ? "passaram" : $"falharam (código {run.ExitCode})";
        if (p is not null && p.Status is Status.Applied or Status.TestsPassed or Status.TestsFailed)
            p = store.Transition(p.Id, run.Passed ? Status.TestsPassed : Status.TestsFailed);
        store.AddEvent("test", $"Testes {verdict} em {run.DurationMs} ms", p?.Id, new JsonObject
        {
            ["command"] = command,
            ["exit_code"] = run.ExitCode,
            ["duration_ms"] = run.DurationMs,
            ["test_run"] = run.Id,
        }, isSample);
        Log.Info($"tests_run proposal={p?.ShortId ?? "-"} exit={run.ExitCode} duration_ms={run.DurationMs}");
        return (run, p);
    }

    public static Proposal Commit(AppSession app, string proposalId, string? message = null, bool allowUntested = false)
    {
        var (store, repo) = (app.Store, app.Repo);
        var p = store.GetProposal(proposalId);
        if (p.Status == Status.TestsFailed)
            throw new WorkflowException("os testes desta proposta falharam. Corrija e rode os testes de novo, ou reverta.");
        Require(p, [Status.Applied, Status.TestsPassed], "commitar");
        if (p.Status == Status.Applied && !string.IsNullOrWhiteSpace(GetTestCommand(app)) && !allowUntested)
            throw new WorkflowException("rode os testes antes ou marque \"Commitar sem testes\".");
        var body = (message ?? p.CommitMessage).Trim();
        if (body.Length == 0)
            throw new WorkflowException("mensagem de commit vazia");
        var fullMessage = $"{Safety.Truncate(body, 2000)}\n\nGitruvian-Proposal: {p.Id}\n";
        string sha;
        try
        {
            sha = Git.Commit(repo, fullMessage, p.Files);
        }
        catch (GitException exc)
        {
            store.AddEvent("commit_failed", $"Falha no commit: {Safety.Truncate(exc.Message, 300)}", p.Id,
                isSample: p.IsSample);
            throw new WorkflowException(exc.Message);
        }
        p = store.Transition(p.Id, Status.Committed, new Dictionary<string, object?> { ["commit_sha"] = sha });
        store.AddEvent("commit", $"Commit {sha[..10]} criado", p.Id,
            new JsonObject { ["commit_sha"] = sha, ["files"] = Array(p.Files) }, p.IsSample);
        Log.Info($"proposal_committed id={p.ShortId} sha={sha[..10]}");
        return p;
    }

    public static Proposal Revert(AppSession app, string proposalId)
    {
        var (store, repo) = (app.Store, app.Repo);
        var p = store.GetProposal(proposalId);
        Require(p, [Status.Applied, Status.TestsPassed, Status.TestsFailed, Status.Committed], "reverter");
        var data = new JsonObject { ["files"] = Array(p.Files) };
        string summary;
        try
        {
            if (p.Status == Status.Committed && p.CommitSha is not null)
            {
                var revertSha = Git.RevertCommit(repo, p.CommitSha);
                data["revert_commit"] = revertSha;
                summary = $"Commit {p.CommitSha[..10]} revertido por {revertSha[..10]}";
            }
            else
            {
                Git.ApplyPatch(repo, p.Diff, reverse: true);
                summary = "Diff desfeito na árvore de trabalho";
            }
        }
        catch (GitException exc)
        {
            store.AddEvent("revert_failed", $"Falha ao reverter: {Safety.Truncate(exc.Message, 300)}", p.Id,
                isSample: p.IsSample);
            throw new WorkflowException(exc.Message);
        }
        p = store.Transition(p.Id, Status.Reverted);
        store.AddEvent("revert", summary, p.Id, data, p.IsSample);
        Log.Info($"proposal_reverted id={p.ShortId}");
        return p;
    }

    public static Proposal Reject(AppSession app, string proposalId)
    {
        var p = app.Store.GetProposal(proposalId);
        Require(p, [Status.Proposed, Status.Reverted], "rejeitar");
        p = app.Store.Transition(p.Id, Status.Rejected);
        app.Store.AddEvent("reject", "Proposta rejeitada", p.Id, isSample: p.IsSample);
        Log.Info($"proposal_rejected id={p.ShortId}");
        return p;
    }
}
