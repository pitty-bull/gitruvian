using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitruvian.Core;

public sealed record RepoInfo(string Root, bool Initialized, bool IsSample);

/// <summary>Estado completo de um repositório, lido de uma vez para a interface.</summary>
public sealed record Snapshot
{
    public required string Repo { get; init; }
    public required string Branch { get; init; }
    public string? Head { get; init; }
    public required List<BranchInfo> Branches { get; init; }
    public required List<StatusEntry> Status { get; init; }
    public required List<GraphCommit> Commits { get; init; }
    public required List<GraphRow> Rows { get; init; }
    public required List<Proposal> Proposals { get; init; }
    public required List<Event> Events { get; init; }
    public long IndexFiles { get; init; }
    public string? IndexLast { get; init; }
    public string? TestCommand { get; init; }
    public required string ProviderLabel { get; init; }
    public required string ProviderExplanation { get; init; }
    public bool ProviderReady { get; init; }
    public bool IsSample { get; init; }
    public required string DataDir { get; init; }
    public required Dictionary<Status, int> Counts { get; init; }
}

public sealed record ProposalDetail(Proposal Proposal, List<TestRun> TestRuns, List<Event> Events);

/// <summary>
/// Operações de alto nível usadas pela interface gráfica. Cada função abre e fecha a própria conexão com o
/// banco: assim podem rodar em threads de trabalho sem compartilhar conexões SQLite.
/// </summary>
public static class Services
{
    public const int GraphLimit = 500;
    public const int MaxCommitMessage = 10_000;

    private static AppSession Session(string repo, Settings settings, bool create = false) =>
        AppSession.Open(repo, create, settings);

    // ------------------------------------------------------------------ repositório
    /// <summary>Valida a pasta escolhida e diz se o Gitruvian já foi inicializado nela.</summary>
    public static RepoInfo InspectRepo(string path, Settings settings)
    {
        var root = Git.FindRepoRoot(path);
        var db = Path.Combine(AppSession.DataDirFor(root, settings), AppSession.DbName);
        return new RepoInfo(root, File.Exists(db), Sample.IsSampleRepo(root));
    }

    /// <summary>Cria a pasta de dados local e sugere um comando de testes, se der para detectar.</summary>
    public static string? InitRepo(string repo, Settings settings)
    {
        using var app = Session(repo, settings, create: true);
        var command = Workflow.GetTestCommand(app);
        if (string.IsNullOrWhiteSpace(command))
        {
            command = TestRunner.DetectTestCommand(app.Repo);
            if (command is not null)
                app.Store.SetSetting(Workflow.TestCommandKey, command);
        }
        app.Store.AddEvent("init", "Gitruvian inicializado neste repositório");
        return command;
    }

    public static Snapshot TakeSnapshot(string repo, Settings settings)
    {
        var commits = Git.GraphLog(repo, GraphLimit);
        var (label, explanation, ready) = Llm.Describe(settings);
        using var app = Session(repo, settings);
        var stats = app.Store.IndexStats();
        return new Snapshot
        {
            Repo = app.Repo,
            Branch = Git.CurrentBranch(app.Repo),
            Head = Git.HeadSha(app.Repo),
            Branches = Git.Branches(app.Repo),
            Status = Git.Status(app.Repo),
            Commits = commits,
            Rows = GraphLayout.Layout(commits),
            Proposals = app.Store.ListProposals(limit: 200),
            Events = app.Store.ListEvents(200),
            IndexFiles = stats.Files,
            IndexLast = stats.LastIndexed,
            TestCommand = Workflow.GetTestCommand(app),
            ProviderLabel = label,
            ProviderExplanation = explanation,
            ProviderReady = ready,
            IsSample = Sample.IsSampleRepo(app.Repo),
            DataDir = app.DataDir,
            Counts = app.Store.StatusCounts(),
        };
    }

