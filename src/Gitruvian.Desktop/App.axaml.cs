using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Gitruvian.Core;
using Gitruvian.Desktop.Infrastructure;
using Gitruvian.Desktop.ViewModels;
using Gitruvian.Desktop.Views;

namespace Gitruvian.Desktop;

public sealed class App : Application
{
    /// <summary>Repositório passado na linha de comando (ex.: atalho ou "Abrir com"), se houver.</summary>
    public static string? StartupPath { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings? settings = null;
            string? configError = null;
            try
            {
                settings = Config.Load();
            }
            catch (ConfigException exc)
            {
                configError = exc.Message;
            }
            if (settings is null)
            {
                desktop.MainWindow = ConfigErrorWindow(configError ?? "");
            }
            else
            {
                var window = new MainWindow();
                var vm = new MainViewModel(settings, new AvaloniaDialogs(() => window), new VelopackUpdater(settings));
                window.DataContext = vm;
                desktop.MainWindow = window;
                window.Opened += (_, _) =>
                {
                    vm.StartupUpdateCheck();
                    if (StartupPath is not null)
                        vm.OpenRepo(StartupPath);
                };
                window.Closing += (_, _) => vm.OnClosing();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static Window ConfigErrorWindow(string message)
    {
        var close = new Button
        {
            Content = "Fechar",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var window = new Window
        {
            Title = "Gitruvian — configuração inválida",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new SelectableTextBlock
                    {
                        Text = $"O arquivo .env tem um valor inválido:\n\n{message}\n\nCorrija o .env (veja .env.example) e " +
                               $"abra o Gitruvian de novo. Local padrão: {Path.Combine(Config.UserConfigDir(), ".env")}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }
}
