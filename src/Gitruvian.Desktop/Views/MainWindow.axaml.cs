using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Gitruvian.Desktop.ViewModels;

namespace Gitruvian.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Alt+1 … Alt+4 trocam de aba.
        for (var i = 0; i < 4; i++)
        {
            var tab = i;
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.D1 + i, KeyModifiers.Alt),
                Command = new RelayCommand(() =>
                {
                    if (Vm is { IsRepoOpen: true } vm) vm.SelectedTab = tab;
                }),
            });
        }
        Opened += (_, _) => this.FindControl<Button>("OpenButton")?.Focus(NavigationMethod.Tab);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();

    private void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == Key.Enter && Vm.OpenRecentCommand.CanExecute(null))
        {
            Vm.OpenRecentCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Vm.ForgetRecentCommand.CanExecute(null))
        {
            Vm.ForgetRecentCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.OpenRecentCommand.CanExecute(null) == true) Vm.OpenRecentCommand.Execute(null);
    }

    private void OnBranchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.SwitchBranchCommand.CanExecute(null) == true)
        {
            Vm.SwitchBranchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBranchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.SwitchBranchCommand.CanExecute(null) == true) Vm.SwitchBranchCommand.Execute(null);
    }

    private async void OnCopyDiff(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || Clipboard is null) return;
        var list = this.FindControl<ListBox>("DiffList");
        var selected = list?.SelectedItems?.OfType<DiffLine>().Select(l => l.Text).ToList();
        var text = selected is { Count: > 0 } ? string.Join("\n", selected) + "\n" : Vm.Diff.Text;
        if (text.Length == 0) return;
        await Clipboard.SetTextAsync(text);
        Vm.Notify("ok", selected is { Count: > 0 } ? $"{selected.Count} linha(s) copiada(s)." : "Diff copiado para a área de transferência.");
    }
}
