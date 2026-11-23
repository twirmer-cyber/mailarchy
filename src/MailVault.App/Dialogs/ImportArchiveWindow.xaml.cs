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
/// Ein gesichertes Archiv (alter PC, externe Festplatte) einbinden: ins laufende Archiv übernehmen oder die
/// Installation darauf umstellen. Die Sicherung selbst wird in keinem Fall verändert.
/// </summary>
public partial class ImportArchiveWindow : Window
{
    static readonly Regex KeyPattern = new(@"\b[A-HJ-NP-Z2-9]{5}(?:-[A-HJ-NP-Z2-9]{5}){5}\b");

    readonly string _currentDir;
    ArchiveFolderKind _kind = ArchiveFolderKind.NotAnArchive;
    bool _recoveryMode;
    KeyVault.KeyFile? _sheetKeyFile;

    public string SourceDirectory { get; private set; } = "";
    public bool SwitchToArchive => SwitchRadio.IsChecked == true;
    public ArchiveCredentials Credentials { get; private set; } = new();

    public ImportArchiveWindow(string currentDataDirectory)
    {
        InitializeComponent();
        _currentDir = currentDataDirectory;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("ImportArchive_PickFolder") };
        if (dlg.ShowDialog(this) != true) return;
        SourceDirectory = dlg.FolderName;
        PathBox.Text = SourceDirectory;
        ErrorText.Text = "";
        _sheetKeyFile = null;
        SheetStatus.Text = "";
        try { _kind = ArchiveImport.Inspect(SourceDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _kind = ArchiveFolderKind.NotAnArchive; }

        var same = _kind != ArchiveFolderKind.NotAnArchive && ArchiveImport.IsSameDirectory(SourceDirectory, _currentDir);
        FolderStatus.Text = same ? L.T("ImportArchive_IsCurrent") : _kind switch
        {
            ArchiveFolderKind.Plain => L.T("ImportArchive_FoundPlain"),
            ArchiveFolderKind.Protected => L.T("ImportArchive_FoundProtected"),
            ArchiveFolderKind.KeyFileMissing => L.T("ImportArchive_FoundKeyMissing"),
            _ => L.T("ImportArchive_NotFound"),
        };
        var ok = _kind != ArchiveFolderKind.NotAnArchive && !same;
        FolderStatus.SetResourceReference(ForegroundProperty, ok ? "MutedTextBrush" : "DangerBrush");
        OkButton.IsEnabled = ok;
        _recoveryMode = _kind == ArchiveFolderKind.KeyFileMissing;
        UpdateAccessPanel();
    }

    void Mode_Changed(object sender, RoutedEventArgs e) => UpdateAccessPanel();

    /// <summary>Zugangsdaten werden nur beim Übernehmen gebraucht; beim Umstellen fragt MailVault nach dem Neustart danach.</summary>
    void UpdateAccessPanel()
    {
        if (AccessPanel is null) return;
        var needsAccess = MergeRadio.IsChecked == true && _kind is ArchiveFolderKind.Protected or ArchiveFolderKind.KeyFileMissing;
        AccessPanel.Visibility = needsAccess ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = _recoveryMode ? Visibility.Collapsed : Visibility.Visible;
        RecoveryPanel.Visibility = _recoveryMode ? Visibility.Visible : Visibility.Collapsed;
        SheetPanel.Visibility = _kind == ArchiveFolderKind.KeyFileMissing ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = L.T(SwitchToArchive ? "ImportArchive_SwitchOk" : "ImportArchive_Ok");
    }

    void UseRecovery_Click(object sender, RoutedEventArgs e)
    {
        _recoveryMode = true;
        UpdateAccessPanel();
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
        if (SwitchToArchive)
        {
            DialogResult = true;
            return;
        }
        var credentials = _recoveryMode
            ? new ArchiveCredentials(RecoveryKey: RecoveryBox.Text.Trim(), SheetKeyFile: _sheetKeyFile)
            : new ArchiveCredentials(Password: PasswordBox.Password);
        if (_kind == ArchiveFolderKind.KeyFileMissing && _sheetKeyFile is null) { ErrorText.Text = L.T("Login_SheetRequired"); return; }

        IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var dir = SourceDirectory;
            // Schlüsselableitung (PBKDF2, 600 000 Runden) nicht im UI-Thread
            await Task.Run(() => ArchiveImport.CheckCredentials(dir, credentials));
            Credentials = credentials;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException)
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