    // ------------------------------------------------------------------ git básico
    private static List<string> ValidatePaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new ArgumentException("nenhum arquivo selecionado");
        return paths.Select(p => Safety.CheckRepoRelativePath(p, allowSensitive: true)).ToList();
    }

    public static void Stage(string repo, IReadOnlyList<string> paths) => Git.Stage(repo, ValidatePaths(paths));

    public static void Unstage(string repo, IReadOnlyList<string> paths) => Git.Unstage(repo, ValidatePaths(paths));

    public static void Discard(string repo, IReadOnlyList<string> paths, bool untracked) =>
        Git.Discard(repo, ValidatePaths(paths), untracked);

    public static string ValidateCommitMessage(string? message)
    {
        var text = (message ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0)
            throw new ArgumentException("escreva uma mensagem de commit");
        if (text.Length > MaxCommitMessage)
            throw new ArgumentException($"mensagem de commit longa demais (máximo {MaxCommitMessage} caracteres)");
        if (text.Contains('\0'))
            throw new ArgumentException("a mensagem contém caracteres inválidos");
        return text + "\n";
    }

    public static string CommitStaged(string repo, Settings settings, string message)
    {
        var text = ValidateCommitMessage(message);
        if (!Git.Status(repo).Any(e => e.IsStaged))
            throw new ArgumentException("nenhuma alteração preparada (stage) para commitar");
        var sha = Git.Commit(repo, text);
        using var app = Session(repo, settings);
        app.Store.AddEvent("git_commit", $"Commit manual {sha[..10]} criado", data: new JsonObject { ["commit_sha"] = sha });
        return sha;
    }

    public static void SwitchBranch(string repo, string name, bool create)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("informe o nome do branch");
        if (create) Git.CreateBranch(repo, name);
        else Git.Switch(repo, name);
    }

    public static string HiddenNotice(string path) =>
        $"(conteúdo oculto: {path} parece conter segredos ou credenciais. Abra o arquivo no seu editor se precisar revisá-lo.)";

    public static string FileDiff(string repo, string path, bool staged, bool untracked)
    {
        path = Safety.CheckRepoRelativePath(path, allowSensitive: true);
        return Safety.IsSensitivePath(path) ? HiddenNotice(path) : Git.WorkingFileDiff(repo, path, staged, untracked);
    }

    /// <summary>Diff de um commit (ou de um arquivo dele), sem mostrar o conteúdo de arquivos sensíveis.</summary>
    public static string CommitDiff(string repo, string sha, string? path = null)
    {
        if (path is not null)
        {
            path = Safety.CheckRepoRelativePath(path, allowSensitive: true);
            return Safety.IsSensitivePath(path) ? HiddenNotice(path) : Git.CommitFileDiff(repo, sha, path);
        }
        var detail = Git.CommitDetail(repo, sha);
        var hidden = detail.Files.Select(f => f.Path).Where(Safety.IsSensitivePath).ToList();
        var args = new List<string> { "show", "--no-ext-diff", "--format=", "-M", detail.Sha, "--", "." };
        args.AddRange(hidden.Select(p => $":(exclude,literal){p}"));
        var text = Git.Run(repo, args).Stdout;
        if (hidden.Count > 0)
            text += "\n" + string.Join('\n', hidden.Select(HiddenNotice)) + "\n";
        return text;
    }

    // --------------------------------------------------------------- assistente
    public static IndexResult Reindex(string repo, Settings settings)
    {
        using var app = Session(repo, settings);
        var result = Indexer.IndexRepo(app.Store, app.Repo);
        var skipped = new JsonObject();
        foreach (var (k, v) in result.Skipped) skipped[k] = v;
        app.Store.AddEvent("index", $"Índice atualizado: +{result.Added} ~{result.Updated} -{result.Removed}",
            data: new JsonObject
            {
                ["added"] = result.Added, ["updated"] = result.Updated, ["removed"] = result.Removed,
                ["unchanged"] = result.Unchanged, ["skipped"] = skipped,
            });
        return result;
    }

    public static List<string> ParseContextFiles(string? text) =>
        (text ?? "").Replace(';', ',').Replace('\n', ',').Split(',')
            .Select(p => p.Trim()).Where(p => p.Length > 0)
            .Select(p => Safety.CheckRepoRelativePath(p)).ToList();

    public static ProposeOutcome Propose(string repo, Settings settings, string instruction, string? patchText,
        IReadOnlyList<string> files, IModelProvider? provider = null)
    {
        Safety.CleanInstruction(instruction); // valida antes de qualquer trabalho caro
        using var app = Session(repo, settings);
        if (patchText is null && app.Store.IndexStats().Files == 0)
            Indexer.IndexRepo(app.Store, app.Repo);
        return Workflow.Propose(app, instruction, patchText, files.Count > 0 ? files : null, provider);
    }

    public static ProposalDetail GetProposalDetail(string repo, Settings settings, string proposalId)
    {
        using var app = Session(repo, settings);
        var p = app.Store.GetProposal(proposalId);
        return new ProposalDetail(p, app.Store.ListTestRuns(p.Id, 20), app.Store.ListEvents(50, p.Id));
    }

    public static Proposal ApplyProposal(string repo, Settings settings, string proposalId, string? branch)
    {
        using var app = Session(repo, settings);
        return Workflow.Apply(app, proposalId, string.IsNullOrWhiteSpace(branch) ? null : branch.Trim());
    }

    public static (TestRun Run, Proposal? Proposal) RunTests(string repo, Settings settings, string? proposalId)
    {
        using var app = Session(repo, settings);
        return Workflow.RunTests(app, proposalId);
    }

    public static Proposal AcceptProposal(string repo, Settings settings, string proposalId, string message,
        bool allowUntested)
    {
        var text = ValidateCommitMessage(message);
        using var app = Session(repo, settings);
        return Workflow.Commit(app, proposalId, text, allowUntested);
    }

    public static Proposal RevertProposal(string repo, Settings settings, string proposalId)
    {
        using var app = Session(repo, settings);
        return Workflow.Revert(app, proposalId);
    }

    public static Proposal RejectProposal(string repo, Settings settings, string proposalId)
    {
        using var app = Session(repo, settings);
        return Workflow.Reject(app, proposalId);
    }

    public static void SetTestCommand(string repo, Settings settings, string command)
    {
        command = command.Trim();
        if (command.Contains('\n') || command.Contains('\0') || command.Length > 1000)
            throw new ArgumentException("comando de testes inválido (uma linha, até 1000 caracteres)");
        if (command.Length > 0)
            CommandLine.Split(command); // valida as aspas
        using var app = Session(repo, settings);
        if (command.Length > 0) app.Store.SetSetting(Workflow.TestCommandKey, command);
        else app.Store.DeleteSetting(Workflow.TestCommandKey);
    }

    // ------------------------------------------------------- importação/exportação
    public static Dictionary<string, int> ExportData(string repo, Settings settings, string target, bool includeSamples)
    {
        using var app = Session(repo, settings);
        var counts = Transfer.ExportToFile(app.Store, target, includeSamples);
        var data = new JsonObject();
        foreach (var (k, v) in counts) data[k] = v;
        app.Store.AddEvent("export", "Dados exportados", data: data);
        return counts;
    }

    public static ImportReport ImportFile(string repo, Settings settings, string source)
    {
        var data = Transfer.LoadExportFile(source);
        using var app = Session(repo, settings);
        return Transfer.Import(app.Store, data);
    }

    // ----------------------------------------------------------- dados de exemplo
    public static string CreateSample(string target, Settings settings) => Sample.Create(target, settings).Repo;

    public static Dictionary<string, int> ClearSamples(string repo, Settings settings)
    {
        using var app = Session(repo, settings);
        return app.Store.DeleteSamples();
    }

    public static void RemoveSample(string path) => Sample.Remove(path);
}

