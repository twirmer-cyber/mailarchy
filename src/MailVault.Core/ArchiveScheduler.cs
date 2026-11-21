using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Ergebnis eines (automatischen oder manuellen) Profillaufs.</summary>
public sealed record ProfileRunOutcome(ArchiveProfile Profile, ImportResult? Result, string? Error, bool NotificationSent, string? NotificationError);

/// <summary>
/// Führt Archivierungsprofile nach Zeitplan aus und benachrichtigt per E-Mail, wenn ein Postfach wiederholt nicht
/// abgerufen werden kann. Zeit, Ausführung und Versand sind austauschbar, damit sich alles ohne Warten testen lässt.
/// Es läuft immer nur ein Profil gleichzeitig (auch manuelle Läufe nutzen <see cref="RunGate"/>).
/// </summary>
public sealed class ArchiveScheduler
{
    readonly ProfileStore _profiles;
    readonly Func<ArchiveProfile, IProgress<ProgressInfo>?, CancellationToken, Task<ImportResult>> _run;
    readonly Func<NotificationSettings> _notification;
    readonly Func<NotificationSettings, INotifier> _notifierFactory;
    readonly Func<DateTime> _utcNow;
    readonly TimeZoneInfo _zone;

    /// <summary>Gemeinsame Sperre für alle Archivierungsläufe.</summary>
    public SemaphoreSlim RunGate { get; } = new(1, 1);

    public event Action<ArchiveProfile>? RunStarted;
    public event Action<ProfileRunOutcome>? RunFinished;

