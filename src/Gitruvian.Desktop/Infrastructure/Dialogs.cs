using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Gitruvian.Desktop.Infrastructure;

/// <summary>Dados exibidos e editados no diálogo de configurações do repositório.</summary>
public sealed record RepoSettingsForm(string TestCommand, bool TestCommandFromEnv, string ProviderLabel,
    string ProviderExplanation, string EnvFiles, string DataDir);

/// <summary>Diálogos usados pela interface. Os testes substituem por respostas prontas.</summary>
public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string text, string confirmLabel = "OK", bool danger = false);
    Task<string?> AskDirectoryAsync(string title);
    Task<string?> AskOpenFileAsync(string title, string filterName, string[] patterns);
    Task<string?> AskSaveFileAsync(string title, string defaultName);
    Task<string?> AskTextAsync(string title, string prompt, string initial = "");
    Task ShowInfoAsync(string title, string text);

    /// <summary>Retorna o novo comando de testes, ou null se o usuário cancelou.</summary>
    Task<string?> EditRepoSettingsAsync(RepoSettingsForm form);
}

/// <summary>Implementação real com janelas Avalonia (modais, navegáveis por teclado).</summary>
public sealed class AvaloniaDialogs(Func<Window?> owner) : IDialogs
{
    private Window Owner => owner() ?? throw new InvalidOperationException("janela principal indisponível");

    private static Window MakeWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static Button MakeButton(string text, bool primary = false, bool danger = false)
    {
        var button = new Button { Content = text, MinWidth = 96 };
        AutomationProperties.SetName(button, text.Replace("_", ""));
        if (primary) button.Classes.Add("primary");
        if (danger) button.Classes.Add("danger");
        return button;
    }

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    private static SelectableTextBlock Body(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 640,
    };

    public async Task<bool> ConfirmAsync(string title, string text, string confirmLabel = "OK", bool danger = false)
    {
        var window = MakeWindow(title, 520);
        var ok = MakeButton(confirmLabel, primary: !danger, danger: danger);
        var cancel = MakeButton("Cancelar");
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        cancel.IsCancel = true;
        // Em ações destrutivas, Enter não confirma por engano: o foco começa em Cancelar.
        ok.IsDefault = !danger;
        window.Content = new StackPanel { Margin = new Thickness(20), Children = { Body(text), ButtonRow(ok, cancel) } };
        window.Opened += (_, _) => (danger ? cancel : ok).Focus(NavigationMethod.Tab);
        return await window.ShowDialog<bool?>(Owner) == true;
    }

    public async Task<string?> AskDirectoryAsync(string title)
    {
        var result = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskOpenFileAsync(string title, string filterName, string[] patterns)
    {
        var result = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskSaveFileAsync(string title, string defaultName)
    {
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> AskTextAsync(string title, string prompt, string initial = "")
    {
        var window = MakeWindow(title, 480);
        var box = new TextBox { Text = initial };
        AutomationProperties.SetName(box, prompt);
        var label = new Label { Content = prompt, Target = box };
        var ok = MakeButton("OK", primary: true);
        var cancel = MakeButton("Cancelar");
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => window.Close(box.Text);
        cancel.Click += (_, _) => window.Close(null);
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 6, Children = { label, box, ButtonRow(ok, cancel) } };
        window.Opened += (_, _) =>
        {
            box.Focus(NavigationMethod.Tab);
            box.SelectAll();
        };
        return await window.ShowDialog<string?>(Owner);
    }

    public async Task ShowInfoAsync(string title, string text)
    {
        var window = MakeWindow(title, 600);
        var ok = MakeButton("Fechar", primary: true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => window.Close();
        window.Content = new StackPanel { Margin = new Thickness(20), Children = { Body(text), ButtonRow(ok) } };
        window.Opened += (_, _) => ok.Focus(NavigationMethod.Tab);
        await window.ShowDialog(Owner);
    }

    public async Task<string?> EditRepoSettingsAsync(RepoSettingsForm form)
    {
        var window = MakeWindow("Configurações do repositório", 640);
        var command = new TextBox
        {
            Text = form.TestCommand,
            PlaceholderText = "ex.: dotnet test   ·   npm test   ·   cargo test",
            FontFamily = new FontFamily(Palette.MonoFamilies),
        };
        AutomationProperties.SetName(command, "Comando de testes");
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 6 };
        panel.Children.Add(new Label { Content = "Comando de _testes", Target = command });
        panel.Children.Add(command);
        panel.Children.Add(Muted("Roda sem shell, na raiz do repositório. O código de saída 0 significa que os testes passaram."));
        if (form.TestCommandFromEnv)
            panel.Children.Add(Muted("A variável GITRUVIAN_TEST_COMMAND do .env tem prioridade sobre este campo."));
        panel.Children.Add(Heading("Modelo"));
        panel.Children.Add(Body($"{form.ProviderLabel}\n{form.ProviderExplanation}"));
        panel.Children.Add(Heading("Arquivos .env lidos"));
        panel.Children.Add(Body(form.EnvFiles));
        panel.Children.Add(Heading("Dados deste repositório"));
        panel.Children.Add(Body(form.DataDir));
        var ok = MakeButton("Salvar", primary: true);
        var cancel = MakeButton("Cancelar");
        ok.IsDefault = true;
        cancel.IsCancel = true;
        ok.Click += (_, _) => window.Close(command.Text ?? "");
        cancel.Click += (_, _) => window.Close(null);
        panel.Children.Add(ButtonRow(ok, cancel));
        window.Content = panel;
        window.Opened += (_, _) => command.Focus(NavigationMethod.Tab);
        return await window.ShowDialog<string?>(Owner);
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Classes = { "muted" },
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 0),
    };
}
