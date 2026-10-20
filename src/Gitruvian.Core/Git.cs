using System.Text;
using System.Text.RegularExpressions;

namespace Gitruvian.Core;

/// <summary>Falha ao executar um comando git.</summary>
public sealed class GitException(string message) : Exception(message);

public sealed record GitResult(int ExitCode, string Stdout, string Stderr);

public sealed record StatusEntry(string Code, string Path)
{
    public bool IsUntracked => Code == "??";
    public bool IsStaged => Code != "??" && Code[0] != ' ';
    public bool HasUnstaged => Code == "??" || Code[1] != ' ';

    public string Label
    {
        get
        {
            if (Code == "??")
                return "não rastreado";
            if (Code.Contains('U') || Code is "AA" or "DD")
                return "conflito";
            var names = new Dictionary<char, string>
            {
                ['M'] = "modificado", ['A'] = "adicionado", ['D'] = "removido", ['R'] = "renomeado",
                ['C'] = "copiado", ['T'] = "tipo alterado",
            };
            string Name(char c) => names.TryGetValue(c, out var n) ? n : c.ToString();
            var parts = new List<string>();
            if (Code[0] != ' ') parts.Add($"{Name(Code[0])} (preparado)");
            if (Code[1] != ' ') parts.Add(Name(Code[1]));
            return string.Join(", ", parts);
        }
    }
}

public sealed record GraphCommit(string Sha, List<string> Parents, string Author, string Email, long Timestamp,
    List<string> Refs, string Subject) : IHasParents
{
    IReadOnlyList<string> IHasParents.Parents => Parents;
}

public sealed record CommitFile(string Status, string Path);

public sealed record CommitDetail(string Sha, List<string> Parents, string Author, string Email, long Timestamp,
    string Message, List<CommitFile> Files);

public sealed record BranchInfo(string Name, bool IsCurrent);

/// <summary>Operações Git via processo (sempre com lista de argumentos, nunca shell).</summary>
public static partial class Git
{
    public const int GitTimeout = 120;
    public const string GraphFormat = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e";
    private static readonly string[] ApplyFlags = ["--recount", "--whitespace=nowarn"];

    // Sem prompts de credenciais nem pagers: o app não tem terminal.
    private static readonly Dictionary<string, string> GitEnv = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_PAGER"] = "cat",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    [GeneratedRegex("^[0-9a-fA-F]{4,64}$")]
    private static partial Regex ShaRe();

    public static bool Available() => Processes.Which("git") is not null;

    public static GitResult Run(string repo, IEnumerable<string> args, string? input = null, bool check = true,
        int timeout = GitTimeout)
    {
        var argList = args.ToList();
        var argv = new List<string> { "git", "-c", "core.quotepath=off", "-c", "color.ui=never" };
        argv.AddRange(argList);
        ProcessResult proc;
        try
        {
            proc = Processes.Run(argv, repo, input is null ? null : Encoding.UTF8.GetBytes(input), timeout, GitEnv);
        }
        catch (FileNotFoundException)
        {
            throw new GitException("git não encontrado no PATH. Instale o Git (https://git-scm.com) e abra o Gitruvian de novo.");
        }
        if (proc.TimedOut)
            throw new GitException($"git {argList.FirstOrDefault()} excedeu {timeout}s");
        var result = new GitResult(proc.ExitCode, proc.StdoutText, proc.StderrText);
        if (check && result.ExitCode != 0)
        {
            var message = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim();
            if (message.Length == 0) message = $"código {result.ExitCode}";
            throw new GitException($"git {argList.FirstOrDefault()} falhou: {message}");
        }
        return result;
    }

    public static GitResult Run(string repo, params string[] args) => Run(repo, (IEnumerable<string>)args);