/// <summary>Lista de repositórios recentes em recent.json (pasta de configuração do usuário).</summary>
public static class RecentRepos
{
    public const string FileName = "recent.json";
    public const int MaxRecent = 12;

    public static string DefaultPath => Path.Combine(Config.UserConfigDir(), FileName);

    /// <summary>Lê a lista, descartando entradas inválidas ou que não existem mais.</summary>
    public static List<string> Load(string? path = null)
    {
        path ??= DefaultPath;
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
        if (raw is not JsonObject obj || obj["repos"] is not JsonArray items)
            return [];
        var result = new List<string>();
        foreach (var item in items.Take(MaxRecent))
        {
            if (item is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                continue;
            var p = v.GetValue<string>();
            if (p.Length is > 0 and < 1000 && !p.Contains('\0') && Path.IsPathFullyQualified(p) &&
                Directory.Exists(p) && !result.Contains(p, StringComparer.OrdinalIgnoreCase))
                result.Add(p);
        }
        return result;
    }

    public static void Save(IEnumerable<string> repos, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new JsonObject { ["repos"] = new JsonArray(repos.Take(MaxRecent).Select(r => (JsonNode?)r).ToArray()) };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static List<string> Remember(string repo, string? path = null)
    {
        var repos = Load(path).Where(p => !p.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();
        repos.Insert(0, repo);
        Save(repos, path);
        return repos.Take(MaxRecent).ToList();
    }

    public static List<string> Forget(string repo, string? path = null)
    {
        var repos = Load(path).Where(p => !p.Equals(repo, StringComparison.OrdinalIgnoreCase)).ToList();
        Save(repos, path);
        return repos;
    }
}

/// <summary>Preferências do app (não do repositório) em preferences.json, ao lado do recent.json.</summary>
public sealed record Preferences
{
    public const string FileName = "preferences.json";

    /// <summary>null = ainda não perguntado; true/false = resposta do usuário.</summary>
    public bool? AutoUpdate { get; init; }

    public static string DefaultPath => Path.Combine(Config.UserConfigDir(), FileName);

    public static Preferences Load(string? path = null)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path ?? DefaultPath, Encoding.UTF8)) is JsonObject obj &&
                obj["auto_update"] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                return new Preferences { AutoUpdate = v.GetValue<bool>() };
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new Preferences();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var obj = new JsonObject { ["auto_update"] = AutoUpdate };
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}
