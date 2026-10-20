using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Gitruvian.Core;
using Gitruvian.Desktop.Infrastructure;
using Gitruvian.Desktop.ViewModels;
using Gitruvian.Desktop.Views;
using static Gitruvian.Desktop.Tests.Ui;

namespace Gitruvian.Desktop.Tests;

/// <summary>Smoke E2E da interface: ciclo completo dirigido pelos ViewModels e pela janela real (headless).</summary>
public sealed class MainWindowTests
{
    private static readonly string FakeModel = Path.Combine(AppContext.BaseDirectory, "fixtures", "fake_model.ps1");

    private static void RequireDotnet()
    {
        if (Processes.Which("dotnet") is null)
            Assert.Skip("SDK do .NET não encontrado no PATH: os testes do repositório de exemplo rodam com dotnet run.");
    }

    /// <summary>Configura o comando de testes do repositório de teste (PowerShell) e relê o estado.</summary>
    private static void UseTestCommand(MainViewModel vm)
    {
        Services.SetTestCommand(vm.Repo!, vm.Settings, Repo.TestCommand);
        vm.RefreshCommand.Execute(null);
        Settle(vm);
    }

    [AvaloniaFact]
    public void SampleFullCycle()
    {
        RequireDotnet();
        using var tmp = new TempDir();
        var dialogs = new FakeDialogs { Directory = tmp.Path };
        var vm = NewViewModel(tmp, dialogs: dialogs);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 860 };
        window.Show();
        Assert.True(vm.IsWelcome);
        Assert.False(vm.HasRecent);

        vm.CreateSampleCommand.Execute(null);
        Settle(vm);
        Assert.True(vm.IsRepoOpen);
        Assert.True(vm.Snapshot!.IsSample);
        Assert.Contains("[EXEMPLO]", vm.Title);
        Assert.Equal(MainViewModel.TabAssistant, vm.SelectedTab);
        var a = vm.Assistant;
        Assert.Single(a.Proposals);

        a.SelectedProposal = a.Proposals[0];
        Settle(vm);
        Assert.Equal(Status.Proposed, a.Current!.Status);
        Assert.Contains("FreteGratisAPartirDe", vm.Diff.Text);
        Assert.Contains(Sample.CarrinhoPath, vm.Diff.Text);
        Assert.True(a.ApplyCommand.CanExecute(null));
        Assert.False(a.AcceptCommand.CanExecute(null));

        a.ApplyCommand.Execute(null); // aplica e roda os testes automaticamente
        Settle(vm);
        Assert.Equal(Status.TestsPassed, a.Current!.Status);
        Assert.Equal("ok", vm.Banner.Kind);
        Assert.Contains("Testes passaram", vm.Banner.Text);
        Assert.Contains("PASSOU", a.TestOutput);
        Assert.True(vm.GraphItems[0].IsWorking); // a árvore de trabalho tem a alteração aplicada
        SaveFrame(window.CaptureRenderedFrame()!, "exemplo-testes-ok.png");

        a.AcceptCommand.Execute(null);
        Settle(vm);
        var p = a.Current!;
        Assert.Equal(Status.Committed, p.Status);
        Assert.Equal(2, vm.Snapshot!.Commits.Count);
        Assert.Equal(p.CommitSha, vm.Snapshot.Commits[0].Sha);
        Assert.Contains("Gitruvian-Proposal", Repo.Git(vm.Repo!, "log", "-1", "--format=%B"));
        Assert.Superset(new HashSet<string> { "apply", "test", "commit", "sample" },
            vm.Snapshot.Events.Select(e => e.Kind).ToHashSet());

        a.RevertCommand.Execute(null);
        Settle(vm);
        Assert.Equal(Status.Reverted, a.Current!.Status);
        Assert.Equal(3, vm.Snapshot!.Commits.Count);

