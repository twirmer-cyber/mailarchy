using System.Windows.Controls;
using System.Windows.Input;
using MailVault.App.ViewModels;

namespace MailVault.App.Views;

public partial class ArchiveView : UserControl
{
    public ArchiveView() => InitializeComponent();

    void Profiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ArchiveViewModel vm && vm.SelectedProfile is not null) vm.EditProfileCommand.Execute(null);
    }
}
