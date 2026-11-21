using MailVault.App.Localization;
using MailVault.Core;
using System.Windows;

namespace MailVault.App.Dialogs;

/// <summary>Kennwort festlegen, ändern oder zur Bestätigung abfragen.</summary>
public partial class PasswordDialog : Window
{
    public const int MinLength = 8;
    readonly bool _askCurrent, _askNew;

    public string CurrentPassword => Current.Password;
    public string NewPassword => New1.Password;

    public PasswordDialog(string header, bool askCurrent, bool askNew)
    {
        InitializeComponent();
        HeaderText.Text = header;
        _askCurrent = askCurrent;
        _askNew = askNew;
        CurrentPanel.Visibility = askCurrent ? Visibility.Visible : Visibility.Collapsed;
        NewPanel.Visibility = askNew ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = (askCurrent, askNew) switch
        {
            (false, true) => L.T("Password_Set"),
            (true, true) => L.T("Password_Change"),
            _ => L.T("Password_Confirm"),
        };
        Loaded += (_, _) => { if (askCurrent) Current.Focus(); else New1.Focus(); };
    }

    public static string? Validate(string pw, string confirm)
    {
        if (pw.Length < MinLength) return L.F("Password_TooShort", MinLength);
        if (pw != confirm) return L.T("Password_Mismatch");
        return null;
    }

    void New1_Changed(object sender, RoutedEventArgs e)
    {
        var pw = New1.Password;
        if (pw.Length == 0) { StrengthBar.Value = 0; StrengthText.Text = L.T("Password_Hint"); return; }
        var level = PasswordStrength.Rate(pw);
        StrengthBar.Value = (int)level;
        StrengthText.Text = L.T("Password_Strength" + level);
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_askCurrent && Current.Password.Length == 0) { ErrorText.Text = L.T("Password_EnterCurrent"); return; }
        if (_askNew && Validate(New1.Password, New2.Password) is { } err) { ErrorText.Text = err; return; }
        // Schwache Kennwörter nicht verbieten, aber deutlich nachfragen: Gegen ein gestohlenes Archiv hilft nur Länge.
        if (_askNew && PasswordStrength.Rate(New1.Password) <= PasswordStrength.Level.Weak &&
            MessageBox.Show(this, L.T("Password_WeakConfirm"), "MailVault", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
