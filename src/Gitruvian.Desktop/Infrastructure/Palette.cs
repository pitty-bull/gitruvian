using Avalonia.Media;

namespace Gitruvian.Desktop.Infrastructure;

/// <summary>Cores e fontes do tema escuro (contraste AA ou melhor sobre o fundo).</summary>
public static class Palette
{
    public const string MonoFamilies = "Cascadia Mono, Consolas, JetBrains Mono, DejaVu Sans Mono, Courier New, monospace";

    public static readonly Color Background = Color.Parse("#1e1f22");
    public static readonly Color Text = Color.Parse("#e8e8e8");
    public static readonly Color Muted = Color.Parse("#b0b4bb");
    public static readonly Color Focus = Color.Parse("#ffd866");
    public static readonly Color Ok = Color.Parse("#8fd18f");
    public static readonly Color Error = Color.Parse("#ff8a80");
    public static readonly Color Warning = Color.Parse("#ffcc66");
    public static readonly Color Info = Color.Parse("#8ab4f8");

    /// <summary>Cores das raias do grafo (distinguíveis entre si e legíveis no fundo escuro).</summary>
    public static readonly Color[] Lanes =
    [
        Color.Parse("#61afef"), Color.Parse("#98c379"), Color.Parse("#e5c07b"), Color.Parse("#c678dd"),
        Color.Parse("#56b6c2"), Color.Parse("#e06c75"), Color.Parse("#d19a66"), Color.Parse("#abb2bf"),
    ];

    public static Color Lane(int index) => Lanes[((index % Lanes.Length) + Lanes.Length) % Lanes.Length];

    public static readonly Dictionary<string, Color> StatusColors = new()
    {
        ["proposed"] = Info,
        ["applied"] = Warning,
        ["tests_passed"] = Ok,
        ["tests_failed"] = Error,
        ["committed"] = Ok,
        ["reverted"] = Muted,
        ["rejected"] = Muted,
    };
}
