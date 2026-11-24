using MailVault.App.Localization;
using MailVault.Core;
using MailVault.Core.Localization;

namespace MailVault.App.Services;

/// <summary>
/// Prüft das Archiv regelmäßig (Standard: wöchentlich) im Hintergrund, wenn gerade nichts archiviert wird, und merkt
/// sich das Ergebnis für die Startseite. Findet die automatische Prüfung Probleme, meldet MailVault das im
/// Infobereich und – wenn eingerichtet – sofort per E-Mail-Benachrichtigung.
/// </summary>
public static class ArchiveCheckService
{
    static bool _started;
    static int _busy;

    /// <summary>Eine Prüfung ist beendet (im UI-Thread ausgelöst).</summary>
    public static event Action? Completed;
    public static event Action? RunningChanged;

    public static bool IsRunning => Volatile.Read(ref _busy) == 1;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _ = LoopAsync();
    }

    static async Task LoopAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(3)); // Programmstart und erste Archivierung nicht bremsen
        while (true)
        {
            try
            {
                if (AppState.RunningProfile is null && AppState.Settings.ArchiveCheck.IsDue(AppState.Archive.DataDirectory, DateTime.UtcNow))
                    await RunAsync(null, CancellationToken.None, automatic: true);
            }
            catch { /* nächster Versuch beim nächsten Durchlauf */ }
            await Task.Delay(TimeSpan.FromMinutes(30));
        }
    }

    /// <summary>Prüft jetzt. Liefert null, wenn bereits eine Prüfung läuft.</summary>
    public static async Task<ArchiveCheckResult?> RunAsync(IProgress<ProgressInfo>? progress, CancellationToken ct, bool automatic = false)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        AppState.OnUi(() => RunningChanged?.Invoke());
        try
        {
            var archive = AppState.Archive;
            var dir = archive.DataDirectory;
            var result = await Task.Run(() => archive.CheckArchive(progress, ct), ct);
            AppState.OnUi(() =>
            {
                AppState.Settings.ArchiveCheck.Store(result, dir);
                AppState.Settings.Save();
                Completed?.Invoke();
            });
            if (!result.Ok && automatic) await NotifyAsync(result, dir);
            return result;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            AppState.OnUi(() => RunningChanged?.Invoke());
        }
    }

    static async Task NotifyAsync(ArchiveCheckResult result, string dir)
    {
        AppState.OnUi(() => App.Current.Background?.ShowInfo(CoreText.T("Check_Mail_Subject"), L.T("Start_CheckFailedTray")));
        var n = AppState.Settings.Notification;
        if (!n.Enabled) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await new SmtpNotifier(n).SendAsync(CoreText.T("Check_Mail_Subject"),
                CoreText.F("Check_Mail_Body", dir, result.CheckedUtc.ToLocalTime(), string.Join("\n", result.Problems)), cts.Token);
        }
        catch { /* Benachrichtigung ist zusätzlich; der Hinweis steht auf der Startseite */ }
    }
}
