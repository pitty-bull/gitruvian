using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Gitruvian.Core;
using Gitruvian.Desktop.Infrastructure;

namespace Gitruvian.Desktop.ViewModels;

/// <summary>Mensagem no topo da tela (info, ok, aviso ou erro), com dica de como resolver.</summary>
public sealed partial class BannerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Icon), nameof(Accent), nameof(Tint), nameof(AccessibleText))]
    private string _kind = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(AccessibleText))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint), nameof(AccessibleText))]
    private string _hint = "";

    public bool IsVisible => Text.Length > 0;
    public bool HasHint => Hint.Length > 0;

    /// <summary>Prefixo textual: o tipo da mensagem não depende só da cor.</summary>
    public string Icon => Kind switch
    {
        "ok" => "✔ Sucesso:",
        "error" => "✖ Erro:",
        "warn" => "⚠ Atenção:",
        _ => "ℹ",
    };

    public IBrush Accent => new SolidColorBrush(Kind switch
    {
        "ok" => Palette.Ok,
        "error" => Palette.Error,
        "warn" => Palette.Warning,
        _ => Palette.Info,
    });

    public IBrush Tint => new SolidColorBrush(Kind switch
    {
        "ok" => Color.Parse("#1f3322"),
        "error" => Color.Parse("#3a2224"),
        "warn" => Color.Parse("#3a3220"),
        _ => Color.Parse("#22303f"),
    });

    public string AccessibleText => $"{Icon} {Text} {Hint}".Trim();

    public void Show(string kind, string text, string hint = "")
    {
        Kind = kind;
        Text = text;
        Hint = hint;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void Clear() => Show("", "", "");
}

public sealed record RefBadge(string Text, string Kind)
{
    public IBrush Background => new SolidColorBrush(Kind switch
    {
        "head" => Color.Parse("#2f5d3a"),
        "tag" => Color.Parse("#5a4a1e"),
        "remote" => Color.Parse("#3b3f5c"),
        _ => Color.Parse("#2b4766"),
    });

    /// <summary>O tipo da referência aparece no texto, não só na cor.</summary>
    public string Display => Kind switch
    {
        "tag" => "tag " + Text,
        "head" => "✔ " + Text,
        _ => Text,
    };
}

/// <summary>Linha do grafo: um commit, ou a linha de alterações locais no topo.</summary>
public sealed class GraphItem
{
    public const string WorkingSha = "__working__";

    public required string Sha { get; init; }
    public required string Subject { get; init; }
    public string Author { get; init; } = "";
    public string Date { get; init; } = "";
    public List<RefBadge> Refs { get; init; } = [];
    public GraphRow? Row { get; init; }
    public bool IsWorking { get; init; }
    public bool IsHead { get; init; }
    public int Lanes { get; init; } = 1;

    /// <summary>A linha de baixo é o HEAD (a linha das alterações locais se liga a ela).</summary>
    public bool ConnectsToHead { get; init; }

    public int HeadColumn { get; init; }
    public string ShortSha => IsWorking ? "" : Sha[..Math.Min(8, Sha.Length)];

    public string AccessibleText => IsWorking
        ? Subject
        : $"Commit {ShortSha}: {Subject}. Autor {Author}, {Date}." +
          (Refs.Count > 0 ? " Referências: " + string.Join(", ", Refs.Select(r => r.Text)) + "." : "");

