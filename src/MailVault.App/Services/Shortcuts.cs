namespace MailVault.App.Services;

/// <summary>Startmenü-Verknüpfung über das Windows-Skriptobjekt (keine zusätzliche Bibliothek nötig).</summary>
public static class Shortcuts
{
    public static string StartMenuPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MailVault Home.lnk");

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
    }
}
