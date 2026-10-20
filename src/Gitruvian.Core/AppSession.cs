namespace Gitruvian.Core;

/// <summary>O repositório ainda não tem a pasta de dados do Gitruvian.</summary>
public sealed class NotInitializedException(string message) : InvalidOperationException(message);

/// <summary>Repositório aberto: raiz, pasta de dados e conexão com o banco (uma por operação).</summary>
public sealed class AppSession : IDisposable
{
    public const string DataDirName = ".gitruvian";
    public const string DbName = "gitruvian.db";
    private const string ExcludeLine = "/" + DataDirName + "/";

    public required string Repo { get; init; }
    public required string DataDir { get; init; }
    public required Store Store { get; init; }
    public required Settings Settings { get; init; }
    public string DbPath => Path.Combine(DataDir, DbName);

    public void Dispose() => Store.Dispose();

    public static string DataDirFor(string repo, Settings settings)
    {
        if (settings.DataDir.Length == 0)
            return Path.Combine(repo, DataDirName);
        // Nome + hash do caminho: dois repositórios com o mesmo nome não colidem.
        var full = Path.GetFullPath(repo);
        var suffix = Clock.Sha256(full)[..8];
        var baseDir = Environment.ExpandEnvironmentVariables(settings.DataDir);
        if (baseDir.StartsWith('~'))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + baseDir[1..];
        return Path.Combine(Path.GetFullPath(baseDir), $"{Path.GetFileName(full)}-{suffix}");
    }

    private static bool IsInside(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Evita que .gitruvian/ seja commitado, sem mexer no .gitignore do usuário.</summary>
    private static void EnsureExcluded(string repo)
    {
        var exclude = Path.Combine(Git.GitDir(repo), "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        var current = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
        if (current.Split('\n').Any(l => l.TrimEnd('\r') == ExcludeLine))
            return;
        var prefix = current.Length > 0 && !current.EndsWith('\n') ? "\n" : "";
        File.AppendAllText(exclude, $"{prefix}# Gitruvian (dados locais)\n{ExcludeLine}\n");
    }

    public static AppSession Open(string path, bool create = false, Settings? settings = null)
    {
        settings ??= Config.Load();
        var repo = Git.FindRepoRoot(path);
        var dataDir = DataDirFor(repo, settings);
        var dbPath = Path.Combine(dataDir, DbName);
        if (!File.Exists(dbPath))
        {
            if (!create)
                throw new NotInitializedException($"repositório ainda não inicializado no Gitruvian ({repo})");
            Directory.CreateDirectory(dataDir);
            if (IsInside(dataDir, repo))
                EnsureExcluded(repo);
        }
        var store = Store.Open(dbPath);
        Log.Setup(dataDir);
        return new AppSession { Repo = repo, DataDir = dataDir, Store = store, Settings = settings };
    }
}
