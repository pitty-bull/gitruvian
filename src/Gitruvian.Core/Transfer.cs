using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitruvian.Core;

public sealed class ImportValidationException(string message) : ArgumentException(message);

public sealed class ImportReport
{
    public Dictionary<string, int> Inserted { get; } = new() { ["proposals"] = 0, ["test_runs"] = 0, ["events"] = 0 };
    public int Duplicates { get; set; }
    public List<string> Skipped { get; } = [];
}

/// <summary>
/// Exportação e importação em JSON aberto, para o usuário não ficar
/// preso ao app.
/// </summary>
public static class Transfer
{
    public const string Format = "gitruvian-export";
    public const int FormatVersion = 1;
    public const long MaxImportBytes = 50L * 1024 * 1024;
    public const int MaxRecords = 100_000;

    /// <summary>Configurações que nunca são importadas: executam comandos na máquina.</summary>
    public static readonly HashSet<string> NonImportableSettings = ["test_command"];

    public static string AppVersion =>
        typeof(Transfer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    public static JsonObject ProposalToJson(Proposal p) => new()
    {
        ["id"] = p.Id,
        ["created_at"] = p.CreatedAt,
        ["updated_at"] = p.UpdatedAt,
        ["instruction"] = p.Instruction,
        ["provider"] = p.Provider,
        ["model"] = p.Model,
        ["diff"] = p.Diff,
        ["diff_sha256"] = p.DiffSha256,
        ["files"] = Strings(p.Files),
        ["commit_message"] = p.CommitMessage,
        ["status"] = p.Status.Value(),
        ["base_commit"] = p.BaseCommit,
        ["branch"] = p.Branch,
        ["commit_sha"] = p.CommitSha,
        ["check_error"] = p.CheckError,
        ["context"] = new JsonArray(p.Context
            .Select(c => (JsonNode?)new JsonObject(c.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))))
            .ToArray()),
        ["latency_ms"] = p.LatencyMs,
        ["is_sample"] = p.IsSample,
    };

    public static JsonObject TestRunToJson(TestRun r) => new()
    {
        ["id"] = r.Id,
        ["proposal_id"] = r.ProposalId,
        ["command"] = r.Command,
        ["started_at"] = r.StartedAt,
        ["duration_ms"] = r.DurationMs,
        ["exit_code"] = r.ExitCode,
        ["passed"] = r.Passed,
        ["output_tail"] = r.OutputTail,
        ["is_sample"] = r.IsSample,
    };

    public static JsonObject EventToJson(Event e) => new()
    {
        ["id"] = e.Id,
        ["ts"] = e.Ts,
        ["kind"] = e.Kind,
        ["summary"] = e.Summary,
        ["proposal_id"] = e.ProposalId,
        ["data"] = e.Data.DeepClone(),
        ["is_sample"] = e.IsSample,
    };

    public static JsonObject Export(Store store, bool includeSamples = true)
    {
        var proposals = store.ListProposals(limit: MaxRecords).Where(p => includeSamples || !p.IsSample).Reverse();
        var runs = store.ListTestRuns(limit: MaxRecords).Where(r => includeSamples || !r.IsSample).Reverse();
        var events = store.ListEvents(MaxRecords).Where(e => includeSamples || !e.IsSample).Reverse();
        var settings = new JsonObject();
        foreach (var (k, v) in store.Settings().Where(kv => !NonImportableSettings.Contains(kv.Key)))
            settings[k] = v;
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["exported_at"] = Clock.NowIso(),
            ["app_version"] = AppVersion,
            ["note"] = "Contém diffs propostos (podem incluir trechos de código). Não contém o índice nem segredos.",
            ["settings"] = settings,
            ["proposals"] = new JsonArray(proposals.Select(p => (JsonNode?)ProposalToJson(p)).ToArray()),
            ["test_runs"] = new JsonArray(runs.Select(r => (JsonNode?)TestRunToJson(r)).ToArray()),
            ["events"] = new JsonArray(events.Select(e => (JsonNode?)EventToJson(e)).ToArray()),
        };
    }

    public static Dictionary<string, int> ExportToFile(Store store, string path, bool includeSamples = true)
    {
        var data = Export(store, includeSamples);
        var tmp = path + ".tmp";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(tmp, data.ToJsonString(options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return new[] { "proposals", "test_runs", "events" }.ToDictionary(k => k, k => data[k]!.AsArray().Count);
    }

    // ------------------------------------------------------------------ validação
    private static JsonNode? Get(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var v) ? v : null;

    private static string? Str(JsonObject obj, string key, int maxLen, bool optional = false)
    {
        var value = Get(obj, key);
        if (value is null && optional)
            return null;
        if (value is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String)
            throw new ImportValidationException($"campo '{key}' deve ser texto");
        var text = jv.GetValue<string>();
        if (text.Length > maxLen)
            throw new ImportValidationException($"campo '{key}' longo demais");
        return text;
    }

    private static long? Int(JsonObject obj, string key, bool optional = false)
    {
        var value = Get(obj, key);
        if (value is null && optional)
            return null;
        if (value is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && jv.TryGetValue<long>(out var n))
            return n;
        if (value is JsonValue jd && jd.GetValueKind() == JsonValueKind.Number && jd.TryGetValue<double>(out var d) &&
            d == Math.Floor(d) && Math.Abs(d) < 9e15)
            return (long)d;
        throw new ImportValidationException($"campo '{key}' deve ser inteiro");
    }

    private static bool Bool(JsonObject obj, string key)
    {
        var value = Get(obj, key);
        if (value is null)
            return false;
        if (value is JsonValue jv && jv.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return jv.GetValue<bool>();
        throw new ImportValidationException($"campo '{key}' deve ser booleano");
    }

    private static string? Id(JsonObject obj, string key, bool optional = false)
    {
        var value = Get(obj, key);
        if (value is null && optional)
            return null;
        if (value is JsonValue jv && jv.GetValueKind() == JsonValueKind.String &&
            StatusInfo.IdRe().IsMatch(jv.GetValue<string>()))
            return jv.GetValue<string>();
        throw new ImportValidationException($"campo '{key}' não é um id válido");
    }

    private static string Ts(JsonObject obj, string key)
    {
        var value = Str(obj, key, 40)!;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new ImportValidationException($"campo '{key}' não é uma data ISO");
        return value;
    }

    private static string? Sha(JsonObject obj, string key)
    {
        var value = Str(obj, key, 64, optional: true);
        if (value is not null && !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ImportValidationException($"campo '{key}' não é um hash válido");
        return value;
    }

    private static JsonObject AsObject(JsonNode? raw, string what) =>
        raw as JsonObject ?? throw new ImportValidationException($"{what} não é um objeto");

    public static Proposal ParseProposal(JsonNode? raw)
    {
        var obj = AsObject(raw, "proposta");
        var statusNode = Get(obj, "status");
        var statusText = statusNode is JsonValue sv && sv.GetValueKind() == JsonValueKind.String ? sv.GetValue<string>() : null;
        if (!StatusInfo.TryParse(statusText, out var status))
            throw new ImportValidationException("estado de proposta inválido");
        var diff = Str(obj, "diff", 400_000);
        ParsedDiff parsed;
        try
        {
            parsed = Diffs.Validate(diff);
        }
        catch (DiffValidationException exc)
        {
            throw new ImportValidationException($"diff inválido: {exc.Message}");
        }
        var contextNode = Get(obj, "context") ?? new JsonArray();
        if (contextNode is not JsonArray contextArray || contextArray.Count > 500)
            throw new ImportValidationException("campo 'context' inválido");
        var context = new List<Dictionary<string, string>>();
        foreach (var item in contextArray)
        {
            if (item is not JsonObject entry)
                throw new ImportValidationException("campo 'context' inválido");
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in entry)
            {
                if (v is not JsonValue cv || cv.GetValueKind() != JsonValueKind.String || cv.GetValue<string>().Length >= 1100)
                    throw new ImportValidationException("campo 'context' inválido");
                dict[k] = cv.GetValue<string>();
            }
            context.Add(dict);
        }
        return new Proposal
        {
            Id = Id(obj, "id")!,
            CreatedAt = Ts(obj, "created_at"),
            UpdatedAt = Ts(obj, "updated_at"),
            Instruction = Str(obj, "instruction", 4000)!,
            Provider = Str(obj, "provider", 50)!,
            Model = Str(obj, "model", 200)!,
            Diff = parsed.Text,
            DiffSha256 = Clock.Sha256(parsed.Text),
            Files = parsed.Files,
            CommitMessage = Str(obj, "commit_message", 2000)!,
            Status = status,
            BaseCommit = Sha(obj, "base_commit"),
            Branch = Str(obj, "branch", 300, optional: true),
            CommitSha = Sha(obj, "commit_sha"),
            CheckError = Str(obj, "check_error", 2000, optional: true),
            Context = context,
            LatencyMs = Int(obj, "latency_ms", optional: true),
            IsSample = Bool(obj, "is_sample"),
        };
    }

    public static TestRun ParseTestRun(JsonNode? raw)
    {
        var obj = AsObject(raw, "execução de teste");
        var exitCode = Int(obj, "exit_code")!.Value;
        if (exitCode is < int.MinValue or > int.MaxValue)
            throw new ImportValidationException("campo 'exit_code' fora do intervalo");
        return new TestRun
        {
            Id = Id(obj, "id")!,
            ProposalId = Id(obj, "proposal_id", optional: true),
            Command = Str(obj, "command", 2000)!,
            StartedAt = Ts(obj, "started_at"),
            DurationMs = Int(obj, "duration_ms")!.Value,
            ExitCode = (int)exitCode,
            OutputTail = Str(obj, "output_tail", 20_000)!,
            IsSample = Bool(obj, "is_sample"),
        };
    }

    public static Event ParseEvent(JsonNode? raw)
    {
        var obj = AsObject(raw, "evento");
        var dataNode = Get(obj, "data") ?? new JsonObject();
        if (dataNode is not JsonObject data || data.ToJsonString().Length > 50_000)
            throw new ImportValidationException("campo 'data' do evento inválido");
        var kind = Str(obj, "kind", 40);
        if (string.IsNullOrEmpty(kind) || !kind.Replace("_", "").All(char.IsLetterOrDigit) || kind.Replace("_", "").Length == 0)
            throw new ImportValidationException("tipo de evento inválido");
        return new Event
        {
            Id = Id(obj, "id")!,
            Ts = Ts(obj, "ts"),
            Kind = kind,
            Summary = Str(obj, "summary", 1000)!,
            ProposalId = Id(obj, "proposal_id", optional: true),
            Data = (JsonObject)data.DeepClone(),
            IsSample = Bool(obj, "is_sample"),
        };
    }

    public static JsonObject LoadExportFile(string path)
    {
        if (!File.Exists(path))
            throw new ImportValidationException($"arquivo não encontrado: {path}");
        if (new FileInfo(path).Length > MaxImportBytes)
            throw new ImportValidationException("arquivo maior que 50 MB");
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        catch (Exception exc) when (exc is JsonException or DecoderFallbackException)
        {
            throw new ImportValidationException($"JSON inválido: {Safety.Truncate(exc.Message, 200)}");
        }
        if (node is not JsonObject data || Get(data, "format") is not JsonValue f ||
            f.GetValueKind() != JsonValueKind.String || f.GetValue<string>() != Format)
            throw new ImportValidationException("não é uma exportação do Gitruvian");
        if (Get(data, "version") is not JsonValue v || v.GetValueKind() != JsonValueKind.Number ||
            !v.TryGetValue<int>(out var version) || version != FormatVersion)
            throw new ImportValidationException($"versão de exportação não suportada: {Get(data, "version")?.ToJsonString() ?? "null"}");
        foreach (var key in new[] { "proposals", "test_runs", "events" })
        {
            var section = Get(data, key);
            if (section is null) continue;
            if (section is not JsonArray array || array.Count > MaxRecords)
                throw new ImportValidationException($"seção '{key}' inválida");
        }
        return data;
    }

    private static IEnumerable<JsonNode?> Section(JsonObject data, string key) =>
        Get(data, key) as JsonArray ?? [];

    /// <summary>Importa registros válidos de forma idempotente (ids existentes são ignorados).</summary>
    public static ImportReport Import(Store store, JsonObject data)
    {
        var report = new ImportReport();
        var knownIds = new HashSet<string>();
        using (var cmd = store.Command("SELECT id FROM proposals"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                knownIds.Add(reader.GetString(0));

        var index = 0;
        foreach (var raw in Section(data, "proposals"))
        {
            index++;
            Proposal proposal;
            try
            {
                proposal = ParseProposal(raw);
            }
            catch (ImportValidationException exc)
            {
                report.Skipped.Add($"proposta #{index}: {exc.Message}");
                continue;
            }
            if (store.InsertProposal(proposal)) report.Inserted["proposals"]++;
            else report.Duplicates++;
            knownIds.Add(proposal.Id);
        }
        index = 0;
        foreach (var raw in Section(data, "test_runs"))
        {
            index++;
            TestRun run;
            try
            {
                run = ParseTestRun(raw);
            }
            catch (ImportValidationException exc)
            {
                report.Skipped.Add($"teste #{index}: {exc.Message}");
                continue;
            }
            if (run.ProposalId is not null && !knownIds.Contains(run.ProposalId))
                run = run with { ProposalId = null };
            if (store.InsertTestRun(run)) report.Inserted["test_runs"]++;
            else report.Duplicates++;
        }
        index = 0;
        foreach (var raw in Section(data, "events"))
        {
            index++;
            Event ev;
            try
            {
                ev = ParseEvent(raw);
            }
            catch (ImportValidationException exc)
            {
                report.Skipped.Add($"evento #{index}: {exc.Message}");
                continue;
            }
            if (ev.ProposalId is not null && !knownIds.Contains(ev.ProposalId))
                ev = ev with { ProposalId = null };
            if (store.InsertEvent(ev)) report.Inserted["events"]++;
            else report.Duplicates++;
        }
        if (Get(data, "settings") is JsonObject settings)
        {
            foreach (var (key, _) in settings)
            {
                report.Skipped.Add(NonImportableSettings.Contains(key)
                    ? $"configuração '{key}' não importada por segurança (executa comandos)"
                    : $"configuração desconhecida '{Safety.Truncate(key, 40)}' ignorada");
            }
        }
        store.AddEvent("import",
            "Importação concluída: " + string.Join(", ", report.Inserted.Select(kv => $"{kv.Value} {kv.Key}")),
            data: new JsonObject
            {
                ["proposals"] = report.Inserted["proposals"],
                ["test_runs"] = report.Inserted["test_runs"],
                ["events"] = report.Inserted["events"],
                ["duplicates"] = report.Duplicates,
                ["skipped"] = report.Skipped.Count,
            });
        return report;
    }
}
