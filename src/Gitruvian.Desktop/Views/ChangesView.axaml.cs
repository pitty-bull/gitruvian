using Avalonia.Controls;
using Avalonia.Input;
using Gitruvian.Desktop.ViewModels;

namespace Gitruvian.Desktop.Views;

public partial class ChangesView : UserControl
{
    public ChangesView() => InitializeComponent();

    private ChangesViewModel? Vm => DataContext as ChangesViewModel;

    // Enter prepara (ou tira do stage) o arquivo selecionado.
    private void OnUnstagedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.StageCommand.CanExecute(null) == true)
        {
            Vm.StageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnStagedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.UnstageCommand.CanExecute(null) == true)
        {
            Vm.UnstageCommand.Execute(null);
            e.Handled = true;
        }
    }
}
