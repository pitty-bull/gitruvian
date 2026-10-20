using System.Text;

namespace Gitruvian.Core;

/// <summary>Configuração inválida no ambiente ou no .env.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>Preferência de atualização automática definida no .env (vazio = perguntar ao usuário).</summary>
public enum AutoUpdateMode
{
    Ask,
    On,
    Off,
}

/// <summary>Configuração vinda do ambiente e de arquivos .env (nunca do banco).</summary>
public sealed record Settings
{
    public const string DefaultAnthropicModel = "claude-opus-5-5";
    public const string DefaultOpenAiBaseUrl = "http://localhost:11434/v1";
    public static readonly string[] Providers = ["none", "anthropic", "openai_compat", "command"];
    private static readonly HashSet<string> LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public string Provider { get; init; } = "none";
    public string Model { get; init; } = "";
    public bool AllowRemote { get; init; }
    public int Timeout { get; init; } = 120;
    public int MaxContextChars { get; init; } = 24000;
    public string OpenAiBaseUrl { get; init; } = DefaultOpenAiBaseUrl;
    public string ModelCommand { get; init; } = "";
    public string TestCommand { get; init; } = "";
    public int TestTimeout { get; init; } = 600;
    public string DataDir { get; init; } = "";
    public AutoUpdateMode AutoUpdate { get; init; } = AutoUpdateMode.Ask;
    public string UpdateUrl { get; init; } = "";
    public IReadOnlyList<string> EnvFiles { get; init; } = [];

    // Segredos: nunca aparecem em ToString() nem em logs.
    public string AnthropicApiKey { get; init; } = "";
    public string OpenAiApiKey { get; init; } = "";

    /// <summary>True se o provedor envia trechos de código para fora desta máquina.</summary>
    public bool IsRemote
    {
        get
        {
            if (Provider == "anthropic")
                return true;
            if (Provider == "openai_compat")
            {
                var host = Uri.TryCreate(OpenAiBaseUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";
                return !LocalHosts.Contains(host);
            }
            return false;
        }
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Provider = {Provider}, Model = {Model}, AllowRemote = {AllowRemote}, ");
        builder.Append($"AnthropicApiKey = {(AnthropicApiKey.Length > 0 ? "***" : "")}, ");
        builder.Append($"OpenAiApiKey = {(OpenAiApiKey.Length > 0 ? "***" : "")}");
        return true;
    }
}

public static class Config
{
    /// <summary>Só estas chaves são lidas de arquivos .env; o resto é ignorado.</summary>
    public static readonly HashSet<string> KnownKeys =
    [
        "GITRUVIAN_PROVIDER", "GITRUVIAN_MODEL", "GITRUVIAN_ALLOW_REMOTE", "GITRUVIAN_TIMEOUT",
        "GITRUVIAN_MAX_CONTEXT_CHARS", "GITRUVIAN_OPENAI_BASE_URL", "GITRUVIAN_OPENAI_API_KEY",
        "GITRUVIAN_MODEL_COMMAND", "GITRUVIAN_TEST_COMMAND", "GITRUVIAN_TEST_TIMEOUT", "GITRUVIAN_DATA_DIR",
        "GITRUVIAN_AUTO_UPDATE", "GITRUVIAN_UPDATE_URL", "ANTHROPIC_API_KEY",
    ];

    /// <summary>Lê um arquivo .env simples (CHAVE=valor). Ignora chaves desconhecidas.</summary>
    public static Dictionary<string, string> ParseEnvFile(string path)
    {
        var values = new Dictionary<string, string>();
        string raw;
        try
        {
            raw = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return values;
        }
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
                continue;
            if (line.StartsWith("export "))
                line = line["export ".Length..];
            var idx = line.IndexOf('=');
            var key = line[..idx].Trim();
            if (!KnownKeys.Contains(key))
                continue;
            var value = line[(idx + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0] &&
                value.Count(c => c == value[0]) == 2)
                value = value[1..^1];
            else if (value.Contains(" #"))
                value = value[..value.IndexOf(" #", StringComparison.Ordinal)].TrimEnd();
            values[key] = value;
        }
        return values;
    }

