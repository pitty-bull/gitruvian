using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Gitruvian.Core;
using Gitruvian.Desktop;
using Gitruvian.Desktop.Infrastructure;
using Gitruvian.Desktop.Tests;
using Gitruvian.Desktop.ViewModels;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Gitruvian.Desktop.Tests;

public static class TestAppBuilder
{
    // Renderização real com Skia (sem janela na tela): permite capturar quadros e checar que tudo desenha.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Diálogos com respostas prontas (substituem as janelas modais).</summary>
public sealed class FakeDialogs : IDialogs
{
    public bool ConfirmAnswer { get; set; } = true;
    public string? Directory { get; set; }
    public string? OpenFile { get; set; }
    public string? SaveFile { get; set; }
    public string? Text { get; set; }
    public string? SettingsAnswer { get; set; }
    public List<string> Confirmations { get; } = [];
    public List<string> Infos { get; } = [];

    public Task<bool> ConfirmAsync(string title, string text, string confirmLabel = "OK", bool danger = false)
    {
        Confirmations.Add(title);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> AskDirectoryAsync(string title) => Task.FromResult(Directory);
    public Task<string?> AskOpenFileAsync(string title, string filterName, string[] patterns) => Task.FromResult(OpenFile);
    public Task<string?> AskSaveFileAsync(string title, string defaultName) => Task.FromResult(SaveFile);
    public Task<string?> AskTextAsync(string title, string prompt, string initial = "") => Task.FromResult(Text);

    public Task ShowInfoAsync(string title, string text)
    {
        Infos.Add(title + "\n" + text);
        return Task.CompletedTask;
    }

    public Task<string?> EditRepoSettingsAsync(RepoSettingsForm form) => Task.FromResult(SettingsAnswer);
}

/// <summary>Atualizador falso: nenhuma rede nos testes.</summary>
public sealed class FakeUpdater : IUpdater
{
    public string SourceDescription => "https://example.invalid/releases";
    public bool IsInstalled { get; set; } = true;
    public int Checks { get; private set; }
    public UpdateState Next { get; set; } = UpdateState.Available;
    public bool Applied { get; private set; }

    public UpdateCheck Check()
    {
        Checks++;
        return new UpdateCheck(Next, "0.3.0", Next == UpdateState.Available ? "0.3.1" : null, Next switch
        {
            UpdateState.Available => "Versão 0.3.1 disponível.",
            UpdateState.NotInstalled => "A atualização automática só funciona na versão instalada pelo Setup do Gitruvian.",
            _ => "Você está na versão mais recente.",
        });
    }

    public UpdateCheck Download(Action<int>? progress = null) =>
        new(UpdateState.Downloaded, "0.3.0", "0.3.1", "Versão 0.3.1 baixada. Reinicie para concluir a atualização.");

    public void ApplyAndRestart() => Applied = true;
    public void ApplyOnExit() => Applied = true;
}

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitruvian-ui-" + Guid.NewGuid().ToString("N")[..10]);
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            System.IO.Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class Repo
{
    public const string CalcBuggy = "function Get-Total([double[]]$Prices, [double]$Discount = 0.0) {\n" +
                                    "    # Soma os preços e aplica um desconto percentual (0.1 = 10%).\n" +
                                    "    $subtotal = ($Prices | Measure-Object -Sum).Sum\n" +
                                    "    return $subtotal - $Discount\n" +
                                    "}\n";

    public const string CalcTest = "# Testes de calc.ps1 (sem dependências): sai com código 1 se algum falhar.\n" +
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

    /// <summary>Comando de testes do repositório de teste (PowerShell do Windows, sem dependências).</summary>
    public const string TestCommand = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File tests/Testes.ps1";

    private const string DiffHead = "diff --git a/calc.ps1 b/calc.ps1\n--- a/calc.ps1\n+++ b/calc.ps1\n@@ -1,5 +1,5 @@\n" +
                                    " function Get-Total([double[]]$Prices, [double]$Discount = 0.0) {\n" +
                                    "     # Soma os preços e aplica um desconto percentual (0.1 = 10%).\n" +
                                    "     $subtotal = ($Prices | Measure-Object -Sum).Sum\n" +
                                    "-    return $subtotal - $Discount\n";

    public const string FixDiff = DiffHead + "+    return $subtotal - $subtotal * $Discount\n }\n";
    public const string BadDiff = DiffHead + "+    return $subtotal + $Discount\n }\n";
    public const string SecretValue = "supersecret-value-123456";

    public static string Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} falhou: {error}");
        return output;
    }

    public static void Write(string path, string content)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Repositório de teste: calc.ps1 com bug, testes em PowerShell e um .env com segredo não rastreado.</summary>
    public static string Make(string root)
    {
        System.IO.Directory.CreateDirectory(root);
        Git(root, "init", "-q");
        foreach (var (k, v) in new[]
                 {
                     ("user.name", "Teste Gitruvian"), ("user.email", "teste@example.invalid"), ("commit.gpgsign", "false"),
                     ("tag.gpgSign", "false"), ("core.autocrlf", "false"),
                 })
            Git(root, "config", k, v);
        Write(System.IO.Path.Combine(root, "calc.ps1"), CalcBuggy);
        Write(System.IO.Path.Combine(root, "tests", "Testes.ps1"), CalcTest);
        Write(System.IO.Path.Combine(root, ".gitignore"), "*.log\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "inicial");
        Write(System.IO.Path.Combine(root, ".env"), $"API_KEY={SecretValue}\n");
        return root;
    }
}

public static class Ui
{
    public static MainViewModel NewViewModel(TempDir tmp, Settings? settings = null, FakeDialogs? dialogs = null,
        IUpdater? updater = null) =>
        new(settings ?? new Settings(), dialogs ?? new FakeDialogs(), updater,
            System.IO.Path.Combine(tmp.Path, "cfg", "recent.json"), System.IO.Path.Combine(tmp.Path, "cfg", "preferences.json"));

    /// <summary>Espera as tarefas em segundo plano, inclusive as encadeadas pelos callbacks.</summary>
    public static void Settle(MainViewModel vm, int timeoutSeconds = 180)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (vm.Runner.Pending == 0)
            {
                Dispatcher.UIThread.RunJobs();
                if (vm.Runner.Pending == 0) return;
            }
            if (watch.Elapsed.TotalSeconds > timeoutSeconds)
                throw new TimeoutException("tarefas em segundo plano não terminaram");
            Thread.Sleep(10);
        }
    }

    public static void OpenAndInit(MainViewModel vm, string repo)
    {
        vm.OpenRepo(repo);
        Settle(vm);
        Assert.Equal(Path.GetFullPath(repo).TrimEnd('\\'), vm.Repo?.TrimEnd('\\'), ignoreCase: true);
    }
}
