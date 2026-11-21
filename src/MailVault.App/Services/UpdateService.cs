using System.Runtime.InteropServices;
using System.Windows;
using MailVault.App.Localization;
using MailVault.App.ViewModels;
using MailVault.Core;
using MailVault.Core.Updates;

namespace MailVault.App.Services;

/// <summary>
/// Sucht im Hintergrund regelmäßig nach Updates. Mit „automatisch installieren“ wird ein neues Paket sofort geladen und
/// eingespielt, sobald MailVault gerade nicht benutzt wird: kein Abruf läuft, kein Dialog offen und das Fenster ist
/// geschlossen (Infobereich) oder seit 10 Minuten unbenutzt. Sonst gibt es nur einen Hinweis.
/// </summary>
public static class UpdateService
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);
    static readonly TimeSpan IdleNeeded = TimeSpan.FromMinutes(10);
    static string? _notifiedVersion;
    static string? _stagedVersion;
    static string? _staging;
    static bool _running;

    /// <summary>Zuletzt gefundenes Update (für die Anzeige unter Verwaltung → Updates).</summary>
    public static UpdateCheck? Pending { get; private set; }

    static string InstallDir => UpdateViewModel.InstallDir;

    /// <summary>Update-Verwaltung; null, wenn diese Kopie nicht installiert ist.</summary>
    public static Task<UpdateManager?> CreateManagerAsync() =>
        Task.FromResult(InstallInfo.Load(InstallDir) is { } info ? new UpdateManager(info, InstallDir) : null);

    public static async Task<UpdateCheck?> CheckNowAsync()
    {
        if (await CreateManagerAsync() is not { } m) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var check = await Task.Run(() => m.CheckAsync(cts.Token));
        AppState.Settings.Updates.LastCheckUtc = DateTime.UtcNow;
        AppState.Settings.Save();
        Pending = check.Available ? check : null;
        return check;
    }

    /// <summary>Startet die Hintergrundsuche (einmal pro Programmstart).</summary>
    public static void Start()
    {
        if (_running) return;
        _running = true;
        _ = LoopAsync();
    }

    static async Task LoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30)); // Programmstart nicht bremsen
        while (true)
        {
            try { await TickAsync(); } catch { /* offline o. Ä. – beim nächsten Mal erneut */ }
            await Task.Delay(_staging is not null ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15));
        }
    }

    static async Task TickAsync()
    {
        var s = AppState.Settings.Updates;
        if (!s.AutoCheck || InstallInfo.Load(InstallDir) is null) return;

        if (_staging is null && (s.LastCheckUtc is not { } last || DateTime.UtcNow - last >= CheckInterval || Pending is not null))
        {
            var check = Pending ?? await CheckNowAsync();
            if (check is not { Available: true }) return;
            if (!s.AutoInstall)
            {
                if (_notifiedVersion != check.TargetVersion)
                {
                    _notifiedVersion = check.TargetVersion;
                    App.Current.Background?.ShowInfo(L.T("Upd_TrayAvailableTitle"), L.F("Upd_TrayAvailableText", check.TargetVersion));
                }
                return;
            }
            // Paket schon jetzt laden, damit das Einspielen später nur Sekunden dauert
            if (await CreateManagerAsync() is not { } m) return;
            _staging = await m.DownloadAsync(check, null, CancellationToken.None);
            _stagedVersion = check.TargetVersion;
            if (MainWindowVisible())
                App.Current.Background?.ShowInfo(L.T("Upd_TrayPreparingTitle"), L.F("Upd_TrayPreparingText", check.TargetVersion));
        }

        if (_staging is not null && s.AutoInstall && IsIdle())
        {
            if (await CreateManagerAsync() is not { } m) return;
            ApplyAndRestart(m, _staging, _stagedVersion!);
        }
    }

    static bool MainWindowVisible() => Application.Current.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

    /// <summary>Darf jetzt neu gestartet werden, ohne jemanden zu stören?</summary>
    static bool IsIdle()
    {
        if (AppState.RunningProfile is not null) return false;
        if (Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible && w != Application.Current.MainWindow)) return false; // Dialog, Import, E-Mail-Fenster
        return !MainWindowVisible() || UserIdleTime() >= IdleNeeded;
    }

    /// <summary>Spielt das geladene Paket ein: Skript starten, MailVault beenden; es startet danach selbst neu.</summary>
    public static void ApplyAndRestart(UpdateManager manager, string staging, string version)
    {
        var args = MainWindowVisible() ? "" : Autostart.BackgroundArgument;
        var script = manager.WriteApplyScript(staging, Environment.ProcessId, version, args);
        UpdateManager.LaunchApplyScript(script);
        App.Current.ExitApplication(force: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    static TimeSpan UserIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }
}
