using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Gitruvian.Core;

/// <summary>O modelo respondeu com erro ou de forma inutilizável.</summary>
public class ModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Nenhum modelo utilizável está configurado (modo degradado).</summary>
public sealed class ModelUnavailableException(string message, Exception? inner = null) : ModelException(message, inner);

public sealed record ModelReply(string Text, string Model, long LatencyMs);

public interface IModelProvider
{
    string Name { get; }
    string Model { get; }
    ModelReply Complete(string system, string user);
}

/// <summary>Provedores de modelo. Todos são opcionais: sem modelo, o app segue útil (modo manual).</summary>
public static class Llm
{
    public const string SystemPrompt = """
        You are a careful software engineer working inside a local git repository.
        Propose the smallest correct change that fulfils the user's request.

        Reply with exactly:
        1. A first line `COMMIT: <short imperative commit message>`.
        2. One fenced code block tagged `diff` containing a unified diff in `git diff` format:
           paths prefixed with a/ and b/, relative to the repository root, 3 lines of unchanged
           context copied exactly from the files shown, correct @@ hunk headers.

        Do not add explanations outside those two items. Only modify files shown in the context,
        unless a new file is strictly necessary. Never touch .env files, credentials, the .git or
        .gitruvian directories. If the request cannot be done safely, reply with
        `COMMIT: none` and no diff.
        """;

    public const string DegradedExplanation =
        "Nenhum modelo configurado (GITRUVIAN_PROVIDER=none). O Gitruvian continua útil: indexa e busca no " +
        "código, valida e aplica diffs que você cola no modo manual, roda testes, commita e registra tudo. " +
        "Para gerar diffs automaticamente, configure um provedor no .env.";

    public static string BuildUserPrompt(string instruction, string contextText) =>
        $"Request from the user:\n{instruction}\n\n" +
        $"Repository context (may be partial):\n{contextText}\n\n" +
        "Return the COMMIT line and the diff block now.";

    public static IModelProvider GetProvider(Settings settings)
    {
        if (settings.Provider == "none")
            throw new ModelUnavailableException(DegradedExplanation);
        if (settings.IsRemote && !settings.AllowRemote)
            throw new ModelUnavailableException(
                "este provedor envia trechos do seu código para fora da máquina. " +
                "Para permitir, defina GITRUVIAN_ALLOW_REMOTE=1 no .env.");
        return settings.Provider switch
        {
            "anthropic" => new AnthropicProvider(settings),
            "openai_compat" => new OpenAiCompatProvider(settings),
            "command" => new CommandProvider(settings),
            _ => throw new ModelUnavailableException($"provedor desconhecido: {settings.Provider}"),
        };
    }

    /// <summary>Resumo legível do modo atual: (rótulo, explicação, pronto). Nunca inclui segredos.</summary>
    public static (string Label, string Explanation, bool Ready) Describe(Settings settings)
    {
        if (settings.Provider == "none")
            return ("manual (modo degradado)", DegradedExplanation, false);
        var where = settings.IsRemote ? "remoto" : "local";
        var label = $"{settings.Provider} · {(settings.Model.Length > 0 ? settings.Model : "(modelo não definido)")} · {where}";
        try
        {
            GetProvider(settings);
        }
        catch (ModelUnavailableException exc)
        {
            return (label + " · indisponível", exc.Message, false);
        }
        return settings.Provider switch
        {
            "anthropic" => settings.AnthropicApiKey.Length == 0
                ? (label + " · indisponível", "ANTHROPIC_API_KEY não definida no .env.", false)
                : (label, "Chave ANTHROPIC_API_KEY presente. Trechos do código são enviados à API da Anthropic.", true),
            "openai_compat" => (label, $"Endpoint: {settings.OpenAiBaseUrl}", true),
            _ => (label, "Comando local configurado em GITRUVIAN_MODEL_COMMAND.", true),
        };
    }
}

// --------------------------------------------------------------------- Anthropic
public sealed class AnthropicProvider(Settings settings) : IModelProvider
{
    public string Name => "anthropic";
    public string Model { get; } = settings.Model;

    /// <summary>Modelos que aceitam o fallback automático do lado do servidor em caso de recusa.</summary>
    public static readonly IReadOnlySet<string> FallbackModels =
        new HashSet<string> { "claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5" };

