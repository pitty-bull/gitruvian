using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Gitruvian.Core;

public enum Status
{
    Proposed,
    Applied,
    TestsPassed,
    TestsFailed,
    Committed,
    Reverted,
    Rejected,
}

public sealed class InvalidTransitionException(string message) : InvalidOperationException(message);

public sealed class NotFoundException(string message) : KeyNotFoundException(message);

public sealed class AmbiguousIdException(string message) : KeyNotFoundException(message);

public static partial class StatusInfo
{
    private static readonly Dictionary<Status, string> Values = new()
    {
        [Status.Proposed] = "proposed",
        [Status.Applied] = "applied",
        [Status.TestsPassed] = "tests_passed",
        [Status.TestsFailed] = "tests_failed",
        [Status.Committed] = "committed",
        [Status.Reverted] = "reverted",
        [Status.Rejected] = "rejected",
    };

    private static readonly Dictionary<Status, string> Labels = new()
    {
        [Status.Proposed] = "proposta",
        [Status.Applied] = "aplicada",
        [Status.TestsPassed] = "testes ok",
        [Status.TestsFailed] = "testes falharam",
        [Status.Committed] = "commitada",
        [Status.Reverted] = "revertida",
        [Status.Rejected] = "rejeitada",
    };

    public static readonly IReadOnlyDictionary<Status, HashSet<Status>> Transitions = new Dictionary<Status, HashSet<Status>>
    {
        [Status.Proposed] = [Status.Applied, Status.Rejected],
        [Status.Applied] = [Status.TestsPassed, Status.TestsFailed, Status.Committed, Status.Reverted],
        [Status.TestsPassed] = [Status.TestsPassed, Status.TestsFailed, Status.Committed, Status.Reverted],
        [Status.TestsFailed] = [Status.TestsPassed, Status.TestsFailed, Status.Reverted],
        [Status.Committed] = [Status.Reverted],
        [Status.Reverted] = [Status.Applied, Status.Rejected],
        [Status.Rejected] = [],
    };

    public static string Value(this Status status) => Values[status];

    public static string Label(this Status status) => Labels[status];

    public static Status Parse(string? value) =>
        TryParse(value, out var status) ? status : throw new ArgumentException($"estado inválido: {value}");

