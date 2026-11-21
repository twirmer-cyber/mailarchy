using MailVault.App.Localization;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MailVault.App.Dialogs;

public partial class FirstRunWindow : Window
{
    public string SelectedDirectory { get; private set; } = "";

    public FirstRunWindow(string defaultDirectory)
    {
        InitializeComponent();
        PathBox.Text = defaultDirectory;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("FirstRun_PickFolder") };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FolderName;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        try
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("FirstRun_FolderUnusable", MailVault.Core.ErrorText.Describe(ex)), "MailVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedDirectory = path;
        DialogResult = true;
    }
}
