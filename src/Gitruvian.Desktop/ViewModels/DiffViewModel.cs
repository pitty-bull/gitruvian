using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gitruvian.Desktop.ViewModels;

/// <summary>Painel de diff: linhas classificadas (adição, remoção, bloco) ou um texto de estado vazio.</summary>
public sealed partial class DiffViewModel : ObservableObject
{
    public const int MaxLines = 20_000;

    [ObservableProperty]
    private string _title = "Diff";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaceholder))]
    private string _placeholder = "Selecione um commit, um arquivo ou uma proposta para ver as alterações.";

    public ObservableCollection<DiffLine> Lines { get; } = [];

    public bool HasPlaceholder => Placeholder.Length > 0;

    /// <summary>Texto completo do diff exibido (para copiar e para os testes).</summary>
    public string Text { get; private set; } = "";

    public void ShowDiff(string title, string text)
    {
        Title = title;
        Text = text.Replace("\r\n", "\n");
        Lines.Clear();
        if (Text.Trim().Length == 0)
        {
            Placeholder = "Sem diferenças para mostrar.";
            return;
        }
        var lines = Text.TrimEnd('\n').Split('\n');
        foreach (var line in lines.Take(MaxLines))
            Lines.Add(new DiffLine(line, DiffLine.Classify(line)));
        if (lines.Length > MaxLines)
            Lines.Add(new DiffLine($"(diff cortado: mostrando {MaxLines} de {lines.Length} linhas)", DiffLineKind.Notice));
        Placeholder = "";
    }

    public void ShowPlaceholder(string text, string title = "Diff")
    {
        Title = title;
        Text = "";
        Lines.Clear();
        Placeholder = text;
    }
}
