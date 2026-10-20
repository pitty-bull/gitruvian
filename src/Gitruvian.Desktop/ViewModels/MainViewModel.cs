using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gitruvian.Core;
using Gitruvian.Desktop.Infrastructure;

namespace Gitruvian.Desktop.ViewModels;

/// <summary>Estado da janela principal: tela inicial, repositório aberto, grafo, diff e as quatro abas.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int TabChanges = 0, TabCommit = 1, TabAssistant = 2, TabHistory = 3;

    public const string ShortcutsHelp = """
        Ctrl+O — abrir repositório
        F5 — atualizar
        Ctrl+I — indexar o código
        Ctrl+B — novo branch
        Ctrl+E / Ctrl+Shift+E — exportar / importar dados
        Ctrl+, — configurações do repositório
        Ctrl+W — fechar repositório
        Alt+1 … Alt+4 — abas Alterações, Commit, Assistente, Histórico
        Ctrl+Enter — commitar (na mensagem) ou propor (no pedido)
        Enter — trocar para o branch selecionado; abrir o repositório recente selecionado
        Delete — tirar o repositório selecionado da lista de recentes
        F1 — esta ajuda
        Tab / Shift+Tab — navegar entre os campos; Alt + letra sublinhada aciona menus, botões e rótulos
        """;

    private readonly string? _recentFile;
    private readonly string? _preferencesFile;

    public MainViewModel(Settings settings, IDialogs dialogs, IUpdater? updater = null, string? recentFile = null,
        string? preferencesFile = null)
    {
        Settings = settings;
        Dialogs = dialogs;
        Updater = updater;
        _recentFile = recentFile;
        _preferencesFile = preferencesFile;
        Changes = new ChangesViewModel(this);
        CommitDetail = new CommitViewModel(this);
        Assistant = new AssistantViewModel(this);
        History = new HistoryViewModel(this);
        Runner.PropertyChanged += OnRunnerChanged;
        LoadRecent();
    }

    public Settings Settings { get; }
    public IDialogs Dialogs { get; set; }
    public IUpdater? Updater { get; }
    public TaskRunner Runner { get; } = new();
    public BannerViewModel Banner { get; } = new();
    public BannerViewModel WelcomeBanner { get; } = new();
    public DiffViewModel Diff { get; } = new();
    public ChangesViewModel Changes { get; }
    public CommitViewModel CommitDetail { get; }
    public AssistantViewModel Assistant { get; }
    public HistoryViewModel History { get; }
    public ObservableCollection<string> Recent { get; } = [];
    public ObservableCollection<GraphItem> GraphItems { get; } = [];
    public ObservableCollection<BranchItem> Branches { get; } = [];
    public string Version => Transfer.AppVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsRepoOpen), nameof(Title), nameof(RepoName))]
    private string? _repo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(StatusText), nameof(BranchLabel), nameof(IsSampleRepo))]
    private Snapshot? _snapshot;

    [ObservableProperty]
    private GraphItem? _selectedGraphItem;

    [ObservableProperty]
    private BranchItem? _selectedBranch;

    [ObservableProperty]
    private string? _selectedRecent;

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    private int _recentCount;

    // Atualizações
    [ObservableProperty]
    private bool _askAutoUpdate;

    [ObservableProperty]
    private string _updateMessage = "";

    [ObservableProperty]
    private bool _updateReady;

    public bool IsWelcome => Repo is null;
    public bool IsRepoOpen => Repo is not null;
    public bool HasRecent => RecentCount > 0;
    public bool IsSampleRepo => Snapshot?.IsSample == true;
    public string RepoName => Repo is null ? "" : Path.GetFileName(Repo.TrimEnd('\\', '/'));
    public string BranchLabel => Snapshot is null ? "" : $"Branch: {Snapshot.Branch}";
    public bool IsBusy => Runner.Busy;

    public string Title => Snapshot is null || Repo is null
        ? "Gitruvian"
        : $"Gitruvian — {RepoName} ({Snapshot.Branch}){(Snapshot.IsSample ? " [EXEMPLO]" : "")}";

    public string StatusText
    {
        get
        {
            if (Runner.Busy)
                return Runner.BusyLabel + "…";
            if (Snapshot is null)
                return "Pronto. Nenhum repositório aberto.";
            var index = Snapshot.IndexFiles > 0
                ? $"índice: {Snapshot.IndexFiles} arquivos ({GraphItem.FormatIso(Snapshot.IndexLast)})"
                : "índice vazio (Ctrl+I para indexar)";
            return $"{Snapshot.Commits.Count} commits · {Snapshot.Status.Count} alterações locais · " +
                   $"{Snapshot.Proposals.Count} propostas · {index} · modelo: {Snapshot.ProviderLabel}";
        }
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskRunner.Busy) or nameof(TaskRunner.BusyLabel))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(StatusText));
            RefreshCommands();
        }
    }

    /// <summary>Reavalia o estado habilitado de todos os comandos (após mudanças de estado ou de "ocupado").</summary>
    public void RefreshCommands()
    {
        foreach (var command in new IRelayCommand[]
                 {
                     OpenRepoCommand, CloseRepoCommand, RefreshCommand, ReindexCommand, NewBranchCommand,
                     ExportDataCommand, ImportDataCommand, OpenSettingsCommand, CreateSampleCommand,
                     ClearSamplesCommand, RemoveSampleCommand, SwitchBranchCommand, OpenRecentCommand,
                     ForgetRecentCommand, CheckUpdatesCommand,
                 })
            command.NotifyCanExecuteChanged();
        Changes.RefreshState();
        Assistant.RefreshState();
    }

    // ================================================================== mensagens
    public void Notify(string kind, string message, string hint = "")
    {
        if (IsWelcome) WelcomeBanner.Show(kind, message, hint);
        else Banner.Show(kind, message, hint);
    }

    public void Fail(Exception exc)
    {
        var (message, hint) = ErrorText.Friendly(exc);
        Notify("error", message, hint);
    }

    /// <summary>Roda trabalho em segundo plano e trata o erro de forma padrão.</summary>
    public void Run<T>(string label, Func<T> work, Action<T> onDone, bool quiet = false, Action<Exception>? onError = null) =>
        Runner.Submit(label, work, onDone, onError ?? Fail, quiet);

    public void Run(string label, Action work, Action onDone, bool quiet = false) =>
        Runner.Submit(label, work, onDone, Fail, quiet);

    private bool CanAct => !Runner.Busy;
    private bool CanActOnRepo => !Runner.Busy && Repo is not null;

    // ================================================================== recentes
    private void LoadRecent()
    {
        Recent.Clear();
        foreach (var repo in RecentRepos.Load(_recentFile)) Recent.Add(repo);
        RecentCount = Recent.Count;
    }

    private void SetRecent(IEnumerable<string> repos)
    {
        Recent.Clear();
        foreach (var repo in repos) Recent.Add(repo);
        RecentCount = Recent.Count;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void OpenRecent(string? path)
    {
        path ??= SelectedRecent;
        if (path is not null) OpenRepo(path);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void ForgetRecent(string? path)
    {
        path ??= SelectedRecent;
        if (path is null) return;
        try
        {
            SetRecent(RecentRepos.Forget(path, _recentFile));
            WelcomeBanner.Show("info", $"{path} saiu da lista de recentes (nada foi apagado do disco).");
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Fail(exc);
        }
    }

    // ================================================================== repositório
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task OpenRepoAsync()
    {
        var path = await Dialogs.AskDirectoryAsync("Abrir repositório git");
        if (path is not null) OpenRepo(path);
    }

    /// <summary>Abre (e, com confirmação, inicializa) o repositório da pasta indicada.</summary>
    public void OpenRepo(string path)
    {
        Run("Abrindo repositório", () => Services.InspectRepo(path, Settings), info =>
        {
            if (info.Initialized)
            {
                Activate(info.Root);
                return;
            }
            _ = ConfirmInitAsync(info);
        });
    }

    private async Task ConfirmInitAsync(RepoInfo info)
    {
        var dataDir = AppSession.DataDirFor(info.Root, Settings);
        var ok = await Dialogs.ConfirmAsync("Inicializar o Gitruvian",
            $"O Gitruvian ainda não foi usado em {info.Root}.\n\nPara continuar ele cria a pasta de dados local " +
            $"{dataDir} (banco SQLite com propostas, testes e histórico) e a adiciona ao .git/info/exclude, " +
            "sem mexer no seu .gitignore. Nada sai desta máquina.", "Inicializar");
        if (!ok)
        {
            Notify("info", "Nada foi criado. Escolha outro repositório ou confirme a inicialização.");
            return;
        }
        Run("Inicializando", () => Services.InitRepo(info.Root, Settings), command =>
        {
            Activate(info.Root, command is null
                ? "Repositório pronto. Nenhum comando de testes detectado: defina um em Repositório › Configurações."
                : $"Repositório pronto. Comando de testes detectado: {command}");
        });
    }

    private void Activate(string root, string? message = null)
    {
        Repo = root;
        Banner.Clear();
        WelcomeBanner.Clear();
        Diff.ShowPlaceholder("Selecione um commit, um arquivo ou uma proposta para ver as alterações.");
        try
        {
            SetRecent(RecentRepos.Remember(root, _recentFile));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // A lista de recentes é conveniência: falhar ao salvá-la não impede abrir o repositório.
        }
        Refresh(() =>
        {
            if (message is not null) Notify("ok", message);
            SelectedTab = Snapshot?.Status.Count > 0 ? TabChanges : TabAssistant;
        });
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private void CloseRepo()
    {
        Repo = null;
        Snapshot = null;
        GraphItems.Clear();
        Branches.Clear();
        Changes.Clear();
        CommitDetail.Clear();
        Assistant.Clear();
        History.Clear();
        Banner.Clear();
        Diff.ShowPlaceholder("");
        LoadRecent();
        RefreshCommands();
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private void Refresh() => Refresh(null);

    /// <summary>Relê o estado do repositório; <paramref name="then"/> roda depois de redesenhar.</summary>
    public void Refresh(Action? then)
    {
        var repo = Repo;
        if (repo is null) return;
        Run("Lendo o repositório", () => Services.TakeSnapshot(repo, Settings), snap =>
        {
            if (Repo != repo) return; // o usuário fechou ou trocou de repositório nesse meio-tempo
            Render(snap);
            then?.Invoke();
        });
    }

    private void Render(Snapshot snap)
    {
        var selectedSha = SelectedGraphItem?.Sha;
        Snapshot = snap;
        GraphItems.Clear();
        foreach (var item in GraphItem.Build(snap)) GraphItems.Add(item);
        _restoringSelection = true;
        SelectedGraphItem = GraphItems.FirstOrDefault(i => i.Sha == selectedSha);
        _restoringSelection = false;
        Branches.Clear();
        foreach (var b in snap.Branches) Branches.Add(new BranchItem(b.Name, b.IsCurrent));
        Changes.Load(snap);
        Assistant.Load(snap);
        History.Load(snap);
        RefreshCommands();
    }

    private bool _restoringSelection;

    partial void OnSelectedGraphItemChanged(GraphItem? value)
    {
        if (_restoringSelection || value is null || Repo is null)
            return;
        if (value.IsWorking)
        {
            SelectedTab = TabChanges;
            Changes.ShowSelectionDiff();
            return;
        }
        SelectedTab = TabCommit;
        CommitDetail.LoadCommit(value.Sha);
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private void Reindex()
    {
        var repo = Repo!;
        Run("Indexando o código", () => Services.Reindex(repo, Settings), result =>
        {
            var skipped = string.Join(", ", result.Skipped.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}"));
            Refresh(() => Notify("ok",
                $"Índice atualizado: {result.Added} novos, {result.Updated} alterados, {result.Removed} removidos, " +
                $"{result.Unchanged} sem mudança.",
                skipped.Length > 0 ? $"Ignorados: {skipped}. Arquivos sensíveis nunca são indexados." : ""));
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private async Task NewBranchAsync()
    {
        var name = await Dialogs.AskTextAsync("Novo branch", "Nome do novo branch (a partir do HEAD atual):");
        if (string.IsNullOrWhiteSpace(name)) return;
        var repo = Repo!;
        Run("Criando branch", () => Services.SwitchBranch(repo, name, create: true),
            () => Refresh(() => Notify("ok", $"Branch {name.Trim()} criado e selecionado.")));
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private void SwitchBranch(BranchItem? branch)
    {
        branch ??= SelectedBranch;
        if (branch is null || branch.IsCurrent) return;
        var repo = Repo!;
        Run("Trocando de branch", () => Services.SwitchBranch(repo, branch.Name, create: false),
            () => Refresh(() => Notify("ok", $"Agora no branch {branch.Name}.")));
    }

    // ================================================================== dados
    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private async Task ExportDataAsync()
    {
        var target = await Dialogs.AskSaveFileAsync("Exportar dados do Gitruvian",
            $"gitruvian-{RepoName}-{DateTime.Now:yyyyMMdd-HHmm}.json");
        if (target is null) return;
        var repo = Repo!;
        Run("Exportando", () => Services.ExportData(repo, Settings, target, includeSamples: true), counts =>
            Refresh(() => Notify("ok", $"Exportado para {target}: {counts["proposals"]} propostas, " +
                                       $"{counts["test_runs"]} execuções de teste, {counts["events"]} eventos.",
                "O arquivo contém os diffs propostos (podem incluir trechos de código). Guarde-o como guardaria o código.")));
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private async Task ImportDataAsync()
    {
        var source = await Dialogs.AskOpenFileAsync("Importar dados do Gitruvian", "Exportação do Gitruvian", ["*.json"]);
        if (source is null) return;
        var repo = Repo!;
        Run("Importando", () => Services.ImportFile(repo, Settings, source), report => Refresh(() =>
        {
            var inserted = report.Inserted;
            var text = $"Importado: {inserted["proposals"]} proposta(s), {inserted["test_runs"]} execução(ões) de teste, " +
                       $"{inserted["events"]} evento(s); {report.Duplicates} já existiam.";
            Notify(report.Skipped.Count > 0 ? "warn" : "ok", text,
                report.Skipped.Count > 0 ? $"{report.Skipped.Count} item(ns) ignorado(s): {string.Join("; ", report.Skipped.Take(3))}" : "");
        }));
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private async Task OpenSettingsAsync()
    {
        var snap = Snapshot;
        if (snap is null) return;
        var form = new RepoSettingsForm(snap.TestCommand ?? "", Settings.TestCommand.Length > 0, snap.ProviderLabel,
            snap.ProviderExplanation, Settings.EnvFiles.Count > 0 ? string.Join("\n", Settings.EnvFiles) : "nenhum",
            snap.DataDir);
        var command = await Dialogs.EditRepoSettingsAsync(form);
        if (command is null) return;
        var repo = Repo!;
        Run("Salvando configurações", () => Services.SetTestCommand(repo, Settings, command), () => Refresh(() =>
            Notify("ok", command.Trim().Length > 0 ? $"Comando de testes: {command.Trim()}" : "Comando de testes removido.")));
    }

    // ================================================================== exemplo
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CreateSampleAsync()
    {
        var parent = await Dialogs.AskDirectoryAsync("Onde criar o repositório de exemplo?");
        if (parent is null) return;
        var target = Path.Combine(parent, "loja-exemplo");
        for (var n = 2; Directory.Exists(target); n++)
            target = Path.Combine(parent, $"loja-exemplo-{n}");
        Run("Criando repositório de exemplo", () => Services.CreateSample(target, Settings), root =>
            Activate(root, "Repositório de exemplo criado (dados fictícios, marcados como [EXEMPLO]). Selecione a proposta " +
                           "e clique em Aplicar para ver o ciclo completo."));
    }

    [RelayCommand(CanExecute = nameof(CanActOnRepo))]
    private async Task ClearSamplesAsync()
    {
        if (!await Dialogs.ConfirmAsync("Limpar registros de exemplo",
                "Apagar do banco deste repositório todas as propostas, testes e eventos marcados como [EXEMPLO]? " +
                "Os arquivos e o histórico do git não são alterados.", "Apagar registros", danger: true))
            return;
        var repo = Repo!;
        Run("Limpando registros de exemplo", () => Services.ClearSamples(repo, Settings), counts => Refresh(() =>
            Notify("ok", $"Registros de exemplo apagados: {counts["proposals"]} propostas, {counts["test_runs"]} testes, " +
                         $"{counts["events"]} eventos.")));
    }

    private bool CanRemoveSample => CanActOnRepo && Snapshot?.IsSample == true;

    [RelayCommand(CanExecute = nameof(CanRemoveSample))]
    private async Task RemoveSampleAsync()
    {
        var root = Repo!;
        if (!await Dialogs.ConfirmAsync("Apagar repositório de exemplo",
                $"Apagar a pasta {root} inteira? Ela é o repositório de exemplo (tem o marcador {Sample.MarkerFile}). " +
                "Isto não pode ser desfeito.", "Apagar pasta", danger: true))
            return;
        CloseRepo();
        Run("Apagando repositório de exemplo", () => Services.RemoveSample(root), () =>
        {
            try
            {
                SetRecent(RecentRepos.Forget(root, _recentFile));
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
            }
            Notify("ok", $"Repositório de exemplo apagado: {root}");
        });
    }

    // ================================================================== atualizações
    /// <summary>Chamado ao abrir a janela: pergunta (uma vez) ou verifica atualizações, conforme a preferência.</summary>
    public void StartupUpdateCheck()
    {
        // Rodando pelo código-fonte ou sem instalar: não há o que atualizar, então nem pergunta.
        if (Updater is null || !Updater.IsInstalled || Settings.AutoUpdate == AutoUpdateMode.Off)
            return;
        var prefs = Preferences.Load(_preferencesFile);
        var enabled = Settings.AutoUpdate == AutoUpdateMode.On || prefs.AutoUpdate == true;
        if (enabled)
            CheckForUpdates(silent: true);
        else if (prefs.AutoUpdate is null)
            AskAutoUpdate = true;
    }

    public string AutoUpdateQuestion => Updater is null
        ? ""
        : "Verificar atualizações automaticamente ao abrir? O Gitruvian consulta " + Updater.SourceDescription +
          ". Como em qualquer acesso a um site, o servidor vê seu endereço IP; nenhum dado dos seus repositórios é enviado.";

    [RelayCommand]
    private void AnswerAutoUpdate(string? answer)
    {
        var enable = answer == "sim";
        AskAutoUpdate = false;
        try
        {
            new Preferences { AutoUpdate = enable }.Save(_preferencesFile);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Fail(exc);
        }
        if (enable) CheckForUpdates(silent: true);
        else Notify("info", "Atualização automática desligada. Você pode verificar quando quiser em Ajuda › Verificar atualizações.");
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void CheckUpdates() => CheckForUpdates(silent: false);

    private void CheckForUpdates(bool silent)
    {
        var updater = Updater;
        if (updater is null) return;
        Run("Verificando atualizações", () =>
        {
            var check = updater.Check();
            return check.State == UpdateState.Available ? updater.Download() : check;
        }, check =>
        {
            UpdateMessage = check.Message;
            UpdateReady = check.State == UpdateState.Downloaded;
            if (!silent || UpdateReady)
                Notify(UpdateReady ? "ok" : "info", check.Message,
                    UpdateReady ? "Clique em \"Reiniciar e atualizar\" ou feche o app: a atualização é aplicada ao sair." : "");
        }, quiet: silent, onError: exc =>
        {
            if (silent) return; // verificação automática: sem rede não é erro para o usuário
            var (message, _) = ErrorText.Friendly(exc);
            Notify("error", $"Não foi possível verificar atualizações: {message}",
                "Confira a conexão. Nenhuma versão foi alterada.");
        });
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        try
        {
            Updater?.ApplyAndRestart();
        }
        catch (Exception exc)
        {
            Fail(exc);
        }
    }

    /// <summary>Ao fechar: se há atualização baixada, ela é aplicada depois que o app sair.</summary>
    public void OnClosing()
    {
        if (UpdateReady)
        {
            try
            {
                Updater?.ApplyOnExit();
            }
            catch (Exception exc)
            {
                Log.Warning($"update_apply_on_exit_failed type={exc.GetType().Name}");
            }
        }
    }

    // ================================================================== ajuda
    [RelayCommand]
    private Task ShowModelHelpAsync() => Dialogs.ShowInfoAsync("Modelo e modo manual",
        $"Modelo atual: {Snapshot?.ProviderLabel ?? Llm.Describe(Settings).Label}\n\n{Llm.DegradedExplanation}\n\n" +
        "Para usar um modelo, edite o arquivo .env (veja .env.example):\n" +
        "• GITRUVIAN_PROVIDER=openai_compat — servidor local (Ollama, LM Studio)\n" +
        "• GITRUVIAN_PROVIDER=command — qualquer programa local que leia o prompt no stdin\n" +
        "• GITRUVIAN_PROVIDER=anthropic — API da Anthropic; exige GITRUVIAN_ALLOW_REMOTE=1, pois trechos do código saem da máquina\n\n" +
        $"Onde fica o .env: {Path.Combine(Config.UserConfigDir(), ".env")}\n" +
        "Arquivos .env lidos: " + (Settings.EnvFiles.Count > 0 ? string.Join(", ", Settings.EnvFiles) : "nenhum") +
        "\nReinicie o Gitruvian depois de alterar o .env.");

    [RelayCommand]
    private Task ShowShortcutsAsync() => Dialogs.ShowInfoAsync("Atalhos de teclado", ShortcutsHelp);

    [RelayCommand]
    private Task ShowAboutAsync() => Dialogs.ShowInfoAsync("Sobre o Gitruvian",
        $"Gitruvian {Version}\n\nCliente Git local com assistente. Sem telemetria, sem contas, sem anúncios. " +
        "Os dados de cada repositório ficam em .gitruvian/ dentro dele; as preferências do app ficam em " +
        $"{Config.UserConfigDir()}.\n\nA única conexão que o app faz por conta própria é a verificação de " +
        "atualizações, e só se você ativar. Chamadas a modelos acontecem apenas quando você pede uma proposta.");
}