    public static string FormatTimestamp(long seconds) =>
        seconds <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string FormatIso(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            : iso ?? "";

    public static List<GraphItem> Build(Snapshot snap)
    {
        var items = new List<GraphItem>();
        var lanes = Math.Max(1, snap.Rows.Count == 0 ? 1 : snap.Rows.Max(r => r.Width));
        var changes = snap.Status.Count;
        var headIndex = snap.Head is null ? -1 : snap.Commits.FindIndex(c => c.Sha == snap.Head);
        if (changes > 0 || snap.Commits.Count == 0)
        {
            items.Add(new GraphItem
            {
                Sha = WorkingSha,
                Subject = changes > 0
                    ? $"Alterações locais ({changes} arquivo{(changes == 1 ? "" : "s")})"
                    : "Sem commits ainda: prepare arquivos na aba Alterações",
                IsWorking = true,
                Lanes = lanes,
                ConnectsToHead = headIndex == 0,
                HeadColumn = headIndex == 0 ? snap.Rows[0].Column : 0,
            });
        }
        for (var i = 0; i < snap.Commits.Count; i++)
        {
            var commit = snap.Commits[i];
            var refs = commit.Refs.Where(r => r != "HEAD").Select(r => r.StartsWith("tag:")
                ? new RefBadge(r[4..], "tag")
                : r.Contains('/') && !snap.Branches.Any(b => b.Name == r)
                    ? new RefBadge(r, "remote")
                    : new RefBadge(r, r == snap.Branch ? "head" : "branch")).ToList();
            items.Add(new GraphItem
            {
                Sha = commit.Sha,
                Subject = commit.Subject,
                Author = commit.Author,
                Date = FormatTimestamp(commit.Timestamp),
                Refs = refs,
                Row = snap.Rows[i],
                IsHead = commit.Sha == snap.Head,
                Lanes = lanes,
                ConnectsToHead = i == 0 && changes > 0 && commit.Sha == snap.Head,
            });
        }
        return items;
    }
}

public sealed record BranchItem(string Name, bool IsCurrent)
{
    public string Display => IsCurrent ? $"● {Name} (atual)" : Name;
}

public sealed record FileItem(StatusEntry Entry, bool StagedSide)
{
    public string Path => Entry.Path;
    public string Code => StagedSide ? Entry.Code[..1] : (Entry.IsUntracked ? "?" : Entry.Code[1..]);
    public string Display => $"{Code}  {Path}";
    public string AccessibleText => $"{Path}: {Entry.Label}";
    public IBrush Brush => new SolidColorBrush(Code switch
    {
        "M" => Palette.Warning,
        "A" or "?" => Palette.Ok,
        "D" => Palette.Error,
        _ => Palette.Info,
    });
}

public sealed record CommitFileItem(string Status, string Path)
{
    public string Display => $"{Status}  {Path}";
    public string AccessibleText => $"{Path}: " + Status switch
    {
        "A" => "adicionado",
        "M" => "modificado",
        "D" => "removido",
        "R" => "renomeado",
        "C" => "copiado",
        _ => Status,
    };
}

public sealed record ProposalItem(Proposal Proposal)
{
    public string Display =>
        $"{(Proposal.IsSample && !Proposal.Instruction.StartsWith("[EXEMPLO]") ? "[EXEMPLO] " : "")}{Proposal.ShortId} · {Proposal.StatusLabel} · {Safety.Truncate(Proposal.Instruction.Split('\n')[0], 70)}";

    public IBrush StatusBrush =>
        new SolidColorBrush(Palette.StatusColors.GetValueOrDefault(Proposal.Status.Value(), Palette.Text));
}

public sealed record EventItem(Event Event)
{
    public string When => GraphItem.FormatIso(Event.Ts);
    public string Kind => Event.Kind;
    public string Summary => (Event.IsSample ? "[EXEMPLO] " : "") + Event.Summary;
    public string Proposal => Event.ProposalId?[..8] ?? "";
    public string AccessibleText => $"{When}, {Kind}: {Summary}";
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    Header,
    Notice,
}

public sealed record DiffLine(string Text, DiffLineKind Kind)
{
    private static readonly IBrush AddedBg = new SolidColorBrush(Color.Parse("#1d3324"));
    private static readonly IBrush RemovedBg = new SolidColorBrush(Color.Parse("#3a2023"));
    private static readonly IBrush HunkBg = new SolidColorBrush(Color.Parse("#22303f"));

    public IBrush Foreground => new SolidColorBrush(Kind switch
    {
        DiffLineKind.Added => Color.Parse("#a6e3a1"),
        DiffLineKind.Removed => Color.Parse("#ffb3b3"),
        DiffLineKind.Hunk => Palette.Info,
        DiffLineKind.Header => Palette.Muted,
        DiffLineKind.Notice => Palette.Warning,
        _ => Palette.Text,
    });

    public IBrush Background => Kind switch
    {
        DiffLineKind.Added => AddedBg,
        DiffLineKind.Removed => RemovedBg,
        DiffLineKind.Hunk => HunkBg,
        _ => Brushes.Transparent,
    };

    /// <summary>Texto lido por leitores de tela: o tipo da linha não depende só da cor.</summary>
    public string AccessibleText => Kind switch
    {
        DiffLineKind.Added => "adicionada: " + Text[1..],
        DiffLineKind.Removed => "removida: " + Text[1..],
        _ => Text,
    };

    public static DiffLineKind Classify(string line) => line switch
    {
        _ when line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff --git") ||
               line.StartsWith("index ") || line.StartsWith("new file") || line.StartsWith("deleted file") ||
               line.StartsWith("novo arquivo:") || line.StartsWith("rename ") || line.StartsWith("similarity ")
            => DiffLineKind.Header,
        _ when line.StartsWith("@@") => DiffLineKind.Hunk,
        _ when line.StartsWith('+') => DiffLineKind.Added,
        _ when line.StartsWith('-') => DiffLineKind.Removed,
        _ when line.StartsWith("(conteúdo oculto") || line.StartsWith("(arquivo binário") => DiffLineKind.Notice,
        _ => DiffLineKind.Context,
    };
}
