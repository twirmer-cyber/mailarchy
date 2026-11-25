namespace MailVault.App.Services;

/// <summary>Startmenü-Verknüpfung über das Windows-Skriptobjekt (keine zusätzliche Bibliothek nötig).</summary>
public static class Shortcuts
{
    static string Programs => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string StartMenuPath => System.IO.Path.Combine(Programs, "MailArchy.lnk");
    /// <summary>Verknüpfung bis Version 0.7 (damals „MailVault Home“).</summary>
    public static string LegacyStartMenuPath => System.IO.Path.Combine(Programs, "MailVault Home.lnk");

    public static void CreateStartMenu(string exePath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(MailVault.App.Localization.L.T("Shortcut_NoScriptHost"));
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(StartMenuPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
            link.Description = MailVault.App.Localization.L.T("Shortcut_Description");
            link.IconLocation = exePath + ",0";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        DeleteLegacy();
    }

    /// <summary>
    /// Nach dem Update von einer Version mit altem Namen: Verknüpfung „MailVault Home“ durch „MailArchy“ ersetzen.
    /// Nur für die installierte Version (nicht portabel), und nur wenn die alte Verknüpfung existiert.
    /// </summary>
    public static void MigrateLegacy(string exePath)
    {
        try
        {
            if (!System.IO.File.Exists(LegacyStartMenuPath)) return;
            CreateStartMenu(exePath);
        }
        catch (Exception) { /* Verknüpfung ist Komfort; Fehler hier dürfen den Start nicht stören */ }
    }

    static void DeleteLegacy()
    {
        try { if (System.IO.File.Exists(LegacyStartMenuPath)) System.IO.File.Delete(LegacyStartMenuPath); }
        catch (Exception) { /* bleibt dann eben liegen */ }
    }
}
