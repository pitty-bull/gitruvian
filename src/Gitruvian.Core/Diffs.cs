using System.Text;
using System.Text.RegularExpressions;

namespace Gitruvian.Core;

/// <summary>O texto não é um diff aceitável.</summary>
public sealed class DiffValidationException(string message) : ArgumentException(message);

public sealed record ParsedDiff(string Text, List<string> Files, int Additions, int Deletions);

/// <summary>Extração e validação de diffs unificados vindos de fontes não confiáveis.</summary>
public static partial class Diffs
{
    public const int MaxDiffBytes = 400_000;
    public const int MaxFiles = 50;

    [GeneratedRegex(@"```[ \t]*(?:diff|patch|udiff)?[ \t]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FenceRe();

    [GeneratedRegex(@"^\s*COMMIT:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CommitRe();

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@")]
    private static partial Regex HunkRe();

    [GeneratedRegex(@"^(new|deleted) file mode 120000|^(old|new) mode 120000")]
    private static partial Regex SymlinkRe();

    private static bool LooksLikeDiff(string text) =>
        ("\n" + text).Contains("\n@@ ") && (text.Contains("--- ") || text.Contains("diff --git"));

    /// <summary>Encontra o diff dentro da resposta do modelo (bloco ```diff ou texto puro).</summary>
    public static string ExtractDiff(string text)
    {
        text = text.Replace("\r\n", "\n");
        foreach (Match m in FenceRe().Matches(text))
        {
            var block = m.Groups[1].Value;
            if (LooksLikeDiff(block))
                return block;
        }
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("diff --git ") ||
                (line.StartsWith("--- ") && i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ")))
                return string.Join('\n', lines[i..]);
        }
        throw new DiffValidationException("a resposta não contém um diff unificado");
    }

    public static string? ExtractCommitMessage(string? text)
    {
        var match = CommitRe().Match(text ?? "");
        if (!match.Success)
            return null;
        var message = match.Groups[1].Value.Trim().Trim('`', '"', '\'');
        if (message.Length > 200) message = message[..200];
        return message.Length > 0 ? message : null;
    }

    private static string? StripPrefix(string raw)
    {
        raw = raw.Split('\t', 2)[0].Trim();
        if (raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"'))
            throw new DiffValidationException($"caminho com caracteres especiais não suportado: {raw}");
        if (raw == "/dev/null")
            return null;
        if (raw.StartsWith("a/") || raw.StartsWith("b/"))
            raw = raw[2..];
        return raw;
    }

    private static string CheckPath(string path)
    {
        try
        {
            return Safety.CheckRepoRelativePath(path);
        }
        catch (UnsafePathException exc)
        {
            throw new DiffValidationException(exc.Message);
        }
    }

    /// <summary>Valida um diff: tamanho, formato, caminhos seguros, sem binários nem links simbólicos.</summary>
    public static ParsedDiff Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DiffValidationException("diff vazio");
        if (Encoding.UTF8.GetByteCount(text) > MaxDiffBytes)
            throw new DiffValidationException($"diff maior que {MaxDiffBytes / 1000} KB");
        if (text.Contains('\0'))
            throw new DiffValidationException("diff contém bytes nulos");
        text = text.Replace("\r\n", "\n");
        if (!text.EndsWith('\n'))
            text += "\n";

        var files = new List<string>();
        int additions = 0, deletions = 0, hunks = 0;
        var inHunk = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("GIT binary patch") || line.StartsWith("Binary files "))
                throw new DiffValidationException("diffs binários não são aceitos");
            if (SymlinkRe().IsMatch(line))
                throw new DiffValidationException("links simbólicos não são aceitos");
            if (line.StartsWith("diff --git "))
            {
                inHunk = false;
                continue;
            }
            if (!inHunk && (line.StartsWith("--- ") || line.StartsWith("+++ ")))
            {
                var path = StripPrefix(line[4..]);
                if (path is not null)
                {
                    path = CheckPath(path);
                    if (!files.Contains(path)) files.Add(path);
                }
                continue;
            }
            if (line.StartsWith("rename from ") || line.StartsWith("rename to ") ||
                line.StartsWith("copy from ") || line.StartsWith("copy to "))
            {
                var path = CheckPath(line.Split(' ', 3)[2]);
                if (!files.Contains(path)) files.Add(path);
                continue;
            }
            if (HunkRe().IsMatch(line))
            {
                hunks++;
                inHunk = true;
                continue;
            }
            if (inHunk)
            {
                if (line.StartsWith('+')) additions++;
                else if (line.StartsWith('-')) deletions++;
                else if (line.StartsWith(' ') || line.StartsWith('\\') || line.Length == 0) { }
                else inHunk = false;
            }
        }

        if (files.Count == 0)
            throw new DiffValidationException("o diff não indica nenhum arquivo (faltam linhas ---/+++)");
        if (hunks == 0)
            throw new DiffValidationException("o diff não tem nenhum bloco @@");
        if (files.Count > MaxFiles)
            throw new DiffValidationException($"o diff altera mais de {MaxFiles} arquivos");
        return new ParsedDiff(text, files, additions, deletions);
    }
}
