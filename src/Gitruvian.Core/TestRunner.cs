namespace Gitruvian.Core;

public sealed record CommandResult(int ExitCode, long DurationMs, string OutputTail)
{
    public bool Passed => ExitCode == 0;
}

/// <summary>Detecção e execução do comando de testes do repositório.</summary>
public static class TestRunner
{
    public const int OutputTailLines = 60;
    public const int OutputTailChars = 6000;

    /// <summary>
    /// Ambiente dos comandos de testes: desliga a telemetria e as mensagens de boas-vindas da CLI do .NET e
    /// os servidores de build que ficariam rodando (e prendendo arquivos) depois do teste.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
        ["UseSharedCompilation"] = "false",
    };

    /// <summary>Sugere um comando de testes a partir dos arquivos na raiz do projeto.</summary>
    public static string? DetectTestCommand(string repo)
    {
        bool Has(string name) => File.Exists(Path.Combine(repo, name));
        bool Any(string pattern) => Directory.EnumerateFiles(repo, pattern).Any();
        if (Any("*.sln") || Any("*.slnx") || Any("*.csproj"))
            return "dotnet test";
        if (Has("package.json"))
            return "npm test";
        if (Has("Cargo.toml"))
            return "cargo test";
        if (Has("go.mod"))
            return "go test ./...";
        return null;
    }

    /// <summary>Divide o comando em argumentos (sem shell).</summary>
    public static List<string> ExpandCommand(string command)
    {
        var argv = CommandLine.Split(command);
        if (argv.Count == 0)
            throw new ArgumentException("comando de testes vazio");
        return argv;
    }

    public static string Tail(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var joined = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - OutputTailLines)));
        if (joined.Length > OutputTailChars)
            joined = joined[^OutputTailChars..];
        return Safety.Redact(joined);
    }

    public static CommandResult Run(string command, string repo, int timeout)
    {
        var argv = ExpandCommand(command);
        ProcessResult result;
        try
        {
            result = Processes.Run(argv, repo, null, timeout, new Dictionary<string, string>(Environment));
        }
        catch (FileNotFoundException)
        {
            return new CommandResult(127, 0, $"comando não encontrado: {argv[0]}");
        }
        var output = result.StdoutText + "\n" + result.StderrText;
        if (result.TimedOut)
            return new CommandResult(124, result.DurationMs, Tail(output) + $"\n[tempo esgotado após {timeout}s]");
        return new CommandResult(result.ExitCode, result.DurationMs, Tail(output));
    }
}
