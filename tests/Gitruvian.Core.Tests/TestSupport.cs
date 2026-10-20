using System.Text;

// O núcleo tem estado global de processo (caminho do log): os testes rodam em série.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Gitruvian.Core.Tests;

/// <summary>Textos do repositório de teste e utilitários.</summary>
public static class TestSupport
{
    // Strings normais (não interpoladas): o conteúdo PowerShell usa '$' livremente.
    public const string CalcBuggy =
        "function Get-Total([double[]]$Prices, [double]$Discount = 0.0) {\n" +
        "    # Soma os preços e aplica um desconto percentual (0.1 = 10%).\n" +
        "    $subtotal = ($Prices | Measure-Object -Sum).Sum\n" +
        "    return $subtotal - $Discount\n" +
        "}\n";

    public const string CalcTests =
        "# Testes de calc.ps1 (sem dependências): sai com código 1 se algum falhar.\n" +
        ". \"$PSScriptRoot/../calc.ps1\"\n" +
        "$falhas = 0\n" +
        "function Confere([string]$Nome, [double]$Esperado, [double]$Obtido) {\n" +
        "    if ([math]::Abs($Esperado - $Obtido) -lt 1e-9) { Write-Output \"ok    $Nome\" }\n" +
        "    else { Write-Output \"FALHA $Nome`: esperado $Esperado, obtido $Obtido\"; $script:falhas++ }\n" +
        "}\n" +
        "Confere 'sem desconto' 30 (Get-Total @(10, 20))\n" +
        "Confere 'desconto percentual' 27 (Get-Total @(10, 20) 0.1)\n" +
        "if ($falhas) { Write-Output \"FALHOU: $falhas teste(s)\"; exit 1 }\n" +
        "Write-Output 'PASSOU: 2 testes'\n" +
        "exit 0\n";

    public const string FixDiff =
        "diff --git a/calc.ps1 b/calc.ps1\n" +
        "--- a/calc.ps1\n" +
        "+++ b/calc.ps1\n" +
        "@@ -1,5 +1,5 @@\n" +
        " function Get-Total([double[]]$Prices, [double]$Discount = 0.0) {\n" +
        "     # Soma os preços e aplica um desconto percentual (0.1 = 10%).\n" +
        "     $subtotal = ($Prices | Measure-Object -Sum).Sum\n" +
        "-    return $subtotal - $Discount\n" +
        "+    return $subtotal - $subtotal * $Discount\n" +
        " }\n";

    public const string BadDiff =
        "diff --git a/calc.ps1 b/calc.ps1\n" +
        "--- a/calc.ps1\n" +
        "+++ b/calc.ps1\n" +
        "@@ -1,5 +1,5 @@\n" +
        " function Get-Total([double[]]$Prices, [double]$Discount = 0.0) {\n" +
        "     # Soma os preços e aplica um desconto percentual (0.1 = 10%).\n" +
        "     $subtotal = ($Prices | Measure-Object -Sum).Sum\n" +
        "-    return $subtotal - $Discount\n" +
        "+    return $subtotal + $Discount\n" +
        " }\n";

    public const string SecretValue = "supersecret-value-123456";
    public const string TestCommand = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tests/Testes.ps1";

    /// <summary>Roda git no repositório e devolve o stdout (falha o teste se o git falhar).</summary>
    public static string RunGit(string repo, params string[] args) => Core.Git.Run(repo, args).Stdout;

    /// <summary>Grava texto em UTF-8 sem BOM e com o conteúdo exatamente como recebido (LF).</summary>
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>Cria uma pasta git com identidade e opções locais que isolam a configuração global da máquina.</summary>
    public static void InitGit(string root)
    {
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-q");
        RunGit(root, "config", "user.name", "Teste Gitruvian");
        RunGit(root, "config", "user.email", "teste@example.invalid");
        RunGit(root, "config", "commit.gpgsign", "false");
        RunGit(root, "config", "tag.gpgSign", "false");
        RunGit(root, "config", "core.autocrlf", "false");
    }

