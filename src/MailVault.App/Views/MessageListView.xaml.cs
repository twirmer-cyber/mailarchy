using System.Windows.Controls;
using System.Windows.Input;
using MailVault.App.ViewModels;

namespace MailVault.App.Views;

public partial class MessageListView : UserControl
{
    public MessageListView() => InitializeComponent();

    MessageListViewModel? Vm => DataContext as MessageListViewModel;

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is { } vm) vm.SelectedItems = MessagesGrid.SelectedItems;
    }

    void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.FrameworkElement { DataContext: Core.MessageSummary }) Vm?.OpenCommand.Execute(null);
    }

    void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Vm?.DeleteCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { Vm?.OpenCommand.Execute(null); e.Handled = true; }
    }
}
