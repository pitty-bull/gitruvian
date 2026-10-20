using System.Text;
using System.Text.RegularExpressions;

namespace Gitruvian.Core;

public sealed class IndexResult
{
    public int Scanned { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Removed { get; set; }
    public Dictionary<string, int> Skipped { get; } = new() { ["sensível"] = 0, ["binário"] = 0, ["grande"] = 0, ["ignorado"] = 0 };
    public long Chunks { get; set; }
}

public sealed record Hit(string Path, int StartLine, int EndLine, string Snippet, double Score);

/// <summary>Contexto enviado ao modelo. <see cref="Entries"/> guarda só caminhos e linhas — nunca conteúdo.</summary>
public sealed record ContextPack(string Text, List<Dictionary<string, string>> Entries)
{
    public int Chars => Text.Length;
}

/// <summary>Indexação local do código (SQLite FTS5) e montagem de contexto para o modelo.</summary>
public static partial class Indexer
{
    public const int MaxFileBytes = 512 * 1024;
    public const int ChunkLines = 60;
    public const string IgnoreFile = ".gitruvianignore";

    private static readonly Dictionary<string, string> Langs = new()
    {
        [".py"] = "python", [".js"] = "javascript", [".jsx"] = "javascript", [".ts"] = "typescript",
        [".tsx"] = "typescript", [".go"] = "go", [".rs"] = "rust", [".java"] = "java", [".kt"] = "kotlin",
        [".rb"] = "ruby", [".php"] = "php", [".cs"] = "csharp", [".c"] = "c", [".h"] = "c", [".cpp"] = "cpp",
        [".hpp"] = "cpp", [".swift"] = "swift", [".sh"] = "shell", [".ps1"] = "powershell", [".sql"] = "sql",
        [".html"] = "html", [".css"] = "css", [".md"] = "markdown", [".toml"] = "toml", [".yaml"] = "yaml",
        [".yml"] = "yaml", [".json"] = "json", [".xaml"] = "xml", [".axaml"] = "xml", [".csproj"] = "xml",
    };

    [GeneratedRegex(@"\w{2,}")]
    private static partial Regex WordRe();

    public static List<string> LoadIgnorePatterns(string repo)
    {
        var path = Path.Combine(repo, IgnoreFile);
        if (!File.Exists(path))
            return [];
        return File.ReadAllText(path).Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }

    private static bool Ignored(string path, List<string> patterns)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        foreach (var pattern in patterns)
        {
            if (pattern.EndsWith('/') && (path.StartsWith(pattern) || ("/" + path).Contains("/" + pattern)))
                return true;
            if (Glob.Match(path, pattern) || Glob.Match(name, pattern))
                return true;
        }
        return false;
    }

    /// <summary>Lê arquivo de texto; retorna null se for binário ou ilegível.</summary>
    public static string? ReadTextFile(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (data.AsSpan(0, Math.Min(8192, data.Length)).Contains((byte)0))
            return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    public static List<(int Start, int End, string Content)> Chunk(string text, int size = ChunkLines)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var chunks = new List<(int, int, string)>();
        for (var start = 0; start < lines.Length; start += size)
        {
            var part = lines.Skip(start).Take(size).ToArray();
            if (part.Any(l => l.Trim().Length > 0))
                chunks.Add((start + 1, start + part.Length, string.Join('\n', part)));
        }
        return chunks;
    }

    private static string LangOf(string rel) =>
        Langs.TryGetValue(Path.GetExtension(rel).ToLowerInvariant(), out var lang) ? lang : "texto";