    public static string FindRepoRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new GitException($"pasta não existe: {full}");
        var result = Run(full, ["rev-parse", "--show-toplevel"], check: false);
        if (result.ExitCode != 0)
            throw new GitException($"{full} não está dentro de um repositório git (rode 'git init')");
        return Path.GetFullPath(result.Stdout.Trim());
    }

    public static string GitDir(string repo)
    {
        var output = Run(repo, "rev-parse", "--git-dir").Stdout.Trim();
        return Path.IsPathRooted(output) ? output : Path.GetFullPath(Path.Combine(repo, output));
    }

    public static string? HeadSha(string repo)
    {
        var result = Run(repo, ["rev-parse", "--verify", "-q", "HEAD"], check: false);
        var sha = result.Stdout.Trim();
        return result.ExitCode == 0 && sha.Length > 0 ? sha : null;
    }

    public static string CurrentBranch(string repo)
    {
        var result = Run(repo, ["symbolic-ref", "--short", "-q", "HEAD"], check: false);
        if (result.ExitCode == 0 && result.Stdout.Trim().Length > 0)
            return result.Stdout.Trim();
        var sha = HeadSha(repo);
        return sha is not null ? $"(HEAD destacado em {sha[..8]})" : "(sem commits)";
    }

    public static List<StatusEntry> Status(string repo)
    {
        var output = Run(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all").Stdout;
        var entries = new List<StatusEntry>();
        var items = output.Split('\0');
        var i = 0;
        while (i < items.Length)
        {
            var item = items[i++];
            if (item.Length < 4)
                continue;
            var code = item[..2];
            if (code[0] is 'R' or 'C')
                i++; // pula o caminho de origem do rename/cópia
            entries.Add(new StatusEntry(code, item[3..]));
        }
        return entries;
    }

    public static List<BranchInfo> Branches(string repo)
    {
        var output = Run(repo, "branch", "--format=%(HEAD)%00%(refname:short)").Stdout;
        var result = new List<BranchInfo>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\0', 2);
            if (parts.Length == 2 && parts[1].Length > 0)
                result.Add(new BranchInfo(parts[1], parts[0].Trim() == "*"));
        }
        return result;
    }

    public static List<string> LsFiles(string repo)
    {
        var output = Run(repo, "ls-files", "-z", "--cached", "--others", "--exclude-standard").Stdout;
        return output.Split('\0').Where(p => p.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    public static string DiffText(string repo, bool staged = false, IEnumerable<string>? paths = null)
    {
        var args = new List<string> { "diff", "--no-ext-diff" };
        if (staged) args.Add("--cached");
        var list = paths?.ToList();
        if (list is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(list);
        }
        return Run(repo, args).Stdout;
    }

    /// <summary>Verifica se o patch aplica. Retorna (ok, erro, flags extras que funcionaram).</summary>
    public static (bool Ok, string Error, string[] Extra) ApplyCheck(string repo, string patch, bool reverse = false)
    {
        var baseArgs = new List<string> { "apply", "--check" };
        baseArgs.AddRange(ApplyFlags);
        if (reverse) baseArgs.Add("-R");
        var first = Run(repo, [.. baseArgs, "-"], input: patch, check: false);
        if (first.ExitCode == 0)
            return (true, "", []);
        // Tolera diferenças de espaço/fim de linha (comum com core.autocrlf no Windows).
        var second = Run(repo, [.. baseArgs, "--ignore-whitespace", "-"], input: patch, check: false);
        if (second.ExitCode == 0)
            return (true, "", ["--ignore-whitespace"]);
        return (false, (first.Stderr.Length > 0 ? first.Stderr : first.Stdout).Trim(), []);
    }

    public static void ApplyPatch(string repo, string patch, bool reverse = false)
    {
        var (ok, error, extra) = ApplyCheck(repo, patch, reverse);
        if (!ok)
            throw new GitException($"o diff não aplica no estado atual: {error}");
        var args = new List<string> { "apply" };
        args.AddRange(ApplyFlags);
        args.AddRange(extra);
        if (reverse) args.Add("-R");
        args.Add("-");
        Run(repo, args, input: patch);
    }

    public static void Stage(string repo, IEnumerable<string> paths) => Run(repo, ["add", "-A", "--", .. paths]);

    public static void Unstage(string repo, IEnumerable<string> paths)
    {
        if (HeadSha(repo) is null)
            Run(repo, ["rm", "--cached", "-q", "--", .. paths]);
        else
            Run(repo, ["reset", "-q", "HEAD", "--", .. paths]);
    }

    public static string Commit(string repo, string message, IReadOnlyList<string>? paths = null)
    {
        var args = new List<string> { "commit", "-q", "-F", "-" };
        if (paths is { Count: > 0 })
        {
            Stage(repo, paths);
            args.Add("--");
            args.AddRange(paths);
        }
        Run(repo, args, input: message);
        return HeadSha(repo) ?? throw new GitException("commit não foi criado");
    }

    public static string RevertCommit(string repo, string sha)
    {
        Run(repo, "revert", "--no-edit", sha);
        return HeadSha(repo) ?? throw new GitException("revert não criou commit");
    }

    public static void CreateBranch(string repo, string name)
    {
        Run(repo, "check-ref-format", "--branch", name);
        Run(repo, "switch", "-c", name);
    }

    public static void Switch(string repo, string name)
    {
        Run(repo, "check-ref-format", "--branch", name);
        Run(repo, "switch", name);
    }

    public static List<string> DirtyPaths(string repo, IEnumerable<string> paths)
    {
        var wanted = paths.ToHashSet();
        return Status(repo).Where(e => wanted.Contains(e.Path)).Select(e => e.Path).ToList();
    }

    // ---------------------------------------------------------------- dados para a interface gráfica
    public static List<string> ParseRefs(string decoration)
    {
        var refs = new List<string>();
        foreach (var raw in decoration.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            if (part.StartsWith("HEAD -> "))
            {
                refs.Add("HEAD");
                part = part["HEAD -> ".Length..];
            }
            if (part.StartsWith("tag: "))
                part = "tag:" + part["tag: ".Length..];
            refs.Add(part);
        }
        return refs;
    }

    private static string[] Fields(string record, int count)
    {
        var fields = record.Split('\x1f');
        if (fields.Length >= count)
            return fields[..count];
        return [.. fields, .. Enumerable.Repeat("", count - fields.Length)];
    }

    /// <summary>Commits de todos os branches em ordem topológica, com pais e referências.</summary>
    public static List<GraphCommit> GraphLog(string repo, int limit = 500)
    {
        if (HeadSha(repo) is null && Branches(repo).Count == 0)
            return [];
        var output = Run(repo, "log", "--all", "--topo-order", $"-n{limit}", $"--format={GraphFormat}").Stdout;
        var commits = new List<GraphCommit>();
        foreach (var raw in output.Split('\x1e'))
        {
            var record = raw.Trim('\n');
            if (record.Length == 0)
                continue;
            var f = Fields(record, 7);
            commits.Add(new GraphCommit(f[0],
                f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                f[2], f[3], long.TryParse(f[4], out var ts) ? ts : 0, ParseRefs(f[5]), f[6]));
        }
        return commits;
    }

    private static void CheckSha(string sha)
    {
        if (!ShaRe().IsMatch(sha))
            throw new GitException("identificador de commit inválido");
    }

    public static CommitDetail CommitDetail(string repo, string sha)
    {
        CheckSha(sha);
        var head = Run(repo, "show", "-s", "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%B", sha).Stdout;
        var f = Fields(head, 6);
        var full = f[0].Trim();
        var filesOut = Run(repo, "diff-tree", "--no-commit-id", "-r", "-z", "--name-status", "--root", "-M", full)
            .Stdout;
        var items = filesOut.Split('\0').Where(i => i.Length > 0).ToList();
        var files = new List<CommitFile>();
        var i = 0;
        while (i < items.Count)
        {
            var code = items[i++];
            if (code[0] is 'R' or 'C' && i + 1 < items.Count)
            {
                files.Add(new CommitFile(code[..1], items[i + 1]));
                i += 2;
            }
            else if (i < items.Count)
            {
                files.Add(new CommitFile(code[..1], items[i]));
                i++;
            }
        }
        return new CommitDetail(full, f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(), f[2], f[3],
            long.TryParse(f[4], out var ts) ? ts : 0, f[5].Trim(), files);
    }

    public static string CommitFileDiff(string repo, string sha, string path)
    {
        CheckSha(sha);
        return Run(repo, "show", "--no-ext-diff", "--format=", "-M", sha, "--", path).Stdout;
    }

    /// <summary>Diff de um arquivo da árvore de trabalho. Arquivos não rastreados aparecem como novos.</summary>
    public static string WorkingFileDiff(string repo, string path, bool staged, bool untracked = false,
        int maxBytes = 512_000)
    {
        if (!untracked)
            return DiffText(repo, staged, [path]);
        var target = Path.Combine(repo, path);
        if (!File.Exists(target))
            return "";
        byte[] data;
        using (var stream = File.OpenRead(target))
        {
            data = new byte[Math.Min(maxBytes, stream.Length)];
            stream.ReadExactly(data);
        }
        if (data.Contains((byte)0))
            return $"(arquivo binário novo: {path})";
        var text = Encoding.UTF8.GetString(data).Replace("\r\n", "\n");
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var body = string.Join('\n', lines.Select(l => "+" + l));
        return $"novo arquivo: {path}\n@@ -0,0 +1,{lines.Count} @@\n{body}\n";
    }

    /// <summary>Descarta alterações não preparadas (ou apaga arquivos não rastreados).</summary>
    public static void Discard(string repo, IReadOnlyList<string> paths, bool untracked = false)
    {
        if (!untracked)
        {
            Run(repo, ["checkout", "-q", "--", .. paths]);
            return;
        }
        var root = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var target = Path.GetFullPath(Path.Combine(repo, path));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                throw new GitException($"caminho inválido: {path}");
            File.Delete(target);
        }
    }
}
