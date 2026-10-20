using Avalonia;
using Velopack;

namespace Gitruvian.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Precisa ser a primeira coisa: trata os ganchos de instalação, atualização e desinstalação do Velopack.
        VelopackApp.Build().Run();
        App.StartupPath = args.FirstOrDefault(a => !a.StartsWith('-') && Directory.Exists(a));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
