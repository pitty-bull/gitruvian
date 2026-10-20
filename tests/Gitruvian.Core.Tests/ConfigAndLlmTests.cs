using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Core;
using MessageCreateParams = Anthropic.Models.Beta.Messages.MessageCreateParams;

namespace Gitruvian.Core.Tests;

/// <summary>Configuração e provedores de modelo (sem nenhuma chamada de rede real).</summary>
public sealed class ConfigAndLlmTests
{
    [Fact]
    public void ParseEnvFileReadsOnlyKnownKeys()
    {
        using var tmp = new TempDir();
        var env = tmp[".env"];
        TestSupport.Write(env,
            "# comentário\nGITRUVIAN_PROVIDER='command'\nOUTRA_COISA=ignorada\nexport GITRUVIAN_TIMEOUT=30 # s\n");
        Assert.Equal(new Dictionary<string, string> { ["GITRUVIAN_PROVIDER"] = "command", ["GITRUVIAN_TIMEOUT"] = "30" },
            Config.ParseEnvFile(env));
        TestSupport.Write(env, "GITRUVIAN_MODEL_COMMAND=\"C:/a b/pwsh.exe\" \"x.ps1\"\n");
        Assert.Equal("\"C:/a b/pwsh.exe\" \"x.ps1\"", Config.ParseEnvFile(env)["GITRUVIAN_MODEL_COMMAND"]);
    }

    [Fact]
    public void EnvironmentOverridesEnvFile()
    {
        using var tmp = new TempDir();
        var env = tmp["custom.env"];
        TestSupport.Write(env, "GITRUVIAN_PROVIDER=command\nGITRUVIAN_TIMEOUT=30\n");
        var settings = Config.Load(tmp.Path,
            new Dictionary<string, string> { ["GITRUVIAN_ENV_FILE"] = env, ["GITRUVIAN_TIMEOUT"] = "45" });
        Assert.Equal("command", settings.Provider);
        Assert.Equal(45, settings.Timeout);
    }

    [Fact]
    public void InvalidConfigIsRejected()
    {
        using var tmp = new TempDir();
        Assert.Throws<ConfigException>(() => Config.Load(tmp.Path,
            new Dictionary<string, string> { ["GITRUVIAN_ENV_FILE"] = tmp["x"], ["GITRUVIAN_PROVIDER"] = "magic" }));
        Assert.Throws<ConfigException>(() => Config.Load(tmp.Path,
            new Dictionary<string, string> { ["GITRUVIAN_ENV_FILE"] = tmp["x"], ["GITRUVIAN_TIMEOUT"] = "abc" }));
    }

    [Fact]
    public void SecretsHiddenFromToStringAndDescription()
    {
        var s = new Settings
        {
            Provider = "anthropic", Model = "claude-opus-5-5", AnthropicApiKey = "sk-ant-secret-xyz", AllowRemote = true,
        };
        Assert.DoesNotContain("sk-ant-secret-xyz", s.ToString());
        var (label, explanation, _) = Llm.Describe(s);
        Assert.DoesNotContain("sk-ant-secret-xyz", label + " " + explanation);
    }

    [Fact]
    public void DefaultIsPrivateDegradedMode()
    {
        using var tmp = new TempDir();
        var s = Config.Load(tmp.Path, TestSupport.IsolatedEnv(tmp.Path));
        Assert.Equal("none", s.Provider);
        Assert.False(s.IsRemote);
        Assert.Throws<ModelUnavailableException>(() => Llm.GetProvider(s));
    }

    [Fact]
    public void RemoteDetection()
    {
        Assert.False(new Settings { Provider = "openai_compat", OpenAiBaseUrl = "http://localhost:11434/v1" }.IsRemote);
        Assert.True(new Settings { Provider = "openai_compat", OpenAiBaseUrl = "https://api.example.com/v1" }.IsRemote);
        var exc = Assert.Throws<ModelUnavailableException>(() => Llm.GetProvider(
            new Settings { Provider = "openai_compat", Model = "m", OpenAiBaseUrl = "https://api.example.com/v1" }));
        Assert.Contains("ALLOW_REMOTE", exc.Message);
    }

