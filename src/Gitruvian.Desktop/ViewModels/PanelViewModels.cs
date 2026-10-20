using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gitruvian.Core;

namespace Gitruvian.Desktop.ViewModels;

/// <summary>Aba Alterações: arquivos não preparados e preparados, stage/descartar e commit manual.</summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private bool _loading;

    public ChangesViewModel(MainViewModel main)
    {
        this.main = main;
        SelectedUnstaged.CollectionChanged += (_, _) => OnSelectionChanged();
        SelectedStaged.CollectionChanged += (_, _) => OnSelectionChanged();
    }

    public ObservableCollection<FileItem> Unstaged { get; } = [];
    public ObservableCollection<FileItem> Staged { get; } = [];
    public ObservableCollection<FileItem> SelectedUnstaged { get; } = [];
    public ObservableCollection<FileItem> SelectedStaged { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitHint))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasChanges))]
    private int _total;

    public bool IsEmpty => Total == 0;
    public bool HasChanges => Total > 0;

    public string CommitHint => Staged.Count == 0
        ? "Prepare (stage) ao menos um arquivo para commitar."
        : Message.Trim().Length == 0 ? "Escreva a mensagem do commit." : $"{Staged.Count} arquivo(s) preparado(s).";

    public void Clear()
    {
        Unstaged.Clear();
        Staged.Clear();
        Total = 0;
    }

    public void Load(Snapshot snap)
    {
        var keepUnstaged = SelectedUnstaged.Select(f => f.Path).ToHashSet();
        var keepStaged = SelectedStaged.Select(f => f.Path).ToHashSet();
        _loading = true;
        Unstaged.Clear();
        Staged.Clear();
        foreach (var entry in snap.Status)
        {
            if (entry.HasUnstaged) Unstaged.Add(new FileItem(entry, false));
            if (entry.IsStaged) Staged.Add(new FileItem(entry, true));
        }
        Total = snap.Status.Count;
        SelectedUnstaged.Clear();
        foreach (var f in Unstaged.Where(f => keepUnstaged.Contains(f.Path))) SelectedUnstaged.Add(f);
        SelectedStaged.Clear();
        foreach (var f in Staged.Where(f => keepStaged.Contains(f.Path))) SelectedStaged.Add(f);
        _loading = false;
        RefreshState();
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(CommitHint));
        StageCommand.NotifyCanExecuteChanged();
        StageAllCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        UnstageCommand.NotifyCanExecuteChanged();
        UnstageAllCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Mostra no painel de diff o arquivo selecionado (ou um texto de orientação).</summary>
    public void ShowSelectionDiff()
    {
        var item = SelectedUnstaged.FirstOrDefault() ?? SelectedStaged.FirstOrDefault();
        var repo = main.Repo;
        if (item is null || repo is null)
        {
            main.Diff.ShowPlaceholder(IsEmpty
                ? "Nenhuma alteração local: a árvore de trabalho está igual ao último commit."
                : "Selecione um arquivo na aba Alterações para ver o diff.");
            return;
        }
        var label = item.StagedSide ? "preparado" : "não preparado";
        main.Run("Lendo diff", () => Services.FileDiff(repo, item.Path, item.StagedSide, item.Entry.IsUntracked),
            text => main.Diff.ShowDiff($"{item.Path} ({label})", text), quiet: true);
    }

    private void OnSelectionChanged()
    {
        if (_loading) return;
        RefreshState();
        ShowSelectionDiff();
    }

    private bool Idle => !main.Runner.Busy && main.Repo is not null;
    private bool CanStage => Idle && SelectedUnstaged.Count > 0;
    private bool CanStageAll => Idle && Unstaged.Count > 0;
    private bool CanUnstage => Idle && SelectedStaged.Count > 0;
    private bool CanUnstageAll => Idle && Staged.Count > 0;
    private bool CanCommit => Idle && Staged.Count > 0 && Message.Trim().Length > 0;

    private void GitAction(string label, Action<string> action, string done)
    {
        var repo = main.Repo!;
        main.Run(label, () => action(repo), () => main.Refresh(() =>
        {
            main.Notify("ok", done);
            ShowSelectionDiff();
        }));
    }

    [RelayCommand(CanExecute = nameof(CanStage))]
    private void Stage()
    {
        var paths = SelectedUnstaged.Select(f => f.Path).ToList();
        GitAction("Preparando", repo => Services.Stage(repo, paths), $"{paths.Count} arquivo(s) preparado(s).");
    }

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    private void StageAll()
    {
        var paths = Unstaged.Select(f => f.Path).ToList();
        GitAction("Preparando", repo => Services.Stage(repo, paths), $"{paths.Count} arquivo(s) preparado(s).");
    }

    [RelayCommand(CanExecute = nameof(CanUnstage))]
    private void Unstage()
    {
        var paths = SelectedStaged.Select(f => f.Path).ToList();
        GitAction("Removendo do stage", repo => Services.Unstage(repo, paths), $"{paths.Count} arquivo(s) fora do stage.");
    }

    [RelayCommand(CanExecute = nameof(CanUnstageAll))]
    private void UnstageAll()
    {
        var paths = Staged.Select(f => f.Path).ToList();
        GitAction("Removendo do stage", repo => Services.Unstage(repo, paths), $"{paths.Count} arquivo(s) fora do stage.");
    }

    [RelayCommand(CanExecute = nameof(CanStage))]
    private async Task DiscardAsync()
    {
        var items = SelectedUnstaged.ToList();
        var untracked = items.Where(f => f.Entry.IsUntracked).Select(f => f.Path).ToList();
        var tracked = items.Where(f => !f.Entry.IsUntracked).Select(f => f.Path).ToList();
        var text = "Descartar as alterações não preparadas destes arquivos? Isto não pode ser desfeito.\n\n" +
                   string.Join("\n", items.Select(f => (f.Entry.IsUntracked ? "(será apagado) " : "") + f.Path));
        if (!await main.Dialogs.ConfirmAsync("Descartar alterações", text, "Descartar", danger: true))
            return;
        GitAction("Descartando", repo =>
        {
            if (tracked.Count > 0) Services.Discard(repo, tracked, untracked: false);
            if (untracked.Count > 0) Services.Discard(repo, untracked, untracked: true);
        }, $"Alterações descartadas em {items.Count} arquivo(s).");
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        var repo = main.Repo!;
        var message = Message;
        main.Run("Commitando", () => Services.CommitStaged(repo, main.Settings, message), sha =>
        {
            Message = "";
            main.Refresh(() => main.Notify("ok", $"Commit {sha[..10]} criado."));
        });
    }
}

