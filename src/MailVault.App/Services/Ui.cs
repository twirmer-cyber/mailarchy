using MailVault.App.Localization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MailVault.App.Dialogs;
using MailVault.Core;

namespace MailVault.App.Services;

/// <summary>Kleine UI-Helfer für Meldungen, Fortschritt und das Öffnen von Dateien.</summary>
public static class Ui
{
    static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static void Info(string text) =>
        MessageBox.Show(Owner!, text, "MailVault", MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string text) =>
        MessageBox.Show(Owner!, text, "MailVault", MessageBoxButton.OK, MessageBoxImage.Error);

    public static bool Confirm(string text) =>
        MessageBox.Show(Owner!, text, "MailVault", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Führt eine lange Aufgabe mit Fortschrittsdialog und Protokoll aus.</summary>
    public static string? RunWithProgress(string title, Func<IProgress<ProgressInfo>, CancellationToken, Task<string>> work, bool autoClose = false)
    {
        var dlg = new ProgressWindow(title, work) { Owner = Owner, AutoCloseOnSuccess = autoClose };
        dlg.ShowDialog();
        return dlg.ResultText;
    }

    public static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }


    static string TempRoot => Path.Combine(Path.GetTempPath(), "MailVault");

    /// <summary>
    /// Entfernt geöffnete Anhänge aus dem Temp-Ordner (beim Start und Beenden). Sie liegen dort entschlüsselt;
    /// noch geöffnete Dateien werden übersprungen und beim nächsten Mal entfernt.
    /// </summary>
    public static void CleanupTempFiles()
    {
        if (!Directory.Exists(TempRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(TempRoot))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Kopiert ein Geheimnis (Wiederherstellungsschlüssel) in die Zwischenablage, ohne dass es im Windows-Verlauf
    /// (Win+V) oder in der Cloud-Zwischenablage landet, und leert sie nach 60 Sekunden wieder.
    /// </summary>
    public static void CopySecret(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        var zero = new MemoryStream(BitConverter.GetBytes(0));
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
        data.SetData("CanIncludeInClipboardHistory", zero);
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, copy: true);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { /* Zwischenablage gerade belegt */ }
        };
        timer.Start();
    }

    static readonly string[] DangerousExtensions =
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".ps1", ".hta", ".lnk", ".jar", ".reg", ".cpl", ".dll", ".iso", ".img", ".vhd", ".vhdx", ".appx", ".msix",
    };

    /// <summary>Speichert eine Datei temporär und öffnet sie; ausführbare Typen erfordern eine Bestätigung.</summary>
    public static void OpenTempFile(string fileName, byte[] content)
    {
        var safe = Exporter.SafeName(fileName);
        if (DangerousExtensions.Contains(Path.GetExtension(safe).ToLowerInvariant()) &&
            !Confirm(L.F("Ui_DangerousFile", safe)))
            return;
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, safe);
        File.WriteAllBytes(path, content);
        OpenWithShell(path);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => L.F("Ui_Bytes", bytes),
        < 1024 * 1024 => L.F("Ui_KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => L.F("Ui_MB", bytes / 1024.0 / 1024),
        _ => L.F("Ui_GB", bytes / 1024.0 / 1024 / 1024),
    };
}
