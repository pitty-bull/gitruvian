using System.Diagnostics;
using System.Text;

namespace Gitruvian.Core;

public sealed record ProcessResult(int ExitCode, byte[] Stdout, byte[] Stderr, bool TimedOut, long DurationMs)
{
    public string StdoutText => Decode(Stdout);
    public string StderrText => Decode(Stderr);

    public static string Decode(byte[] data) => Encoding.UTF8.GetString(data);
}

/// <summary>Executa programas sem shell, sempre com lista de argumentos e sem abrir janela de console.</summary>
public static class Processes
{
    /// <summary>Procura um executável no PATH (no Windows, também com as extensões de PATHEXT).</summary>
    public static string? Which(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        bool HasExtension(string candidate) =>
            extensions.Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        IEnumerable<string> Candidates(string basePath)
        {
            if (!OperatingSystem.IsWindows() || HasExtension(basePath))
                yield return basePath;
            foreach (var ext in extensions)
                yield return basePath + ext.ToLowerInvariant();
        }

        if (name.Contains('/') || name.Contains('\\') || Path.IsPathRooted(name))
            return Candidates(name).FirstOrDefault(File.Exists);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs)
        {
            string basePath;
            try
            {
                basePath = Path.Combine(dir.Trim('"'), name);
            }
            catch (ArgumentException)
            {
                continue;
            }
            var found = Candidates(basePath).FirstOrDefault(File.Exists);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Roda <paramref name="argv"/> em <paramref name="cwd"/>. Lança <see cref="FileNotFoundException"/>
    /// se o programa não existe. Em caso de tempo esgotado, encerra a árvore de processos.
    /// </summary>
    public static ProcessResult Run(IReadOnlyList<string> argv, string? cwd, byte[]? input = null,
        int timeoutSeconds = 120, IDictionary<string, string>? env = null)
    {
        if (argv.Count == 0)
            throw new ArgumentException("comando vazio");
        var exe = Which(argv[0]) ?? throw new FileNotFoundException($"comando não encontrado: {argv[0]}", argv[0]);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (var arg in argv.Skip(1))
            psi.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (key, value) in env)
                psi.Environment[key] = value;

        var watch = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception exc)
        {
            throw new FileNotFoundException($"não foi possível executar {argv[0]}: {exc.Message}", argv[0]);
        }
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var outTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errTask = proc.StandardError.BaseStream.CopyToAsync(stderr);
        try
        {
            if (input is not null)
                proc.StandardInput.BaseStream.Write(input);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // O programa terminou sem ler a entrada; o código de saída conta a história.
        }

        var timedOut = !proc.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds));
        if (timedOut)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            proc.WaitForExit(5000);
        }
        else
        {
            proc.WaitForExit();
        }
        Task.WaitAll([outTask, errTask], TimeSpan.FromSeconds(10));
        return new ProcessResult(timedOut ? 124 : proc.ExitCode, stdout.ToArray(), stderr.ToArray(), timedOut,
            watch.ElapsedMilliseconds);
    }
}

/// <summary>Divisão de linhas de comando em argumentos, sem shell.</summary>
public static class CommandLine
{
    /// <summary>
    /// Divide por espaços respeitando aspas duplas (e simples fora do Windows). As aspas não fazem
    /// parte do argumento resultante.
    /// </summary>
    public static List<string> Split(string command)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inArg = false;
        char? quote = null;
        foreach (var c in command)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
                continue;
            }
            if (c == '"' || (c == '\'' && !OperatingSystem.IsWindows()))
            {
                quote = c;
                inArg = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    inArg = false;
                }
                continue;
            }
            current.Append(c);
            inArg = true;
        }
        if (quote is not null)
            throw new ArgumentException("aspas sem fechamento no comando");
        if (inArg)
            args.Add(current.ToString());
        return args;
    }
}