    /// <summary>Monta o pedido enviado à API (sem rede).</summary>
    public static MessageCreateParams BuildParams(string model, string system, string user)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = system,
            Messages = [new() { Role = Role.User, Content = user }],
        };
        if (!FallbackModels.Contains(model))
            return parameters;
        // Em caso de recusa pelos classificadores, a própria API refaz o pedido num modelo substituto.
        return parameters with
        {
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };
    }

    public ModelReply Complete(string system, string user)
    {
        if (settings.AnthropicApiKey.Length == 0)
            throw new ModelUnavailableException("ANTHROPIC_API_KEY não definida no .env");
        var client = new AnthropicClient
        {
            ApiKey = settings.AnthropicApiKey,
            Timeout = TimeSpan.FromSeconds(settings.Timeout),
            MaxRetries = 2,
        };
        var watch = Stopwatch.StartNew();
        BetaMessage response;
        try
        {
            response = client.Beta.Messages.Create(BuildParams(Model, system, user)).GetAwaiter().GetResult();
        }
        catch (AnthropicUnauthorizedException exc)
        {
            throw new ModelException("chave da Anthropic rejeitada (401). Confira ANTHROPIC_API_KEY.", exc);
        }
        catch (AnthropicNotFoundException exc)
        {
            throw new ModelException($"modelo '{Model}' não encontrado (404). Confira GITRUVIAN_MODEL.", exc);
        }
        catch (AnthropicRateLimitException exc)
        {
            throw new ModelException("limite de uso atingido (429). Tente novamente em instantes.", exc);
        }
        catch (AnthropicIOException exc)
        {
            throw new ModelUnavailableException("sem conexão com a API da Anthropic.", exc);
        }
        catch (AnthropicApiException exc)
        {
            throw new ModelException($"a API da Anthropic respondeu com erro: {Safety.Truncate(exc.Message, 200)}", exc);
        }
        var latency = watch.ElapsedMilliseconds;
        var stopReason = response.StopReason?.ToString() ?? "";
        if (stopReason.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            throw new ModelException("o modelo recusou o pedido. Reformule a solicitação.");
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        if (stopReason.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            text += "\n"; // o diff provavelmente foi cortado; a validação vai acusar
        return new ModelReply(text, response.Model.ToString() ?? Model, latency);
    }
}

// ------------------------------------------------------- OpenAI-compatível local
/// <summary>Qualquer servidor com /v1/chat/completions (Ollama, LM Studio, llama.cpp, vLLM...).</summary>
public sealed class OpenAiCompatProvider : IModelProvider
{
    private readonly Settings _settings;

    public OpenAiCompatProvider(Settings settings)
    {
        if (settings.Model.Length == 0)
            throw new ModelUnavailableException("defina GITRUVIAN_MODEL (ex.: qwen2.5-coder:7b no Ollama)");
        _settings = settings;
        Model = settings.Model;
    }

    public string Name => "openai_compat";
    public string Model { get; }

    public ModelReply Complete(string system, string user)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0.1,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var url = $"{_settings.OpenAiBaseUrl}/chat/completions";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_settings.Timeout) };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_settings.OpenAiApiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);
        var watch = Stopwatch.StartNew();
        string payloadText;
        try
        {
            using var response = http.Send(request);
            if (!response.IsSuccessStatusCode)
                throw new ModelException($"o servidor de modelo respondeu HTTP {(int)response.StatusCode} em {url}");
            using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
            payloadText = reader.ReadToEnd();
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ModelUnavailableException(
                $"não foi possível conectar em {_settings.OpenAiBaseUrl} (o servidor local está rodando? ex.: 'ollama serve')",
                exc);
        }
        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(payloadText);
        }
        catch (JsonException exc)
        {
            throw new ModelException("o servidor de modelo devolveu JSON inválido", exc);
        }
        string text;
        try
        {
            text = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new ModelException("resposta do servidor sem 'choices[0].message.content'");
        }
        catch (Exception exc) when (exc is InvalidOperationException or FormatException)
        {
            throw new ModelException("resposta do servidor sem 'choices[0].message.content'", exc);
        }
        var model = payload?["model"] is JsonValue m && m.TryGetValue<string>(out var name) && name.Length > 0 ? name : Model;
        return new ModelReply(text, model, watch.ElapsedMilliseconds);
    }
}

// ------------------------------------------------------------ Comando local (CLI)
/// <summary>Executa um programa local que lê o prompt no stdin e escreve a resposta no stdout.</summary>
public sealed class CommandProvider : IModelProvider
{
    private readonly string _command;
    private readonly int _timeout;

    public CommandProvider(Settings settings)
    {
        if (settings.ModelCommand.Length == 0)
            throw new ModelUnavailableException("defina GITRUVIAN_MODEL_COMMAND (ex.: ollama run qwen2.5-coder:7b)");
        _command = settings.ModelCommand;
        _timeout = settings.Timeout;
        var argv = CommandLine.Split(_command);
        Model = settings.Model.Length > 0 ? settings.Model : argv.FirstOrDefault() ?? "command";
    }

    public string Name => "command";
    public string Model { get; }

    public ModelReply Complete(string system, string user)
    {
        var argv = CommandLine.Split(_command);
        ProcessResult result;
        try
        {
            // O prompt vai em UTF-8 pelo stdin; a resposta é lida como UTF-8.
            result = Processes.Run(argv, null, Encoding.UTF8.GetBytes($"{system}\n\n{user}"), _timeout);
        }
        catch (FileNotFoundException exc)
        {
            throw new ModelUnavailableException($"comando não encontrado: {argv.FirstOrDefault()}", exc);
        }
        if (result.TimedOut)
            throw new ModelException($"o comando do modelo excedeu {_timeout}s");
        if (result.ExitCode != 0)
            throw new ModelException($"o comando do modelo terminou com código {result.ExitCode}");
        return new ModelReply(result.StdoutText, Model, result.DurationMs);
    }
}