/// <summary>Aba Commit: detalhes do commit selecionado no grafo e seus arquivos.</summary>
public sealed partial class CommitViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<CommitFileItem> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommit))]
    private CommitDetail? _detail;

    [ObservableProperty]
    private CommitFileItem? _selectedFile;

    public bool HasCommit => Detail is not null;
    public string Header => Detail is null ? "" : $"{Detail.Sha[..10]} · {Detail.Author} <{Detail.Email}> · {GraphItem.FormatTimestamp(Detail.Timestamp)}";
    public string Parents => Detail is null ? "" : Detail.Parents.Count == 0 ? "Commit raiz" : "Pais: " + string.Join(", ", Detail.Parents.Select(p => p[..10]));
    public string Message => Detail?.Message ?? "";

    public void Clear()
    {
        Detail = null;
        Files.Clear();
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Parents));
        OnPropertyChanged(nameof(Message));
    }

    public void LoadCommit(string sha)
    {
        var repo = main.Repo;
        if (repo is null) return;
        main.Run("Lendo commit", () => (Detail: Git.CommitDetail(repo, sha), Diff: Services.CommitDiff(repo, sha)), result =>
        {
            Detail = result.Detail;
            Files.Clear();
            foreach (var f in result.Detail.Files) Files.Add(new CommitFileItem(f.Status, f.Path));
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(Parents));
            OnPropertyChanged(nameof(Message));
            main.Diff.ShowDiff($"Commit {sha[..10]}", result.Diff);
        }, quiet: true);
    }

    partial void OnSelectedFileChanged(CommitFileItem? value)
    {
        var repo = main.Repo;
        var detail = Detail;
        if (repo is null || detail is null) return;
        main.Run("Lendo diff", () => Services.CommitDiff(repo, detail.Sha, value?.Path), text =>
            main.Diff.ShowDiff(value is null ? $"Commit {detail.Sha[..10]}" : $"{value.Path} @ {detail.Sha[..10]}", text),
            quiet: true);
    }
}

/// <summary>Aba Histórico: trilha de auditoria (cada alteração de estado registrada no banco).</summary>
public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<EventItem> Events { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    [ObservableProperty]
    private EventItem? _selectedEvent;

    public bool IsEmpty => Count == 0;

    public void Clear()
    {
        Events.Clear();
        Count = 0;
    }

    public void Load(Snapshot snap)
    {
        Events.Clear();
        foreach (var e in snap.Events) Events.Add(new EventItem(e));
        Count = Events.Count;
    }

    /// <summary>Abre a proposta ligada ao evento selecionado na aba Assistente.</summary>
    [RelayCommand]
    private void OpenEvent(EventItem? item)
    {
        item ??= SelectedEvent;
        if (item?.Event.ProposalId is not { } id) return;
        main.SelectedTab = MainViewModel.TabAssistant;
        main.Assistant.Select(id);
    }
}