    [Fact]
    public void AnthropicRequestShape()
    {
        var parameters = AnthropicProvider.BuildParams("claude-opus-5-5", "sys", "user");
        var body = parameters.RawBodyData;
        Assert.Equal(JsonValueKind.String, body["fallbacks"].ValueKind);
        Assert.Equal("default", body["fallbacks"].GetString());
        Assert.Equal("""[{"role":"user","content":"user"}]""", JsonNode.Parse(body["messages"].GetRawText())!.ToJsonString());
        Assert.Equal(["server-side-fallback-2026-07-01"], parameters.Betas!.Select(b => b.ToString().Trim('"')));

        // Pela API pública: o dicionário cru do corpo serializado com System.Text.Json.
        var rawJson = JsonSerializer.Serialize(body);
        Assert.Contains("\"fallbacks\":\"default\"", rawJson);

        // Corpo HTTP serializado pelo próprio SDK (o mesmo que iria para a API), sem enviar nada.
        // BodyContent() e AddHeadersToRequest() são internos no SDK: chamados por reflexão só no teste.
        var json = SdkBody(parameters);
        Assert.Contains("\"fallbacks\":\"default\"", json);
        var parsed = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("default", parsed["fallbacks"]!.GetValue<string>());
        Assert.Equal("claude-opus-5-5", parsed["model"]!.GetValue<string>());
        Assert.Equal(16000, parsed["max_tokens"]!.GetValue<int>());
        Assert.False(parsed.ContainsKey("betas")); // betas viajam no cabeçalho anthropic-beta, não no corpo
        Assert.Contains("server-side-fallback-2026-07-01", SdkBetaHeader(parameters));

        var plain = AnthropicProvider.BuildParams("claude-haiku-5-5", "s", "u");
        Assert.False(plain.RawBodyData.ContainsKey("fallbacks"));
        Assert.DoesNotContain("fallbacks", SdkBody(plain));
        Assert.True(plain.Betas is null || plain.Betas.Count == 0);
        Assert.DoesNotContain("server-side-fallback", SdkBetaHeader(plain));
    }

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static string SdkBody(MessageCreateParams parameters)
    {
        var method = typeof(MessageCreateParams).GetMethod("BodyContent", AnyInstance, Type.EmptyTypes)!;
        using var content = (HttpContent)method.Invoke(parameters, null)!;
        using var reader = new StreamReader(content.ReadAsStream(TestContext.Current.CancellationToken), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string SdkBetaHeader(MessageCreateParams parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/messages");
        var method = typeof(MessageCreateParams).GetMethod("AddHeadersToRequest", AnyInstance,
            [typeof(HttpRequestMessage), typeof(ClientOptions)])!;
        method.Invoke(parameters, [request, new ClientOptions { ApiKey = "k" }]);
        return request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : "";
    }

    /// <summary>Servidor HTTP mínimo em 127.0.0.1 (porta livre) que responde como /v1/chat/completions.</summary>
    private sealed class FakeOpenAiServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _loop;
        public List<(string Path, JsonObject Body, string? Auth)> Received { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeOpenAiServer()
        {
            _listener.Start();
            _loop = Task.Run(ServeOne);
        }

        private async Task ServeOne()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
            {
                var n = await stream.ReadAsync(chunk);
                if (n == 0) return;
                buffer.AddRange(chunk[..n]);
            }
            var head = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd).Split("\r\n");
            var path = head[0].Split(' ')[1];
            var headers = head.Skip(1).Select(h => h.Split(':', 2))
                .ToDictionary(h => h[0].Trim().ToLowerInvariant(), h => h[1].Trim());
            var length = int.Parse(headers["content-length"]);
            while (buffer.Count < headerEnd + 4 + length)
            {
                var n = await stream.ReadAsync(chunk);
                if (n == 0) break;
                buffer.AddRange(chunk[..n]);
            }
            var body = JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray(), headerEnd + 4, length))!.AsObject();
            Received.Add((path, body, headers.GetValueOrDefault("authorization")));
            var payload = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["model"] = body["model"]!.GetValue<string>(),
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = "COMMIT: ok" } }),
            }.ToJsonString());
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }

        private static int IndexOf(List<byte> data, byte[] pattern)
        {
            for (var i = 0; i + pattern.Length <= data.Count; i++)
                if (!pattern.Where((b, j) => data[i + j] != b).Any())
                    return i;
            return -1;
        }

        public void Dispose()
        {
            _listener.Stop();
            try { _loop.Wait(2000); } catch (AggregateException) { }
        }
    }

    [Fact]
    public void OpenAiCompatProviderOverRealHttp()
    {
        using var server = new FakeOpenAiServer();
        var settings = new Settings
        {
            Provider = "openai_compat", Model = "modelo-local",
            OpenAiBaseUrl = $"http://127.0.0.1:{server.Port}/v1", OpenAiApiKey = "tok",
        };
        var reply = Llm.GetProvider(settings).Complete("sys", "user");
        Assert.Equal("COMMIT: ok", reply.Text);
        Assert.Equal("modelo-local", reply.Model);
        var req = server.Received[^1];
        Assert.Equal("/v1/chat/completions", req.Path);
        Assert.Equal("Bearer tok", req.Auth);
        Assert.Equal("""{"role":"system","content":"sys"}""", req.Body["messages"]![0]!.ToJsonString());
    }

    [Fact]
    public void OpenAiCompatUnreachableIsDegraded()
    {
        var settings = new Settings { Provider = "openai_compat", Model = "m", OpenAiBaseUrl = "http://127.0.0.1:9/v1", Timeout = 5 };
        var exc = Assert.Throws<ModelUnavailableException>(() => new OpenAiCompatProvider(settings).Complete("s", "u"));
        Assert.Contains("conectar", exc.Message);
    }

    [Fact]
    public void CommandProvider()
    {
        using var tmp = new TempDir();
        var script = tmp["echo_model.ps1"];
        TestSupport.Write(script, "$data = [Console]::In.ReadToEnd()\n[Console]::Out.Write(\"RECEBI $($data.Length -gt 0)\")\n");
        var provider = new CommandProvider(new Settings { Provider = "command", ModelCommand = TestSupport.PowerShellCommand(script) });
        Assert.Equal("RECEBI True", provider.Complete("s", "u").Text.Trim());
        var exit4 = tmp["exit4.ps1"];
        TestSupport.Write(exit4, "exit 4\n");
        var failing = new CommandProvider(new Settings
        {
            Provider = "command", ModelCommand = TestSupport.PowerShellCommand(exit4),
        });
        var exc = Assert.Throws<ModelException>(() => failing.Complete("s", "u"));
        Assert.Contains("código 4", exc.Message);
    }
}