        var root = vm.Repo!;
        vm.RemoveSampleCommand.Execute(null);
        Settle(vm);
        Assert.False(Directory.Exists(root));
        Assert.True(vm.IsWelcome);
        Assert.Null(vm.Repo);
        Assert.Contains("Apagar repositório de exemplo", dialogs.Confirmations);
        window.Close();
    }

    [AvaloniaFact]
    public void OpenRepoAsksBeforeCreatingData()
    {
        using var tmp = new TempDir();
        var repo = Repo.Make(Path.Combine(tmp.Path, "proj"));
        Repo.Write(Path.Combine(repo, "package.json"), "{\"scripts\": {\"test\": \"echo ok\"}}\n");
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var vm = NewViewModel(tmp, dialogs: dialogs);
        vm.OpenRepo(repo);
        Settle(vm);
        Assert.Null(vm.Repo);
        Assert.False(Directory.Exists(Path.Combine(repo, ".gitruvian")));
        Assert.Equal("info", vm.WelcomeBanner.Kind);

        dialogs.ConfirmAnswer = true;
        OpenAndInit(vm, repo);
        Assert.True(File.Exists(Path.Combine(repo, ".gitruvian", "gitruvian.db")));
        Assert.Contains("npm test", vm.Banner.Text); // comando de testes detectado
        Assert.Single(vm.Recent);
        Assert.Contains("/.gitruvian/", File.ReadAllText(Path.Combine(repo, ".git", "info", "exclude")));

        vm.CloseRepoCommand.Execute(null);
        vm.OpenRepo(tmp.Path); // não é um repositório git
        Settle(vm);
        Assert.Equal("error", vm.WelcomeBanner.Kind);
        Assert.Contains("git", vm.WelcomeBanner.Text);
    }

    [AvaloniaFact]
    public void ManualDiffValidationAndWorkingCopyCommit()
    {
        using var tmp = new TempDir();
        var repo = Repo.Make(Path.Combine(tmp.Path, "proj"));
        var vm = NewViewModel(tmp);
        OpenAndInit(vm, repo);
        UseTestCommand(vm);
        var a = vm.Assistant;
        Assert.True(a.Manual); // sem modelo configurado, o modo manual vem ligado
        Assert.False(a.ProposeCommand.CanExecute(null));
        a.Instruction = "ok";
        Assert.Contains("mínimo 3", a.Validation);

        a.Instruction = "corrige desconto";
        a.Patch = "isto não é um diff";
        a.ProposeCommand.Execute(null);
        Settle(vm);
        Assert.Equal("error", vm.Banner.Kind);
        Assert.Empty(a.Proposals);

        a.Patch = Repo.BadDiff;
        a.ProposeCommand.Execute(null);
        Settle(vm);
        Assert.Single(a.Proposals);
        Assert.Equal("", a.Instruction);
        a.ApplyCommand.Execute(null);
        Settle(vm);
        Assert.Equal(Status.TestsFailed, a.Current!.Status);
        Assert.Equal("error", vm.Banner.Kind);
        Assert.Contains("falharam", vm.Banner.Text);
        Assert.False(a.AcceptCommand.CanExecute(null));
        a.RevertCommand.Execute(null);
        Settle(vm);
        Assert.Equal(Status.Reverted, a.Current!.Status);

        // Alterações locais: preparar e commitar pela aba Alterações.
        Repo.Write(Path.Combine(repo, "calc.ps1"), "function Get-Total([double[]]$Prices) {\n    return ($Prices | Measure-Object -Sum).Sum\n}\n");
        vm.RefreshCommand.Execute(null);
        Settle(vm);
        var c = vm.Changes;
        Assert.True(c.HasChanges);
        Assert.Equal(2, c.Unstaged.Count); // calc.ps1 e .env
        Assert.False(c.CommitCommand.CanExecute(null));

        c.SelectedUnstaged.Add(c.Unstaged.First(f => f.Path == "calc.ps1"));
        Settle(vm);
        Assert.Contains("+    return ($Prices | Measure-Object -Sum).Sum", vm.Diff.Text);
        c.SelectedUnstaged.Clear();
        c.SelectedUnstaged.Add(c.Unstaged.First(f => f.Path == ".env"));
        Settle(vm);
        Assert.DoesNotContain("supersecret", vm.Diff.Text);
        Assert.Contains("oculto", vm.Diff.Text);
        c.SelectedUnstaged.Clear();
        c.SelectedUnstaged.Add(c.Unstaged.First(f => f.Path == "calc.ps1"));
        c.StageCommand.Execute(null);
        Settle(vm);
        Assert.Single(c.Staged);
        c.Message = "Simplifica total";
        Assert.True(c.CommitCommand.CanExecute(null));
        c.CommitCommand.Execute(null);
        Settle(vm);
        Assert.Equal("ok", vm.Banner.Kind);
        Assert.Contains("Commit", vm.Banner.Text);
        Assert.Equal("Simplifica total", vm.Snapshot!.Commits[0].Subject);
        Assert.Equal("", c.Message);
    }

    [AvaloniaFact]
    public void DiscardAsksAndRemovesUntrackedFile()
    {
        using var tmp = new TempDir();
        var repo = Repo.Make(Path.Combine(tmp.Path, "proj"));
        var dialogs = new FakeDialogs();
        var vm = NewViewModel(tmp, dialogs: dialogs);
        OpenAndInit(vm, repo);
        Repo.Write(Path.Combine(repo, "rascunho.txt"), "temporário\n");
        vm.RefreshCommand.Execute(null);
        Settle(vm);
        var c = vm.Changes;
        dialogs.ConfirmAnswer = false;
        c.SelectedUnstaged.Add(c.Unstaged.First(f => f.Path == "rascunho.txt"));
        c.DiscardCommand.Execute(null);
        Settle(vm);
        Assert.True(File.Exists(Path.Combine(repo, "rascunho.txt")));
        dialogs.ConfirmAnswer = true;
        c.DiscardCommand.Execute(null);
        Settle(vm);
        Assert.False(File.Exists(Path.Combine(repo, "rascunho.txt")));
        Assert.Contains("Descartar alterações", dialogs.Confirmations);
    }

    [AvaloniaFact]
    public void ModelProviderThroughCommand()
    {
        using var tmp = new TempDir();
        var shell = Processes.Which("pwsh") ?? Processes.Which("powershell");
        if (shell is null) Assert.Skip("PowerShell não encontrado no PATH");
        var command = $"\"{shell}\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{FakeModel}\"";
        var settings = new Settings { Provider = "command", Model = "modelo-falso-de-teste", ModelCommand = command };
        var repo = Repo.Make(Path.Combine(tmp.Path, "proj"));
        var vm = NewViewModel(tmp, settings);
        OpenAndInit(vm, repo);
        UseTestCommand(vm);
        var a = vm.Assistant;
        Assert.True(a.ProviderReady);
        Assert.False(a.Manual);
        a.Instruction = "aplique o desconto como percentual";
        a.Files = "calc.ps1";
        a.ProposeCommand.Execute(null);
        Settle(vm);
        Assert.NotNull(a.Current);
        Assert.Equal("command", a.Current!.Provider);
        Assert.Equal("Aplica o desconto como percentual", a.CommitMessage);
        a.ApplyCommand.Execute(null);
        Settle(vm);
        Assert.True(a.Current!.Status == Status.TestsPassed, vm.Banner.Text);
    }

    [AvaloniaFact]
    public void DegradedModeBlocksModelRequests()
    {
        using var tmp = new TempDir();
        var vm = NewViewModel(tmp);
        OpenAndInit(vm, Repo.Make(Path.Combine(tmp.Path, "proj")));
        var a = vm.Assistant;
        a.Manual = false;
        a.Instruction = "corrige desconto";
        Assert.False(a.ProposeCommand.CanExecute(null));
        Assert.Contains("Nenhum modelo", a.Validation);
        Assert.Contains("nada sai desta máquina", a.ProviderExplanation);
    }

    [AvaloniaFact]
    public void ExportImportAndErrors()
    {
        using var tmp = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = NewViewModel(tmp, dialogs: dialogs);
        OpenAndInit(vm, Repo.Make(Path.Combine(tmp.Path, "proj")));
        vm.Assistant.Instruction = "corrige desconto";
        vm.Assistant.Patch = Repo.FixDiff;
        vm.Assistant.ProposeCommand.Execute(null);
        Settle(vm);
        var target = Path.Combine(tmp.Path, "export.json");
        dialogs.SaveFile = target;
        vm.ExportDataCommand.Execute(null);
        Settle(vm);
        Assert.True(File.Exists(target));
        Assert.Equal("ok", vm.Banner.Kind);

        OpenAndInit(vm, Repo.Make(Path.Combine(tmp.Path, "outro")));
        dialogs.OpenFile = target;
        vm.ImportDataCommand.Execute(null);
        Settle(vm);
        Assert.Contains("1 proposta", vm.Banner.Text);
        Assert.Single(vm.Assistant.Proposals);

        var bad = Path.Combine(tmp.Path, "ruim.json");
        File.WriteAllText(bad, "{\"format\": \"outra-coisa\"}");
        dialogs.OpenFile = bad;
        vm.ImportDataCommand.Execute(null);
        Settle(vm);
        Assert.Equal("error", vm.Banner.Kind);
    }

    [AvaloniaFact]
    public void GraphWithMergeRendersAndIsKeyboardAccessible()
    {
        using var tmp = new TempDir();
        var repo = Repo.Make(Path.Combine(tmp.Path, "proj"));
        Repo.Git(repo, "switch", "-q", "-c", "feature");
        Repo.Write(Path.Combine(repo, "f.txt"), "f\n");
        Repo.Git(repo, "add", "-A");
        Repo.Git(repo, "commit", "-q", "-m", "feature");
        Repo.Git(repo, "switch", "-q", "-");
        Repo.Write(Path.Combine(repo, "m.txt"), "m\n");
        Repo.Git(repo, "add", "-A");
        Repo.Git(repo, "commit", "-q", "-m", "main");
        Repo.Git(repo, "merge", "-q", "--no-ff", "-m", "merge", "feature");
        Repo.Git(repo, "-c", "tag.gpgSign=false", "tag", "v1.0");

        var vm = NewViewModel(tmp);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 860 };
        window.Show();
        OpenAndInit(vm, repo);
        Assert.Equal(2, vm.Snapshot!.Rows.Max(r => r.Width));
        Assert.True(vm.Snapshot.Rows[0].IsMerge);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame); // desenha grafo, diff e abas sem erros
        SaveFrame(frame!, "grafo-merge.png");

        var commit = vm.GraphItems.First(i => !i.IsWorking && i.Subject == "main");
        vm.SelectedGraphItem = commit;
        Settle(vm);
        Assert.Equal(MainViewModel.TabCommit, vm.SelectedTab);
        Assert.True(vm.CommitDetail.HasCommit);
        Assert.NotEmpty(vm.CommitDetail.Files);
        Assert.Contains("m.txt", vm.Diff.Text);
        var top = vm.GraphItems.First(i => !i.IsWorking);
        Assert.Contains(top.Refs, r => r is { Text: "v1.0", Kind: "tag" });
        Assert.Contains(top.Refs, r => r is { Text: "master", Kind: "head" });

        // Troca de branch pela barra lateral.
        vm.SwitchBranchCommand.Execute(vm.Branches.First(b => b.Name == "feature"));
        Settle(vm);
        Assert.Equal("feature", vm.Snapshot!.Branch);
        SaveFrame(window.CaptureRenderedFrame()!, "commit-selecionado.png");

        foreach (var button in window.GetLogicalDescendants().OfType<Button>())
        {
            var label = AutomationProperties.GetName(button);
            var content = button.Content as string;
            Assert.False(string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(content),
                "botão sem rótulo acessível");
        }
        foreach (var box in window.GetLogicalDescendants().OfType<TextBox>())
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)), $"campo sem nome acessível: {box.Name}");
        foreach (var list in window.GetLogicalDescendants().OfType<ListBox>())
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(list)), $"lista sem nome acessível: {list.Name}");
        foreach (var gesture in new[] { "Ctrl+O", "F5", "Ctrl+I", "Ctrl+E", "Ctrl+Shift+E", "Ctrl+W", "F1" })
            Assert.Contains(window.KeyBindings, k => k.Gesture.Equals(Avalonia.Input.KeyGesture.Parse(gesture)));
        window.Close();
    }

    [AvaloniaFact]
    public void WelcomeScreenRendersEmptyState()
    {
        using var tmp = new TempDir();
        var vm = NewViewModel(tmp);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
        window.Show();
        Settle(vm);
        Assert.True(vm.IsWelcome);
        SaveFrame(window.CaptureRenderedFrame()!, "inicio.png");
        var focused = window.FocusManager?.GetFocusedElement() as Button;
        Assert.Equal("OpenButton", focused?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void AutoUpdateAsksOnceAndRespectsAnswer()
    {
        using var tmp = new TempDir();
        var updater = new FakeUpdater();
        var vm = NewViewModel(tmp, updater: updater);
        vm.StartupUpdateCheck();
        Settle(vm);
        Assert.True(vm.AskAutoUpdate); // primeira vez: pergunta antes de qualquer acesso à rede
        Assert.Equal(0, updater.Checks);
        Assert.Contains("example.invalid", vm.AutoUpdateQuestion);

        vm.AnswerAutoUpdateCommand.Execute("nao");
        Settle(vm);
        Assert.False(vm.AskAutoUpdate);
        Assert.Equal(0, updater.Checks);
        Assert.False(Preferences.Load(Path.Combine(tmp.Path, "cfg", "preferences.json")).AutoUpdate);

        var again = NewViewModel(tmp, updater: updater); // reabrir: não pergunta de novo nem verifica
        again.StartupUpdateCheck();
        Settle(again);
        Assert.False(again.AskAutoUpdate);
        Assert.Equal(0, updater.Checks);

        again.AnswerAutoUpdateCommand.Execute("sim");
        Settle(again);
        Assert.Equal(1, updater.Checks);
        Assert.True(again.UpdateReady);
        Assert.Equal("ok", again.WelcomeBanner.Kind);
        again.OnClosing();
        Assert.True(updater.Applied); // aplicada ao sair

        var off = NewViewModel(tmp, new Settings { AutoUpdate = AutoUpdateMode.Off }, updater: new FakeUpdater());
        off.StartupUpdateCheck();
        Settle(off);
        Assert.False(off.AskAutoUpdate);

        var portable = new FakeUpdater { IsInstalled = false }; // rodando sem instalar: nem pergunta
        using var devTmp = new TempDir();
        var dev = NewViewModel(devTmp, updater: portable);
        dev.StartupUpdateCheck();
        Settle(dev);
        Assert.False(dev.AskAutoUpdate);
        Assert.Equal(0, portable.Checks);
    }

    [AvaloniaFact]
    public void ManualUpdateCheckReportsStateAndErrors()
    {
        using var tmp = new TempDir();
        var updater = new FakeUpdater { Next = UpdateState.UpToDate };
        var vm = NewViewModel(tmp, updater: updater);
        vm.CheckUpdatesCommand.Execute(null);
        Settle(vm);
        Assert.Equal("info", vm.WelcomeBanner.Kind);
        Assert.Contains("mais recente", vm.WelcomeBanner.Text);

        updater.Next = UpdateState.NotInstalled;
        vm.CheckUpdatesCommand.Execute(null);
        Settle(vm);
        Assert.Contains("versão instalada", vm.WelcomeBanner.Text);
        Assert.False(vm.UpdateReady);
    }

    private static void SaveFrame(Avalonia.Media.Imaging.WriteableBitmap frame, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, name));
    }
}
