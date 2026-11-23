using System.Windows;
using MailVault.Core;

namespace MailVault.App.Services;

/// <summary>Gemeinsamer Zustand der Anwendung: geöffnetes Archiv, Profile, Einstellungen und Zeitplaner.</summary>
public static class AppState
{
    public static AppSettings Settings { get; private set; } = new();
    public static MailArchive Archive { get; private set; } = null!;
    public static ProfileStore Profiles { get; private set; } = null!;
    public static ArchiveScheduler Scheduler { get; private set; } = null!;
    public static Autostart Autostart { get; } = new();

    /// <summary>Wird ausgelöst, wenn sich der Archivinhalt geändert hat (Import, Löschen, Umbenennen).</summary>
    public static event Action? ArchiveChanged;
    public static event Action? ProfilesChanged;
    /// <summary>Ein Archivierungslauf hat begonnen oder ist beendet (Status für Infobereich und Startseite).</summary>
    public static event Action? RunStateChanged;

    /// <summary>Profil, das gerade archiviert wird (null = keins).</summary>
    public static ArchiveProfile? RunningProfile { get; private set; }

    public static string ExePath => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "MailVault.exe");

    public static void Initialize(AppSettings settings, MailArchive archive)
    {
        Settings = settings;
        Archive = archive;
        Profiles = new ProfileStore(archive.DataDirectory);
        // Eigene Client-IDs aus den Einstellungen haben Vorrang vor den eingebauten.
        settings.OAuth ??= new();
        MailVault.Core.OAuth.OAuthProviders.ApplyUserClients(settings.OAuth);
        // OAuth-Anmeldungen (Refresh-Tokens, DPAPI) liegen neben den Profilen im Datenordner.
        MailVault.Core.OAuth.OAuthService.Current = new MailVault.Core.OAuth.OAuthService(new MailVault.Core.OAuth.OAuthTokenStore(archive.DataDirectory));
        ArchiveImport.CleanupLeftovers(archive.DataDirectory);
        Profiles.Changed += NotifyProfilesChanged;
        Scheduler = new ArchiveScheduler(Profiles,
            (p, progress, ct) => ImportJob.RunAsync(Archive, p, progress, ct),
            () => Settings.Notification);
        Scheduler.RunStarted += p => { RunningProfile = p; OnUi(() => RunStateChanged?.Invoke()); };
        Scheduler.RunFinished += o =>
        {
            RunningProfile = null;
            OnUi(() => RunStateChanged?.Invoke());
            if (o.Result is { Archived: > 0 }) NotifyArchiveChanged();
        };
    }

    public static bool HasScheduledProfiles => Profiles.All.Any(p => p.Schedule != ScheduleKind.Manual);

    /// <summary>Im Hintergrund weiterlaufen, wenn das Fenster geschlossen wird?</summary>
    public static bool ShouldStayInTray => Settings.CloseToTray && (HasScheduledProfiles || Settings.Web.Enabled);

    public static void NotifyArchiveChanged() => OnUi(() => ArchiveChanged?.Invoke());
    public static void NotifyProfilesChanged() => OnUi(() => ProfilesChanged?.Invoke());

    public static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    public static void Shutdown()
    {
        try { Archive?.Dispose(); } catch { /* beim Beenden unkritisch */ }
    }
}