    /// <summary>Pasta de configuração do usuário: %APPDATA%\gitruvian (Windows) ou ~/.config/gitruvian.</summary>
    public static string UserConfigDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            return Path.Combine(appData, "gitruvian");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(xdg, "gitruvian");
    }

    /// <summary>
    /// Ordem de precedência dos arquivos .env (o primeiro que define a chave vence). Se GITRUVIAN_ENV_FILE
    /// estiver definido, somente esse arquivo é lido.
    /// </summary>
    public static List<string> EnvFileCandidates(string cwd, IReadOnlyDictionary<string, string> environ)
    {
        if (environ.TryGetValue("GITRUVIAN_ENV_FILE", out var explicitFile) && explicitFile.Length > 0)
            return [explicitFile];
        return [Path.Combine(cwd, ".env"), Path.Combine(UserConfigDir(), ".env")];
    }

    public static Dictionary<string, string> ProcessEnvironment()
    {
        var result = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            result[(string)entry.Key] = entry.Value as string ?? "";
        return result;
    }

    /// <summary>Mescla variáveis de ambiente com arquivos .env sem alterar o ambiente do processo.</summary>
    public static (Dictionary<string, string> Values, List<string> Used) LoadValues(string? cwd = null,
        IReadOnlyDictionary<string, string>? environ = null)
    {
        environ ??= ProcessEnvironment();
        cwd ??= Environment.CurrentDirectory;
        var merged = new Dictionary<string, string>();
        var used = new List<string>();
        foreach (var candidate in Enumerable.Reverse(EnvFileCandidates(cwd, environ)))
        {
            if (!File.Exists(candidate))
                continue;
            var parsed = ParseEnvFile(candidate);
            if (parsed.Count > 0)
                used.Add(candidate);
            foreach (var (k, v) in parsed)
                merged[k] = v;
        }
        foreach (var key in KnownKeys)
            if (environ.TryGetValue(key, out var value) && value.Length > 0)
                merged[key] = value;
        used.Reverse();
        return (merged, used);
    }

    private static int Int(Dictionary<string, string> values, string key, int fallback, int min, int max)
    {
        var raw = values.GetValueOrDefault(key, "").Trim();
        if (raw.Length == 0)
            return fallback;
        if (!int.TryParse(raw, out var number))
            throw new ConfigException($"{key} deve ser um número inteiro");
        if (number < min || number > max)
            throw new ConfigException($"{key} deve estar entre {min} e {max}");
        return number;
    }

    private static bool Bool(Dictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key, "").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "sim" or "on";

    private static bool IsFalse(string raw) => raw.Trim().ToLowerInvariant() is "0" or "false" or "no" or "nao" or "não" or "off";

    public static Settings Load(string? cwd = null, IReadOnlyDictionary<string, string>? environ = null)
    {
        var (values, used) = LoadValues(cwd, environ);
        var provider = values.GetValueOrDefault("GITRUVIAN_PROVIDER", "none").Trim().ToLowerInvariant();
        if (provider.Length == 0) provider = "none";
        if (!Settings.Providers.Contains(provider))
            throw new ConfigException($"GITRUVIAN_PROVIDER inválido: use um de {string.Join(", ", Settings.Providers)}");
        var model = values.GetValueOrDefault("GITRUVIAN_MODEL", "").Trim();
        if (provider == "anthropic" && model.Length == 0)
            model = Settings.DefaultAnthropicModel;
        var baseUrl = values.GetValueOrDefault("GITRUVIAN_OPENAI_BASE_URL", "").Trim();
        if (baseUrl.Length == 0) baseUrl = Settings.DefaultOpenAiBaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ConfigException("GITRUVIAN_OPENAI_BASE_URL deve começar com http:// ou https://");
        var updateRaw = values.GetValueOrDefault("GITRUVIAN_AUTO_UPDATE", "").Trim();
        var autoUpdate = updateRaw.Length == 0 ? AutoUpdateMode.Ask
            : Bool(values, "GITRUVIAN_AUTO_UPDATE") ? AutoUpdateMode.On
            : IsFalse(updateRaw) ? AutoUpdateMode.Off
            : throw new ConfigException("GITRUVIAN_AUTO_UPDATE deve ser 1 (ligado), 0 (desligado) ou vazio (perguntar)");
        var updateUrl = values.GetValueOrDefault("GITRUVIAN_UPDATE_URL", "").Trim();
        if (updateUrl.Length > 0 && !Path.IsPathRooted(updateUrl) &&
            !(Uri.TryCreate(updateUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            throw new ConfigException("GITRUVIAN_UPDATE_URL deve ser um endereço http(s) ou uma pasta local absoluta");
        return new Settings
        {
            Provider = provider,
            Model = model,
            AllowRemote = Bool(values, "GITRUVIAN_ALLOW_REMOTE"),
            Timeout = Int(values, "GITRUVIAN_TIMEOUT", 120, 5, 3600),
            MaxContextChars = Int(values, "GITRUVIAN_MAX_CONTEXT_CHARS", 24000, 2000, 400000),
            OpenAiBaseUrl = baseUrl.TrimEnd('/'),
            ModelCommand = values.GetValueOrDefault("GITRUVIAN_MODEL_COMMAND", "").Trim(),
            TestCommand = values.GetValueOrDefault("GITRUVIAN_TEST_COMMAND", "").Trim(),
            TestTimeout = Int(values, "GITRUVIAN_TEST_TIMEOUT", 600, 5, 86400),
            DataDir = values.GetValueOrDefault("GITRUVIAN_DATA_DIR", "").Trim(),
            AutoUpdate = autoUpdate,
            UpdateUrl = updateUrl,
            EnvFiles = used,
            AnthropicApiKey = values.GetValueOrDefault("ANTHROPIC_API_KEY", "").Trim(),
            OpenAiApiKey = values.GetValueOrDefault("GITRUVIAN_OPENAI_API_KEY", "").Trim(),
        };
    }
}
