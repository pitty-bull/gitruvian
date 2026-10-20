using Avalonia.Controls;
using Avalonia.Input;
using Gitruvian.Desktop.ViewModels;

namespace Gitruvian.Desktop.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is not null)
        {
            Vm.OpenEventCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Vm?.OpenEventCommand.Execute(null);
}
