using Microsoft.Win32;

namespace MailVault.Core;

/// <summary>
/// Start mit Windows über HKCU\...\Run (kein Administratorrecht nötig, nur für den aktuellen Benutzer).
/// MailVault startet dann minimiert im Infobereich und führt die geplanten Abrufe aus.
/// </summary>
public sealed class Autostart
{
    public const string BackgroundArgument = "--background";
    const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly string _keyPath;
    readonly string _valueName;

    /// <param name="keyPath">Registrierungsschlüssel unter HKCU (für Tests austauschbar).</param>
    public Autostart(string valueName = "MailVault Home", string keyPath = DefaultKey)
    {
        _valueName = valueName;
        _keyPath = keyPath;
    }

    public static string CommandFor(string exePath) => $"\"{exePath}\" {BackgroundArgument}";

    public bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(_valueName) is string v && string.Equals(v, CommandFor(exePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Eingetragener Befehl, auch wenn er auf eine andere (z. B. verschobene) EXE zeigt.</summary>
    public string? RegisteredCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(_valueName) as string;
        }
    }

    public void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        if (enabled) key.SetValue(_valueName, CommandFor(exePath), RegistryValueKind.String);
        else if (key.GetValue(_valueName) is not null) key.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