    public static bool TryParse(string? value, out Status status)
    {
        foreach (var (key, v) in Values)
        {
            if (v == value)
            {
                status = key;
                return true;
            }
        }
        status = default;
        return false;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    public static partial Regex IdRe();

    [GeneratedRegex("^[0-9a-f]{4,32}$")]
    public static partial Regex IdPrefixRe();
}

public static class Clock
{
    /// <summary>Data/hora UTC em ISO 8601 com segundos (ex.: 2026-10-19T12:00:00+00:00).</summary>
    public static string NowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>Trecho de contexto enviado ao modelo: só caminho e linhas, nunca conteúdo.</summary>
public sealed record ContextEntry(string Path, string Lines);

public sealed record Proposal
{
    public required string Id { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public required string Instruction { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Diff { get; init; }
    public required string DiffSha256 { get; init; }
    public required List<string> Files { get; init; }
    public required string CommitMessage { get; init; }
    public required Status Status { get; init; }
    public string? BaseCommit { get; init; }
    public string? Branch { get; init; }
    public string? CommitSha { get; init; }
    public string? CheckError { get; init; }
    public List<Dictionary<string, string>> Context { get; init; } = [];
    public long? LatencyMs { get; init; }
    public bool IsSample { get; init; }

    public string ShortId => Id[..8];
    public string StatusLabel => Status.Label();
}

public sealed record TestRun
{
    public required string Id { get; init; }
    public string? ProposalId { get; init; }
    public required string Command { get; init; }
    public required string StartedAt { get; init; }
    public required long DurationMs { get; init; }
    public required int ExitCode { get; init; }
    public bool Passed => ExitCode == 0;
    public required string OutputTail { get; init; }
    public bool IsSample { get; init; }
}

public sealed record Event
{
    public required string Id { get; init; }
    public required string Ts { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public string? ProposalId { get; init; }
    public JsonObject Data { get; init; } = [];
    public bool IsSample { get; init; }
}

public sealed record IndexStats(long Files, long Chunks, string? LastIndexed);

/// <summary>
/// Acesso ao banco SQLite (esquema versionado em PRAGMA user_version).
/// Todas as consultas são parametrizadas. Uma instância por operação/thread.
/// </summary>
public sealed class Store : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly string StatusSql =
        string.Join(", ", Enum.GetValues<Status>().Select(s => $"'{s.Value()}'"));

    private static readonly string Schema = $$"""
        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            sha256 TEXT NOT NULL,
            size INTEGER NOT NULL,
            lang TEXT NOT NULL,
            chunk_count INTEGER NOT NULL,
            indexed_at TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks USING fts5(
            path, content, file_id UNINDEXED, start_line UNINDEXED, end_line UNINDEXED,
            tokenize = 'unicode61'
        );
        CREATE TABLE IF NOT EXISTS proposals (
            id TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            instruction TEXT NOT NULL,
            provider TEXT NOT NULL,
            model TEXT NOT NULL,
            diff TEXT NOT NULL,
            diff_sha256 TEXT NOT NULL,
            files_json TEXT NOT NULL,
            commit_message TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ({{StatusSql}})),
            base_commit TEXT,
            branch TEXT,
            commit_sha TEXT,
            check_error TEXT,
            context_json TEXT NOT NULL DEFAULT '[]',
            latency_ms INTEGER,
            is_sample INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS proposals_created ON proposals(created_at);
        CREATE TABLE IF NOT EXISTS test_runs (
            id TEXT PRIMARY KEY,
            proposal_id TEXT REFERENCES proposals(id) ON DELETE SET NULL,
            command TEXT NOT NULL,
            started_at TEXT NOT NULL,
            duration_ms INTEGER NOT NULL,
            exit_code INTEGER NOT NULL,
            passed INTEGER NOT NULL,
            output_tail TEXT NOT NULL,
            is_sample INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS events (
            id TEXT PRIMARY KEY,
            ts TEXT NOT NULL,
            kind TEXT NOT NULL,
            summary TEXT NOT NULL,
            proposal_id TEXT REFERENCES proposals(id) ON DELETE SET NULL,
            data_json TEXT NOT NULL DEFAULT '{}',
            is_sample INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
        """;

    private static readonly HashSet<string> TransitionFields = ["base_commit", "branch", "commit_sha", "files"];

    public SqliteConnection Connection { get; }

    private Store(SqliteConnection connection)
    {
        Connection = connection;
        Migrate();
    }

    public static Store Open(string dbPath)
    {
        // Pooling desligado: o arquivo precisa fechar de verdade ao terminar (no Windows, senão a pasta
        // do repositório de exemplo não pode ser apagada).
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
            DefaultTimeout = 10,
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        Exec(conn, "PRAGMA foreign_keys = ON");
        if (dbPath != ":memory:")
            Exec(conn, "PRAGMA journal_mode = WAL");
        try
        {
            return new Store(conn);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public void Dispose() => Connection.Dispose();

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteTransaction? _transaction;

    /// <summary>Executa <paramref name="work"/> numa transação (commit no fim, rollback em erro).</summary>
    public T InTransaction<T>(Func<T> work)
    {
        if (_transaction is not null)
            return work();
        _transaction = Connection.BeginTransaction();
        try
        {
            var result = work();
            _transaction.Commit();
            return result;
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public void InTransaction(Action work) => InTransaction(() =>
    {
        work();
        return 0;
    });

    internal SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    internal int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    internal object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"banco criado por uma versão mais nova do Gitruvian (schema {version})");
        try
        {
            InTransaction(() =>
            {
                Execute(Schema);
                Execute($"PRAGMA user_version = {SchemaVersion}");
            });
        }
        catch (SqliteException exc) when (exc.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("o SQLite embutido não tem suporte a FTS5 (busca no código)", exc);
        }
    }

    // ---------------------------------------------------------------- settings
    public string? GetSetting(string key, string? fallback = null) =>
        Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) as string ?? fallback;

    public void SetSetting(string key, string value) =>
        Execute("INSERT INTO settings(key, value, updated_at) VALUES ($k, $v, $t) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            ("$k", key), ("$v", value), ("$t", Clock.NowIso()));

    public bool DeleteSetting(string key) => Execute("DELETE FROM settings WHERE key = $k", ("$k", key)) > 0;

    public Dictionary<string, string> Settings()
    {
        using var cmd = Command("SELECT key, value FROM settings ORDER BY key");
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, string>();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    // --------------------------------------------------------------- proposals
    public Proposal AddProposal(string instruction, string provider, string model, string diff, List<string> files,
        string commitMessage, List<Dictionary<string, string>>? context = null, long? latencyMs = null,
        string? checkError = null, bool isSample = false)
    {
        var ts = Clock.NowIso();
        var proposal = new Proposal
        {
            Id = Clock.NewId(),
            CreatedAt = ts,
            UpdatedAt = ts,
            Instruction = instruction,
            Provider = provider,
            Model = model,
            Diff = diff,
            DiffSha256 = Clock.Sha256(diff),
            Files = [.. files],
            CommitMessage = commitMessage,
            Status = Status.Proposed,
            CheckError = checkError,
            Context = context ?? [],
            LatencyMs = latencyMs,
            IsSample = isSample,
        };
        InsertProposal(proposal);
        return proposal;
    }

    /// <summary>Insere uma proposta já montada. Retorna false se o id já existia.</summary>
    public bool InsertProposal(Proposal p) =>
        Execute("INSERT OR IGNORE INTO proposals (id, created_at, updated_at, instruction, provider, model, " +
                "diff, diff_sha256, files_json, commit_message, status, base_commit, branch, commit_sha, " +
                "check_error, context_json, latency_ms, is_sample) VALUES ($id, $ca, $ua, $in, $pr, $mo, $di, " +
                "$ds, $fi, $cm, $st, $bc, $br, $cs, $ce, $cx, $la, $sa)",
            ("$id", p.Id), ("$ca", p.CreatedAt), ("$ua", p.UpdatedAt), ("$in", p.Instruction), ("$pr", p.Provider),
            ("$mo", p.Model), ("$di", p.Diff), ("$ds", p.DiffSha256), ("$fi", JsonSerializer.Serialize(p.Files)),
            ("$cm", p.CommitMessage), ("$st", p.Status.Value()), ("$bc", p.BaseCommit), ("$br", p.Branch),
            ("$cs", p.CommitSha), ("$ce", p.CheckError), ("$cx", JsonSerializer.Serialize(p.Context)),
            ("$la", p.LatencyMs), ("$sa", p.IsSample ? 1 : 0)) > 0;

    private static string? Str(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static Proposal ReadProposal(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        CreatedAt = Str(r, "created_at")!,
        UpdatedAt = Str(r, "updated_at")!,
        Instruction = Str(r, "instruction")!,
        Provider = Str(r, "provider")!,
        Model = Str(r, "model")!,
        Diff = Str(r, "diff")!,
        DiffSha256 = Str(r, "diff_sha256")!,
        Files = JsonSerializer.Deserialize<List<string>>(Str(r, "files_json")!) ?? [],
        CommitMessage = Str(r, "commit_message")!,
        Status = StatusInfo.Parse(Str(r, "status")),
        BaseCommit = Str(r, "base_commit"),
        Branch = Str(r, "branch"),
        CommitSha = Str(r, "commit_sha"),
        CheckError = Str(r, "check_error"),
        Context = ParseContext(Str(r, "context_json")),
        LatencyMs = r.IsDBNull(r.GetOrdinal("latency_ms")) ? null : r.GetInt64(r.GetOrdinal("latency_ms")),
        IsSample = r.GetInt64(r.GetOrdinal("is_sample")) != 0,
    };

    private static List<Dictionary<string, string>> ParseContext(string? json)
    {
        var result = new List<Dictionary<string, string>>();
        if (JsonNode.Parse(json ?? "[]") is not JsonArray array)
            return result;
        foreach (var item in array.OfType<JsonObject>())
            result.Add(item.Where(kv => kv.Value is JsonValue)
                .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()));
        return result;
    }

    private List<Proposal> QueryProposals(string sql, params (string, object?)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        using var reader = cmd.ExecuteReader();
        var result = new List<Proposal>();
        while (reader.Read())
            result.Add(ReadProposal(reader));
        return result;
    }

    public Proposal GetProposal(string idOrPrefix)
    {
        var key = (idOrPrefix ?? "").Trim().ToLowerInvariant();
        if (!StatusInfo.IdPrefixRe().IsMatch(key))
            throw new NotFoundException("id inválido: use de 4 a 32 caracteres hexadecimais");
        var rows = QueryProposals("SELECT * FROM proposals WHERE id LIKE $p ORDER BY created_at LIMIT 2",
            ("$p", key + "%"));
        if (rows.Count == 0)
            throw new NotFoundException($"nenhuma proposta com id {key}");
        if (rows.Count > 1)
            throw new AmbiguousIdException($"o prefixo {key} corresponde a mais de uma proposta; use mais caracteres");
        return rows[0];
    }

    public List<Proposal> ListProposals(Status? status = null, int limit = 50) =>
        status is null
            ? QueryProposals("SELECT * FROM proposals ORDER BY created_at DESC, rowid DESC LIMIT $l", ("$l", limit))
            : QueryProposals("SELECT * FROM proposals WHERE status = $s ORDER BY created_at DESC, rowid DESC LIMIT $l",
                ("$s", status.Value.Value()), ("$l", limit));

    /// <summary>Muda o estado respeitando as transições permitidas e atualiza campos permitidos.</summary>
    public Proposal Transition(string proposalId, Status newStatus, IReadOnlyDictionary<string, object?>? fields = null)
    {
        fields ??= new Dictionary<string, object?>();
        var unknown = fields.Keys.Where(k => !TransitionFields.Contains(k)).Order().ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"campos não permitidos: {string.Join(", ", unknown)}");
        var current = GetProposal(proposalId);
        if (!StatusInfo.Transitions[current.Status].Contains(newStatus))
            throw new InvalidTransitionException(
                $"não é possível ir de '{current.StatusLabel}' para '{newStatus.Label()}'");
        var assignments = new List<string> { "status = $status", "updated_at = $updated" };
        var values = new List<(string, object?)> { ("$status", newStatus.Value()), ("$updated", Clock.NowIso()) };
        foreach (var (key, value) in fields)
        {
            var column = key == "files" ? "files_json" : key;
            assignments.Add($"{column} = ${key}");
            values.Add(($"${key}", key == "files" ? JsonSerializer.Serialize(value) : value));
        }
        values.Add(("$id", current.Id));
        Execute($"UPDATE proposals SET {string.Join(", ", assignments)} WHERE id = $id", [.. values]);
        return GetProposal(current.Id);
    }

    public Dictionary<Status, int> StatusCounts()
    {
        var counts = Enum.GetValues<Status>().ToDictionary(s => s, _ => 0);
        using var cmd = Command("SELECT status, COUNT(*) FROM proposals GROUP BY status");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (StatusInfo.TryParse(reader.GetString(0), out var status))
                counts[status] = reader.GetInt32(1);
        return counts;
    }

    // --------------------------------------------------------------- test runs
    public TestRun AddTestRun(string command, long durationMs, int exitCode, string outputTail,
        string? proposalId = null, string? startedAt = null, bool isSample = false)
    {
        var run = new TestRun
        {
            Id = Clock.NewId(),
            ProposalId = proposalId,
            Command = command,
            StartedAt = startedAt ?? Clock.NowIso(),
            DurationMs = durationMs,
            ExitCode = exitCode,
            OutputTail = outputTail,
            IsSample = isSample,
        };
        InsertTestRun(run);
        return run;
    }

    public bool InsertTestRun(TestRun run) =>
        Execute("INSERT OR IGNORE INTO test_runs (id, proposal_id, command, started_at, duration_ms, exit_code, " +
                "passed, output_tail, is_sample) VALUES ($id, $p, $c, $s, $d, $e, $ok, $o, $sa)",
            ("$id", run.Id), ("$p", run.ProposalId), ("$c", run.Command), ("$s", run.StartedAt),
            ("$d", run.DurationMs), ("$e", run.ExitCode), ("$ok", run.Passed ? 1 : 0), ("$o", run.OutputTail),
            ("$sa", run.IsSample ? 1 : 0)) > 0;

    public List<TestRun> ListTestRuns(string? proposalId = null, int limit = 50)
    {
        using var cmd = proposalId is not null
            ? Command("SELECT * FROM test_runs WHERE proposal_id = $p ORDER BY started_at DESC, rowid DESC LIMIT $l",
                ("$p", proposalId), ("$l", limit))
            : Command("SELECT * FROM test_runs ORDER BY started_at DESC, rowid DESC LIMIT $l", ("$l", limit));
        using var r = cmd.ExecuteReader();
        var result = new List<TestRun>();
        while (r.Read())
        {
            result.Add(new TestRun
            {
                Id = Str(r, "id")!,
                ProposalId = Str(r, "proposal_id"),
                Command = Str(r, "command")!,
                StartedAt = Str(r, "started_at")!,
                DurationMs = r.GetInt64(r.GetOrdinal("duration_ms")),
                ExitCode = r.GetInt32(r.GetOrdinal("exit_code")),
                OutputTail = Str(r, "output_tail")!,
                IsSample = r.GetInt64(r.GetOrdinal("is_sample")) != 0,
            });
        }
        return result;
    }

    // ------------------------------------------------------------------ events
    public Event AddEvent(string kind, string summary, string? proposalId = null, JsonObject? data = null,
        bool isSample = false)
    {
        var ev = new Event
        {
            Id = Clock.NewId(),
            Ts = Clock.NowIso(),
            Kind = kind,
            Summary = summary,
            ProposalId = proposalId,
            Data = data ?? [],
            IsSample = isSample,
        };
        InsertEvent(ev);
        return ev;
    }

    public bool InsertEvent(Event ev) =>
        Execute("INSERT OR IGNORE INTO events (id, ts, kind, summary, proposal_id, data_json, is_sample) " +
                "VALUES ($id, $ts, $k, $s, $p, $d, $sa)",
            ("$id", ev.Id), ("$ts", ev.Ts), ("$k", ev.Kind), ("$s", ev.Summary), ("$p", ev.ProposalId),
            ("$d", ev.Data.ToJsonString()), ("$sa", ev.IsSample ? 1 : 0)) > 0;

    public List<Event> ListEvents(int limit = 50, string? proposalId = null)
    {
        using var cmd = proposalId is not null
            ? Command("SELECT * FROM events WHERE proposal_id = $p ORDER BY ts DESC, rowid DESC LIMIT $l",
                ("$p", proposalId), ("$l", limit))
            : Command("SELECT * FROM events ORDER BY ts DESC, rowid DESC LIMIT $l", ("$l", limit));
        using var r = cmd.ExecuteReader();
        var result = new List<Event>();
        while (r.Read())
        {
            result.Add(new Event
            {
                Id = Str(r, "id")!,
                Ts = Str(r, "ts")!,
                Kind = Str(r, "kind")!,
                Summary = Str(r, "summary")!,
                ProposalId = Str(r, "proposal_id"),
                Data = JsonNode.Parse(Str(r, "data_json") ?? "{}") as JsonObject ?? [],
                IsSample = r.GetInt64(r.GetOrdinal("is_sample")) != 0,
            });
        }
        return result;
    }

    // ------------------------------------------------------------------- index
    public IndexStats IndexStats()
    {
        using var cmd = Command("SELECT COUNT(*), COALESCE(SUM(chunk_count), 0), MAX(indexed_at) FROM files");
        using var r = cmd.ExecuteReader();
        r.Read();
        return new IndexStats(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    // ----------------------------------------------------------------- samples
    /// <summary>Apaga todos os registros marcados como dados de exemplo.</summary>
    public Dictionary<string, int> DeleteSamples()
    {
        return InTransaction(() => new Dictionary<string, int>
        {
            ["events"] = Execute("DELETE FROM events WHERE is_sample = 1"),
            ["test_runs"] = Execute("DELETE FROM test_runs WHERE is_sample = 1"),
            ["proposals"] = Execute("DELETE FROM proposals WHERE is_sample = 1"),
        });
    }
}