    public ArchiveScheduler(ProfileStore profiles,
        Func<ArchiveProfile, IProgress<ProgressInfo>?, CancellationToken, Task<ImportResult>> run,
        Func<NotificationSettings> notification,
        Func<NotificationSettings, INotifier>? notifierFactory = null,
        Func<DateTime>? utcNow = null,
        TimeZoneInfo? zone = null)
    {
        _profiles = profiles;
        _run = run;
        _notification = notification;
        _notifierFactory = notifierFactory ?? (s => new SmtpNotifier(s));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>
    /// Nächster geplanter Lauf (UTC); null = kein Zeitplan. Ein Zeitpunkt in der Vergangenheit bedeutet "fällig".
    /// Ein noch nie ausgeführtes Profil ist sofort fällig (Erstarchivierung). Verpasste Termine – etwa weil der PC aus
    /// war – werden einmal nachgeholt.
    /// </summary>
    public static DateTime? NextRunUtc(ArchiveProfile p, DateTime nowUtc, TimeZoneInfo zone)
    {
        switch (p.Schedule)
        {
            case ScheduleKind.Interval:
                var minutes = Math.Max(5, p.IntervalMinutes);
                return p.LastRunUtc is { } last ? last.AddMinutes(minutes) : nowUtc;

            case ScheduleKind.Daily:
                if (!ArchiveProfile.TryParseTime(p.DailyTime, out var time)) return null;
                if (p.LastRunUtc is null) return nowUtc;
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
                var slotLocal = nowLocal.Date + time;                 // letzter planmäßiger Termin ...
                if (slotLocal > nowLocal) slotLocal = slotLocal.AddDays(-1); // ... heute oder gestern
                var slotUtc = ToUtc(slotLocal, zone);
                return p.LastRunUtc < slotUtc ? slotUtc : ToUtc(slotLocal.AddDays(1), zone);

            default:
                return null;
        }
    }

    static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // Zeitumstellung: Termin fällt in die übersprungene Stunde
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public DateTime? NextRunUtc(ArchiveProfile p) => NextRunUtc(p, _utcNow(), _zone);

    public bool IsRunning => RunGate.CurrentCount == 0;

    public IReadOnlyList<ArchiveProfile> DueProfiles()
    {
        var now = _utcNow();
        return _profiles.All
            .Select(p => (p, next: NextRunUtc(p, now, _zone)))
            .Where(x => x.next is { } n && n <= now)
            .OrderBy(x => x.next)
            .Select(x => x.p)
            .ToList();
    }

    /// <summary>Führt alle fälligen Profile nacheinander aus. Läuft bereits etwas, passiert nichts.</summary>
    public async Task<IReadOnlyList<ProfileRunOutcome>> RunDueAsync(CancellationToken ct)
    {
        var outcomes = new List<ProfileRunOutcome>();
        if (!await RunGate.WaitAsync(0, ct)) return outcomes;
        try
        {
            foreach (var p in DueProfiles())
            {
                if (ct.IsCancellationRequested) break;
                // Zwischenzeitlich gelöscht oder umgeplant?
                var current = _profiles.Get(p.Id);
                if (current is null || NextRunUtc(current) is not { } n || n > _utcNow()) continue;
                outcomes.Add(await RunCoreAsync(current, automatic: true, progress: null, ct));
            }
        }
        finally { RunGate.Release(); }
        return outcomes;
    }

    /// <summary>Alle Profile mit Zeitplan sofort ausführen (z. B. "Jetzt alle abrufen" im Infobereich).</summary>
    public async Task<IReadOnlyList<ProfileRunOutcome>> RunAllScheduledAsync(CancellationToken ct)
    {
        var outcomes = new List<ProfileRunOutcome>();
        await RunGate.WaitAsync(ct);
        try
        {
            foreach (var p in _profiles.All.Where(p => p.Schedule != ScheduleKind.Manual))
            {
                if (ct.IsCancellationRequested) break;
                outcomes.Add(await RunCoreAsync(p, automatic: true, progress: null, ct));
            }
        }
        finally { RunGate.Release(); }
        return outcomes;
    }

    /// <summary>Manueller Lauf (wartet, bis ein laufender Hintergrundabruf fertig ist).</summary>
    public async Task<ProfileRunOutcome> RunNowAsync(ArchiveProfile profile, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await RunGate.WaitAsync(ct);
        try { return await RunCoreAsync(profile, automatic: false, progress, ct); }
        finally { RunGate.Release(); }
    }

    async Task<ProfileRunOutcome> RunCoreAsync(ArchiveProfile profile, bool automatic, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        RunStarted?.Invoke(profile);
        ImportResult? result = null;
        string? error = null;
        try
        {
            result = await _run(profile, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ImportResult { Cancelled = true };
        }
        catch (Exception ex)
        {
            error = ErrorText.Describe(ex);
        }

        var now = _utcNow();
        var settings = _notification();
        string? mailSubject = null, mailBody = null;
        var updated = _profiles.Update(profile.Id, p =>
        {
            p.LastRunUtc = now;
            if (error is null)
            {
                p.LastResult = result!.ToString();
                if (result.Cancelled) return;
                var wasNotified = p.FailureNotifiedUtc is not null;
                var failures = p.ConsecutiveFailures;
                p.LastSuccessUtc = now;
                p.LastError = null;
                p.ConsecutiveFailures = 0;
                p.FailureNotifiedUtc = null;
                if (wasNotified && settings.Enabled && settings.NotifyOnRecovery)
                    (mailSubject, mailBody) = RecoveryMail(p, failures, result);
            }
            else
            {
                p.LastResult = CoreText.F("Run_Failed", error);
                p.LastError = error;
                if (!automatic) return; // Fehler bei manuellen Läufen sieht der Benutzer direkt
                p.ConsecutiveFailures++;
                if (!settings.Enabled || p.ConsecutiveFailures < Math.Max(1, settings.FailureThreshold)) return;
                if (p.FailureNotifiedUtc is { } last && now - last < TimeSpan.FromHours(Math.Max(1, settings.RepeatHours))) return;
                (mailSubject, mailBody) = FailureMail(p, error, reminder: p.FailureNotifiedUtc is not null);
                p.FailureNotifiedUtc = now;
            }
        }) ?? profile;

        var sent = false;
        string? notificationError = null;
        if (mailSubject is not null)
        {
            try
            {
                await _notifierFactory(settings).SendAsync(mailSubject, mailBody!, CancellationToken.None);
                sent = true;
            }
            catch (Exception ex)
            {
                notificationError = ErrorText.Describe(ex);
                // Versand fehlgeschlagen: beim nächsten Fehlschlag erneut versuchen (Fehlerserie bleibt "unbenachrichtigt").
                if (error is not null) updated = _profiles.Update(profile.Id, p => p.FailureNotifiedUtc = null) ?? updated;
            }
        }

        var outcome = new ProfileRunOutcome(updated, result, error, sent, notificationError);
        RunFinished?.Invoke(outcome);
        return outcome;
    }

    static string Local(DateTime? utc) => utc is { } u ? CoreText.F("Mail_LocalTime", u.ToLocalTime()) : CoreText.T("Mail_Never");

    static string Source(ArchiveProfile p) => p.IsServer ? CoreText.F("Mail_ServerSource", p.UserName, p.Host) : p.Path;

    /// <summary>Benachrichtigung in der Oberflächensprache (Zeilenumbrüche immer "\n").</summary>
    static string Body(string key, params object?[] args) => CoreText.F(key, args).Replace("\r\n", "\n");

    internal static (string Subject, string Body) FailureMail(ArchiveProfile p, string error, bool reminder)
    {
        var subject = CoreText.F("Mail_Failure_Subject", p.Name);
        if (reminder) subject = CoreText.F("Mail_Reminder_Subject", subject);
        var body = Body("Mail_Failure_Body", p.Name, Source(p), p.ConsecutiveFailures, error,
            Local(p.LastSuccessUtc), Local(p.LastRunUtc), p.ScheduleDisplay);
        return (subject, body);
    }

    internal static (string Subject, string Body) RecoveryMail(ArchiveProfile p, int failures, ImportResult result)
    {
        var subject = CoreText.F("Mail_Recovery_Subject", p.Name);
        var body = Body("Mail_Recovery_Body", p.Name, Source(p), failures, result);
        return (subject, body);
    }

    /// <summary>Testnachricht aus den Einstellungen.</summary>
    public static Task SendTestAsync(NotificationSettings settings, CancellationToken ct) =>
        new SmtpNotifier(settings).SendAsync(CoreText.T("Mail_Test_Subject"), CoreText.T("Mail_Test_Body"), ct);
}