    /// <summary>Indexa os arquivos rastreados/não ignorados do repositório, de forma incremental.</summary>
    public static IndexResult IndexRepo(Store store, string repo, bool full = false, Action<int, int>? progress = null)
    {
        var result = new IndexResult();
        var patterns = LoadIgnorePatterns(repo);
        var paths = Git.LsFiles(repo);
        if (full)
            store.InTransaction(() =>
            {
                store.Execute("DELETE FROM chunks");
                store.Execute("DELETE FROM files");
            });
        var known = new Dictionary<string, (long Id, string Sha)>();
        using (var cmd = store.Command("SELECT id, path, sha256 FROM files"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                known[reader.GetString(1)] = (reader.GetInt64(0), reader.GetString(2));
        var seen = new HashSet<string>();

        for (var i = 0; i < paths.Count; i++)
        {
            progress?.Invoke(i + 1, paths.Count);
            result.Scanned++;
            string rel;
            try
            {
                rel = Safety.CheckRepoRelativePath(paths[i]);
            }
            catch (UnsafePathException)
            {
                result.Skipped["sensível"]++;
                continue;
            }
            if (Safety.IsSensitivePath(rel))
            {
                result.Skipped["sensível"]++;
                continue;
            }
            if (Ignored(rel, patterns))
            {
                result.Skipped["ignorado"]++;
                continue;
            }
            var fullPath = Path.Combine(repo, rel);
            long size;
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) continue;
                size = info.Length;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (size > MaxFileBytes)
            {
                result.Skipped["grande"]++;
                continue;
            }
            var text = ReadTextFile(fullPath);
            if (text is null)
            {
                result.Skipped["binário"]++;
                continue;
            }
            seen.Add(rel);
            var digest = Clock.Sha256(text);
            var hasPrevious = known.TryGetValue(rel, out var previous);
            if (hasPrevious && previous.Sha == digest)
            {
                result.Unchanged++;
                continue;
            }
            var chunks = Chunk(text);
            store.InTransaction(() =>
            {
                long fileId;
                if (hasPrevious)
                {
                    store.Execute("DELETE FROM chunks WHERE file_id = $f", ("$f", previous.Id));
                    store.Execute("UPDATE files SET sha256 = $s, size = $z, lang = $l, chunk_count = $c, " +
                                  "indexed_at = $t WHERE id = $id",
                        ("$s", digest), ("$z", size), ("$l", LangOf(rel)), ("$c", chunks.Count),
                        ("$t", Clock.NowIso()), ("$id", previous.Id));
                    fileId = previous.Id;
                    result.Updated++;
                }
                else
                {
                    fileId = (long)store.Scalar("INSERT INTO files (path, sha256, size, lang, chunk_count, indexed_at) " +
                                                "VALUES ($p, $s, $z, $l, $c, $t) RETURNING id",
                        ("$p", rel), ("$s", digest), ("$z", size), ("$l", LangOf(rel)), ("$c", chunks.Count),
                        ("$t", Clock.NowIso()))!;
                    result.Added++;
                }
                foreach (var (start, end, content) in chunks)
                    store.Execute("INSERT INTO chunks (path, content, file_id, start_line, end_line) " +
                                  "VALUES ($p, $c, $f, $s, $e)",
                        ("$p", rel), ("$c", content), ("$f", fileId), ("$s", start), ("$e", end));
            });
        }

        foreach (var (rel, (fileId, _)) in known)
        {
            if (seen.Contains(rel))
                continue;
            store.InTransaction(() =>
            {
                store.Execute("DELETE FROM chunks WHERE file_id = $f", ("$f", fileId));
                store.Execute("DELETE FROM files WHERE id = $f", ("$f", fileId));
            });
            result.Removed++;
        }
        result.Chunks = store.IndexStats().Chunks;
        return result;
    }

    /// <summary>Transforma texto livre em consulta FTS5 segura (termos entre aspas, unidos por OR).</summary>
    public static string? BuildFtsQuery(string text, int maxTerms = 24)
    {
        var terms = new List<string>();
        foreach (Match word in WordRe().Matches(text.ToLowerInvariant()))
            foreach (var part in word.Value.Split('_'))
                if (part.Length >= 2 && !terms.Contains(part))
                    terms.Add(part);
        if (terms.Count == 0)
            return null;
        return string.Join(" OR ", terms.Take(maxTerms).Select(t => $"\"{t}\""));
    }

    public static List<Hit> Search(Store store, string query, int limit = 10)
    {
        var fts = BuildFtsQuery(query);
        if (fts is null)
            return [];
        using var cmd = store.Command(
            "SELECT path, start_line, end_line, bm25(chunks, 4.0, 1.0) AS score, " +
            "snippet(chunks, 1, '«', '»', ' … ', 16) AS snip " +
            "FROM chunks WHERE chunks MATCH $q ORDER BY score LIMIT $l", ("$q", fts), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var hits = new List<Hit>();
        while (r.Read())
            hits.Add(new Hit(r.GetString(0), Convert.ToInt32(r.GetValue(1)), Convert.ToInt32(r.GetValue(2)),
                Safety.Redact(r.GetString(4)), r.GetDouble(3)));
        return hits;
    }

    private static bool IsInside(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Monta o contexto enviado ao modelo: arquivos pedidos + melhores resultados da busca.</summary>
    public static ContextPack BuildContext(Store store, string repo, string instruction, int maxChars,
        IEnumerable<string>? explicitFiles = null)
    {
        var pieces = new List<string>();
        var entries = new List<Dictionary<string, string>>();
        var budget = maxChars;
        var perFileCap = Math.Max(2000, maxChars / 2);

        var listing = new List<string>();
        using (var cmd = store.Command("SELECT path FROM files ORDER BY path LIMIT 300"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                listing.Add(reader.GetString(0));
        if (listing.Count > 0)
        {
            var header = "Arquivos do repositório (lista parcial):\n" + string.Join('\n', listing) + "\n";
            header = header[..Math.Min(header.Length, maxChars / 6)];
            pieces.Add(header);
            budget -= header.Length;
        }

        var ordered = new List<string>();
        foreach (var raw in explicitFiles ?? [])
        {
            var rel = Safety.CheckRepoRelativePath(raw);
            if (!ordered.Contains(rel)) ordered.Add(rel);
        }
        var hitsByFile = new Dictionary<string, List<Hit>>();
        foreach (var hit in Search(store, instruction, 40))
        {
            if (!hitsByFile.TryGetValue(hit.Path, out var list))
                hitsByFile[hit.Path] = list = [];
            list.Add(hit);
            if (!ordered.Contains(hit.Path)) ordered.Add(hit.Path);
        }

        foreach (var rel in ordered)
        {
            if (budget <= 200)
                break;
            if (Safety.IsSensitivePath(rel))
                continue;
            var fullPath = Path.GetFullPath(Path.Combine(repo, rel));
            if (!IsInside(fullPath, repo) || !File.Exists(fullPath))
                continue;
            var text = ReadTextFile(fullPath);
            if (text is null)
                continue;
            text = text.Replace("\r\n", "\n");
            string block;
            if (text.Length <= Math.Min(perFileCap, budget))
            {
                block = $"\n### ARQUIVO: {rel}\n```\n{Safety.Redact(text)}\n```\n";
                entries.Add(new() { ["path"] = rel, ["lines"] = "todas" });
            }
            else
            {
                var lines = text.Split('\n');
                var parts = new List<string>();
                var ranges = new List<string>();
                foreach (var hit in hitsByFile.GetValueOrDefault(rel, []).OrderBy(h => h.StartLine).Take(4))
                {
                    var start = Math.Max(1, hit.StartLine - 5);
                    var end = Math.Min(lines.Length, hit.EndLine + 5);
                    parts.Add($"# linhas {start}-{end}\n" + string.Join('\n', lines[(start - 1)..end]));
                    ranges.Add($"{start}-{end}");
                }
                if (parts.Count == 0)
                {
                    parts.Add("# linhas 1-80\n" + string.Join('\n', lines.Take(80)));
                    ranges.Add("1-80");
                }
                block = $"\n### ARQUIVO: {rel} (trechos)\n```\n{Safety.Redact(string.Join('\n', parts))}\n```\n";
                entries.Add(new() { ["path"] = rel, ["lines"] = string.Join(',', ranges) });
            }
            if (block.Length > budget)
            {
                if (entries.Count > 1)
                {
                    entries.RemoveAt(entries.Count - 1); // não cabe: melhor omitir do que enviar um arquivo cortado
                    continue;
                }
                block = block[..Math.Max(0, budget - 8)] + "\n```\n";
                entries[^1]["lines"] += " (cortado)";
            }
            pieces.Add(block);
            budget -= block.Length;
        }
        return new ContextPack(string.Concat(pieces), entries);
    }
}