    /// <summary>Repositório de teste: calc.ps1 com bug, tests/Testes.ps1 e um .env sensível não rastreado.</summary>
    public static string MakeRepo(string root)
    {
        InitGit(root);
        Write(Path.Combine(root, "calc.ps1"), CalcBuggy);
        Write(Path.Combine(root, "tests", "Testes.ps1"), CalcTests);
        Write(Path.Combine(root, ".gitignore"), "*.log\n");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-q", "-m", "inicial");
        // Arquivo sensível não rastreado: nunca pode ser indexado nem enviado ao modelo.
        Write(Path.Combine(root, ".env"), $"API_KEY={SecretValue}\n");
        return Core.Git.FindRepoRoot(root);
    }

    public static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

    /// <summary>O comando de testes do repositório de teste usa o powershell.exe; pula o teste se não houver.</summary>
    public static void RequireTestShell()
    {
        if (Processes.Which("powershell") is null)
            Assert.Skip("powershell não encontrado no PATH");
    }

    /// <summary>O exemplo (Loja) roda com dotnet run; pula o teste se não houver SDK do .NET.</summary>
    public static void RequireDotnet()
    {
        if (Processes.Which("dotnet") is null)
            Assert.Skip("dotnet não encontrado no PATH");
    }

    /// <summary>Linha de comando que roda um script PowerShell (pwsh, se houver; senão o powershell.exe do Windows).</summary>
    public static string PowerShellCommand(string script)
    {
        var shell = Processes.Which("pwsh") ?? Processes.Which("powershell");
        if (shell is null)
            Assert.Skip("PowerShell não encontrado no PATH");
        return $"\"{shell}\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"";
    }

    /// <summary>Caminho de tests/fixtures/fake_model.ps1, achado subindo a partir da pasta de saída.</summary>
    public static string FakeModelPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "fake_model.ps1");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("tests/fixtures/fake_model.ps1 não encontrado");
    }

    /// <summary>Ambiente isolado para Config.Load: nunca lê o .env real do usuário.</summary>
    public static Dictionary<string, string> IsolatedEnv(string tmp, params (string Key, string Value)[] values)
    {
        var env = new Dictionary<string, string> { ["GITRUVIAN_ENV_FILE"] = Path.Combine(tmp, "nao-existe.env") };
        foreach (var (k, v) in values)
            env[k] = v;
        return env;
    }
}

/// <summary>Pasta temporária apagada no fim do teste.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitruvian-tests", Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        Log.Close();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                    return;
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Repositório de teste aberto com o comando de testes configurado.</summary>
public sealed class TestApp : IDisposable
{
    public TempDir Tmp { get; } = new();
    public string Repo { get; }
    public AppSession App { get; }

    public TestApp(Settings? settings = null)
    {
        Repo = TestSupport.MakeRepo(Tmp["proj"]);
        App = AppSession.Open(Repo, create: true, settings: settings ?? new Settings());
        App.Store.SetSetting(Workflow.TestCommandKey, TestSupport.TestCommand);
    }

    public Store Store => App.Store;

    public string File(string relative) => Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Tipos dos eventos em ordem cronológica.</summary>
    public List<string> Kinds() => Store.ListEvents().AsEnumerable().Reverse().Select(e => e.Kind).ToList();

    public void Dispose()
    {
        App.Dispose();
        Tmp.Dispose();
    }
}

/// <summary>Provedor de teste: devolve uma resposta fixa e guarda o prompt recebido.</summary>
public sealed class RecordingProvider(string reply) : IModelProvider
{
    public List<string> Prompts { get; } = [];
    public string Name => "teste";
    public string Model => "modelo-de-teste";

    public ModelReply Complete(string system, string user)
    {
        Prompts.Add(user);
        return new ModelReply(reply, Model, 5);
    }
}
