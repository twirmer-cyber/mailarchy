using MailVault.App.Localization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using MailVault.Core;
using Microsoft.Win32;

namespace MailVault.App.Dialogs;

/// <summary>
/// Anmeldung am geschützten Archiv. Drei Wege: Kennwort; Wiederherstellungsschlüssel (Kennwort vergessen);
/// Notfallblatt (die Schlüsseldatei fehlt – dann wird sie aus dem Blatt wiederhergestellt).
/// </summary>
public partial class LoginWindow : Window
{
    readonly string _dir;
    readonly bool _keyFileMissing;
    bool _recoveryMode;
    KeyVault.KeyFile? _sheetKeyFile;
    public MailArchive? Archive { get; private set; }

    static readonly Regex KeyPattern = new(@"\b[A-HJ-NP-Z2-9]{5}(?:-[A-HJ-NP-Z2-9]{5}){5}\b");

    public LoginWindow(string dataDirectory, bool keyFileMissing = false)
    {
        InitializeComponent();
        _dir = dataDirectory;
        _keyFileMissing = keyFileMissing;
        if (keyFileMissing)
        {
            HeaderText.Text = L.T("Login_KeyFileMissingHeader");
            RecoveryIntro.Text = L.F("Login_KeyFileMissingIntro", dataDirectory);
            SheetPanel.Visibility = Visibility.Visible;
            ShowRecovery();
        }
        Loaded += (_, _) => { if (_recoveryMode) RecoveryBox.Focus(); else PasswordBox.Focus(); };
    }

    void ShowRecovery()
    {
        _recoveryMode = true;
        PasswordPanel.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Visible;
        OkButton.Content = L.T("Login_Restore");
    }

    void Forgot_Click(object sender, RoutedEventArgs e)
    {
        ShowRecovery();
        RecoveryBox.Focus();
    }

    void PickSheet_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("Login_SheetFilter") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dlg.FileName);
            _sheetKeyFile = EmergencySheet.ReadKeyFile(text);
            if (KeyPattern.Match(text) is { Success: true } m && RecoveryBox.Text.Length == 0) RecoveryBox.Text = m.Value;
            SheetStatus.Text = L.F("Login_SheetLoaded", Path.GetFileName(dlg.FileName));
            ErrorText.Text = "";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _sheetKeyFile = null;
            SheetStatus.Text = "";
            ErrorText.Text = L.T("Login_SheetInvalid");
        }
    }

    async void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        string? recovery = null, password;
        if (_recoveryMode)
        {
            recovery = RecoveryBox.Text;
            password = NewPassword1.Password;
            if (_keyFileMissing && _sheetKeyFile is null) { ErrorText.Text = L.T("Login_SheetRequired"); return; }
            var error = PasswordDialog.Validate(password, NewPassword2.Password);
            if (error is not null) { ErrorText.Text = error; return; }
        }
        else password = PasswordBox.Password;

        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        var sheet = _sheetKeyFile;
        try
        {
            // Schlüsselableitung (PBKDF2) und SQLCipher-Öffnen dauern spürbar – nicht im UI-Thread.
            Archive = await Task.Run(() =>
                recovery is null ? MailArchive.Open(_dir, password)
                : sheet is not null ? MailArchive.RestoreKeyFile(_dir, sheet, recovery, password)
                : MailArchive.OpenWithRecoveryKey(_dir, recovery, password));
            DialogResult = true;
        }
        catch (CryptographicException ex)
        {
            // Core liefert die genaue Ursache: Kennwort/Schlüssel falsch, Schlüsseldatei beschädigt, anderes Archiv.
            ErrorText.Text = ex.Message;
        }
        catch (InvalidDataException ex)
        {
            ErrorText.Text = ex.Message; // Schlüsseldatei beschädigt (beide Kopien)
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }
}
