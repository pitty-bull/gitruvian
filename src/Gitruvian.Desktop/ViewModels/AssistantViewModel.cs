using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gitruvian.Core;

namespace Gitruvian.Desktop.ViewModels;

/// <summary>Aba Assistente: pedir um diff (ao modelo ou manual), revisar, aplicar, testar, commitar ou reverter.</summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    public const int MaxPatchFile = 400_000;

    public const string GuiDegradedExplanation =
        "Nenhum modelo configurado, então nada sai desta máquina. O Gitruvian continua útil: cole ou carregue " +
        "um diff, revise, aplique, rode os testes e commite, com tudo registrado. Para gerar diffs " +
        "automaticamente, configure um modelo no .env (Ajuda › Modelo e modo manual).";

    private readonly MainViewModel _main;
    private bool _manualTouched;

    public AssistantViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<ProposalItem> Proposals { get; } = [];

    [ObservableProperty]
    private ProposalItem? _selectedProposal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent), nameof(DetailText), nameof(Warning), nameof(HasWarning),
        nameof(ShowUntested), nameof(CanEditCommitMessage), nameof(CanEditBranch))]
    private Proposal? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Validation))]
    private string _instruction = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Validation))]
    private string _patch = "";

    [ObservableProperty]
    private string _files = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Validation))]
    private bool _manual;

    [ObservableProperty]
    private string _branch = "";

    [ObservableProperty]
    private string _commitMessage = "";

    [ObservableProperty]
    private bool _untested;

    [ObservableProperty]
    private string _testOutput = "";

    [ObservableProperty]
    private string _providerLabel = "";

    [ObservableProperty]
    private string _providerExplanation = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Validation))]
    private bool _providerReady;

    [ObservableProperty]
    private bool _hasTestCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _count;

    public bool IsEmpty => Count == 0;
    public bool HasCurrent => Current is not null;
    public Status? CurrentStatus => Current?.Status;
    public bool ShowUntested => CurrentStatus == Status.Applied && HasTestCommand;
    public bool CanEditCommitMessage => CurrentStatus is Status.Applied or Status.TestsPassed or Status.Proposed or Status.Reverted;
    public bool CanEditBranch => CurrentStatus is Status.Proposed or Status.Reverted;

    public string Warning => Current is { CheckError: { } err, Status: Status.Proposed }
        ? "AVISO: o diff não aplica no estado atual: " + Safety.Truncate(err, 300)
        : "";

    public bool HasWarning => Warning.Length > 0;

    public string DetailText
    {
        get
        {
            if (Current is not { } p)
                return "Selecione uma proposta na lista para revisar, aplicar, testar e commitar.";
            var lines = new List<string>
            {
                $"{p.ShortId} · {p.StatusLabel}{(p.IsSample ? " · [EXEMPLO]" : "")}",
                $"Pedido: {p.Instruction}",
                $"Arquivos: {string.Join(", ", p.Files)}",
                $"Origem: {p.Provider} · {p.Model}{(p.LatencyMs is { } ms ? $" · {ms} ms" : "")} · criada em {GraphItem.FormatIso(p.CreatedAt)}",
            };
            if (p.CommitSha is not null) lines.Add($"Commit: {p.CommitSha[..10]}");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Mensagem de validação do formulário de pedido ("" = pode propor).</summary>
    public string Validation
    {
        get
        {
            var text = Instruction.Trim();
            var typed = text.Length > 0 || Patch.Trim().Length > 0;
            if (!typed) return "";
            if (text.Length < 3) return "Escreva o pedido (mínimo 3 caracteres).";
            if (text.Length > Safety.MaxInstructionChars) return $"Pedido longo demais (máximo {Safety.MaxInstructionChars} caracteres).";
            if (Manual && Patch.Trim().Length == 0) return "Cole um diff ou carregue um arquivo .patch.";
            if (!Manual && !ProviderReady) return "Nenhum modelo configurado: use o diff manual.";
            return "";
        }
    }

    public bool InstructionInvalid => Instruction.Trim().Length is > 0 and < 3;

    partial void OnInstructionChanged(string value)
    {
        OnPropertyChanged(nameof(InstructionInvalid));
        ProposeCommand.NotifyCanExecuteChanged();
    }

    partial void OnPatchChanged(string value) => ProposeCommand.NotifyCanExecuteChanged();

    partial void OnManualChanged(bool value)
    {
        _manualTouched = true;
        ProposeCommand.NotifyCanExecuteChanged();
    }

    partial void OnCommitMessageChanged(string value) => AcceptCommand.NotifyCanExecuteChanged();

    partial void OnUntestedChanged(bool value) => AcceptCommand.NotifyCanExecuteChanged();

    partial void OnSelectedProposalChanged(ProposalItem? value)
    {
        if (value is null) return;
        LoadDetail(value.Proposal.Id, showDiff: true);
    }

    public void Clear()
    {
        Proposals.Clear();
        Count = 0;
        ShowDetail(null, null);
    }

    public void Load(Snapshot snap)
    {
        (ProviderLabel, ProviderExplanation, ProviderReady) = (snap.ProviderLabel,
            snap.ProviderReady ? snap.ProviderExplanation : GuiDegradedExplanation, snap.ProviderReady);
        if (!ProviderReady && !_manualTouched)
        {
            Manual = true; // sem modelo, o modo manual é o único caminho
            _manualTouched = false;
        }
        HasTestCommand = !string.IsNullOrWhiteSpace(snap.TestCommand);
        var currentId = Current?.Id;
        Proposals.Clear();
        foreach (var p in snap.Proposals) Proposals.Add(new ProposalItem(p));
        Count = Proposals.Count;
        if (currentId is not null && Proposals.FirstOrDefault(p => p.Proposal.Id == currentId) is { } again)
        {
            // Mantém a seleção sem recarregar o diff (o usuário pode estar olhando outro arquivo).
#pragma warning disable MVVMTK0034 // de propósito: muda a seleção sem disparar OnSelectedProposalChanged
            SetProperty(ref _selectedProposal, again, nameof(SelectedProposal));
#pragma warning restore MVVMTK0034
            LoadDetail(currentId, showDiff: false);
        }
        else if (currentId is not null)
        {
            ShowDetail(null, null);
        }
        RefreshState();
    }

    /// <summary>Seleciona a proposta pelo id (ex.: vindo do Histórico).</summary>
    public void Select(string proposalId)
    {
        var item = Proposals.FirstOrDefault(p => p.Proposal.Id == proposalId);
        if (item is not null) SelectedProposal = item;
        else LoadDetail(proposalId, showDiff: true);
    }

    private void LoadDetail(string id, bool showDiff)
    {
        var repo = _main.Repo;
        if (repo is null) return;
        _main.Run("Lendo proposta", () => Services.GetProposalDetail(repo, _main.Settings, id), detail =>
        {
            ShowDetail(detail.Proposal, detail.TestRuns);
            if (showDiff)
                _main.Diff.ShowDiff($"Proposta {detail.Proposal.ShortId}", detail.Proposal.Diff);
        }, quiet: true);
    }

    private void ShowDetail(Proposal? proposal, List<TestRun>? runs)
    {
        var previous = Current?.Id;
        Current = proposal;
        if (proposal is null)
        {
            CommitMessage = "";
            TestOutput = "";
        }
        else
        {
            if (previous != proposal.Id || CommitMessage.Trim().Length == 0)
                CommitMessage = proposal.CommitMessage;
            if (previous != proposal.Id)
                Untested = false;
            TestOutput = runs is { Count: > 0 } ? FormatRun(runs[0]) : "";
        }
        RefreshState();
    }

    public static string FormatRun(TestRun run)
    {
        var verdict = run.Passed ? "PASSOU" : $"FALHOU (código {run.ExitCode})";
        return $"$ {run.Command}\n{verdict} em {run.DurationMs} ms · {GraphItem.FormatIso(run.StartedAt)}\n\n{run.OutputTail}";
    }

    public void RefreshState()
    {
        OnPropertyChanged(nameof(ShowUntested));
        OnPropertyChanged(nameof(CanEditCommitMessage));
        OnPropertyChanged(nameof(CanEditBranch));
        ProposeCommand.NotifyCanExecuteChanged();
        LoadPatchCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        AcceptCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    // ================================================================== estado dos botões
    private bool Idle => !_main.Runner.Busy && _main.Repo is not null;
    private bool CanPropose => Idle && Instruction.Trim().Length > 0 && Validation.Length == 0;
    private bool CanApply => Idle && CurrentStatus is Status.Proposed or Status.Reverted;
    private bool CanTest => Idle && HasTestCommand && CurrentStatus is Status.Applied or Status.TestsPassed or Status.TestsFailed;

    private bool CanAccept => Idle && CommitMessage.Trim().Length > 0 &&
                              (CurrentStatus == Status.TestsPassed ||
                               (CurrentStatus == Status.Applied && (!HasTestCommand || Untested)));

    private bool CanRevert => Idle && CurrentStatus is Status.Applied or Status.TestsPassed or Status.TestsFailed or Status.Committed;
    private bool CanReject => Idle && CurrentStatus is Status.Proposed or Status.Reverted;

    // ================================================================== ações
    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task LoadPatchAsync()
    {
        var path = await _main.Dialogs.AskOpenFileAsync("Carregar diff", "Diff/patch", ["*.patch", "*.diff", "*.txt"]);
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxPatchFile)
                throw new ArgumentException($"arquivo maior que {MaxPatchFile / 1000} KB");
            Patch = File.ReadAllText(path);
            Manual = true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _main.Fail(exc);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPropose))]
    private void Propose()
    {
        var repo = _main.Repo!;
        var instruction = Instruction;
        var patch = Manual ? Patch : null;
        List<string> files;
        try
        {
            files = Services.ParseContextFiles(Files);
        }
        catch (UnsafePathException exc)
        {
            _main.Fail(exc);
            return;
        }
        _main.Run(patch is null ? "Consultando o modelo" : "Validando o diff",
            () => Services.Propose(repo, _main.Settings, instruction, patch, files), outcome =>
            {
                Instruction = "";
                Patch = "";
                Files = "";
                _main.SelectedTab = MainViewModel.TabAssistant;
                _main.Refresh(() =>
                {
                    Select(outcome.Proposal.Id);
                    if (outcome.Applies)
                        _main.Notify("ok", $"Proposta {outcome.Proposal.ShortId} criada. Revise o diff e clique em Aplicar.");
                    else
                        _main.Notify("warn", $"Proposta {outcome.Proposal.ShortId} criada, mas o diff não aplica no estado atual.",
                            "Revise o aviso na proposta; talvez o arquivo tenha mudado desde o pedido.");
                });
            });
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        var repo = _main.Repo!;
        var id = Current!.Id;
        var branch = Branch;
        _main.Run("Aplicando", () => Services.ApplyProposal(repo, _main.Settings, id, branch), p =>
        {
            Branch = "";
            _main.Refresh(() =>
            {
                LoadDetail(p.Id, showDiff: false);
                if (HasTestCommand)
                    RunTests(p.Id, $"Diff aplicado no branch {p.Branch}. ");
                else
                    _main.Notify("ok", $"Diff aplicado no branch {p.Branch}.",
                        "Sem comando de testes: commite marcando a proposta, ou defina um em Repositório › Configurações.");
            });
        });
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private void Test() => RunTests(Current!.Id, "");

    private void RunTests(string id, string prefix)
    {
        var repo = _main.Repo!;
        _main.Run("Rodando os testes", () => Services.RunTests(repo, _main.Settings, id), result =>
        {
            TestOutput = FormatRun(result.Run);
            _main.Refresh(() =>
            {
                LoadDetail(id, showDiff: false);
                if (result.Run.Passed)
                    _main.Notify("ok", $"{prefix}Testes passaram em {result.Run.DurationMs} ms. Pode commitar.");
                else
                    _main.Notify("error", $"{prefix}Testes falharam (código {result.Run.ExitCode}).",
                        "Veja a saída dos testes abaixo. Reverta a proposta ou corrija e rode de novo.");
            });
        });
    }

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        var repo = _main.Repo!;
        var id = Current!.Id;
        var message = CommitMessage;
        var untested = Untested;
        _main.Run("Commitando", () => Services.AcceptProposal(repo, _main.Settings, id, message, untested), p =>
            _main.Refresh(() =>
            {
                LoadDetail(p.Id, showDiff: false);
                _main.Notify("ok", $"Commit {p.CommitSha![..10]} criado a partir da proposta {p.ShortId}.");
            }));
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        var p = Current!;
        var text = p.Status == Status.Committed
            ? $"Criar um commit que desfaz {p.CommitSha![..10]} (git revert)? O histórico é preservado."
            : "Desfazer o diff desta proposta na árvore de trabalho?";
        if (!await _main.Dialogs.ConfirmAsync("Reverter proposta", text, "Reverter"))
            return;
        var repo = _main.Repo!;
        _main.Run("Revertendo", () => Services.RevertProposal(repo, _main.Settings, p.Id), reverted =>
            _main.Refresh(() =>
            {
                LoadDetail(reverted.Id, showDiff: false);
                _main.Notify("ok", $"Proposta {reverted.ShortId} revertida.");
            }));
    }

    [RelayCommand(CanExecute = nameof(CanReject))]
    private void Reject()
    {
        var repo = _main.Repo!;
        var id = Current!.Id;
        _main.Run("Rejeitando", () => Services.RejectProposal(repo, _main.Settings, id), p =>
            _main.Refresh(() =>
            {
                LoadDetail(p.Id, showDiff: false);
                _main.Notify("info", $"Proposta {p.ShortId} rejeitada (continua registrada no histórico).");
            }));
    }
}
