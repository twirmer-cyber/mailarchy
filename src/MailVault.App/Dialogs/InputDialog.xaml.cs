using MailVault.App.Localization;
using System.Windows;

namespace MailVault.App.Dialogs;

public partial class InputDialog : Window
{
    public string Value => ValueBox.Text;

    public InputDialog(string title, string prompt, string initial = "", string? confirmText = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        OkButton.Content = confirmText ?? L.T("Common_Apply");
        ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string prompt, string initial = "", string? confirmText = null)
    {
        var d = new InputDialog(title, prompt, initial, confirmText) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
