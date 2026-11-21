using MailVault.App.Localization;
using MailVault.Web;

namespace MailVault.App.Services;

/// <summary>Webzugriff im Heimnetz: startet/stoppt den eingebauten Webserver passend zu den Einstellungen.</summary>
public static class WebHost
{
    static WebArchiveServer? _server;
    static WebUserStore? _users;

    /// <summary>Gemeinsame Benutzerverwaltung (dieselbe Instanz für Server und Einstellungen).</summary>
    public static WebUserStore Users => _users ??= new WebUserStore(AppState.Archive.DataDirectory);

    public static bool IsRunning => _server is not null;
    public static IReadOnlyList<string> Urls => _server?.Urls ?? Array.Empty<string>();
    public static string? LastError { get; private set; }
    public static event Action? StateChanged;

    public static void StartIfEnabled()
    {
        if (AppState.Settings.Web.Enabled) _ = RestartAsync();
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        if (!AppState.Settings.Web.Enabled) return;
        try
        {
            var s = AppState.Settings.Web;
            _server = await WebArchiveServer.StartAsync(AppState.Archive, Users, new WebServerOptions
            {
                Port = s.Port,
                LanAccess = s.LanAccess,
                BlockExternalContent = AppState.Settings.BlockExternalContent,
            });
            LastError = null;
        }
        catch (Exception ex)
        {
            _server = null;
            LastError = ex is System.IO.IOException
                ? L.F("Web_PortInUse", AppState.Settings.Web.Port)
                : ex.Message;
        }
        AppState.OnUi(() => StateChanged?.Invoke());
    }

    public static async Task StopAsync()
    {
        var s = _server;
        _server = null;
        if (s is not null) await s.DisposeAsync();
        AppState.OnUi(() => StateChanged?.Invoke());
    }

    /// <summary>Beim Beenden: synchron stoppen, bevor das Archiv geschlossen wird.</summary>
    public static void Stop()
    {
        try { StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { /* beim Beenden unkritisch */ }
    }

    public static void SignOut(string userName) => _server?.SignOutUser(userName);
}
