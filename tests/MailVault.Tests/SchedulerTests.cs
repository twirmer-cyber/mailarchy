using System.Net.Sockets;
using MailVault.Core;
using Xunit;

namespace MailVault.Tests;

/// <summary>Automatischer Abruf, Zeitplan und E-Mail-Benachrichtigung – mit simulierter Uhr, ohne Warten.</summary>
public class SchedulerTests
{
    sealed class FakeNotifier : INotifier
    {
        public List<(string Subject, string Body)> Sent { get; } = new();
        public bool Fail { get; set; }
        public Task SendAsync(string subject, string body, CancellationToken ct)
        {
            if (Fail) throw new SocketException((int)SocketError.ConnectionRefused);
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    sealed class Harness : IDisposable
    {
        readonly TempDir _tmp = new();
        public ProfileStore Store { get; }
        public DateTime Now { get; set; } = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        public FakeNotifier Notifier { get; } = new();
        public NotificationSettings Notification { get; } = new() { Enabled = true, SmtpHost = "smtp.example.org", From = "a@example.org", To = "b@example.org", FailureThreshold = 2, RepeatHours = 24 };
        public Func<ArchiveProfile, Task<ImportResult>> Behaviour { get; set; } = _ => Task.FromResult(new ImportResult { Archived = 3 });
        public List<string> Runs { get; } = new();
        public ArchiveScheduler Scheduler { get; }

        public Harness()
        {
            Store = new ProfileStore(_tmp.Path);
            Scheduler = new ArchiveScheduler(Store, (p, _, _) => { Runs.Add(p.Name); return Behaviour(p); },
                () => Notification, _ => Notifier, () => Now, TimeZoneInfo.Utc);
        }

        public ArchiveProfile Add(string name, ScheduleKind kind, int minutes = 60, string daily = "08:00", DateTime? lastRun = null)
        {
            var p = new ArchiveProfile { Name = name, Type = ProfileType.Imap, Host = "imap.example.org", UserName = name + "@example.org", Schedule = kind, IntervalMinutes = minutes, DailyTime = daily };
            Store.Save(p);
            if (lastRun is not null) Store.Update(p.Id, x => x.LastRunUtc = lastRun);
            return Store.Get(p.Id)!;
        }

        public ArchiveProfile Get(ArchiveProfile p) => Store.Get(p.Id)!;
        public void Dispose() => _tmp.Dispose();
    }

    static Exception AuthError() => new MailKit.Security.AuthenticationException("Invalid credentials");

    [Fact]
    public void Interval_profile_is_due_after_interval_and_never_run_profile_immediately()
    {
        using var h = new Harness();
        var never = h.Add("neu", ScheduleKind.Interval, 30);
        var recent = h.Add("frisch", ScheduleKind.Interval, 30, lastRun: h.Now.AddMinutes(-10));
        var old = h.Add("alt", ScheduleKind.Interval, 30, lastRun: h.Now.AddMinutes(-31));
        h.Add("manuell", ScheduleKind.Manual, lastRun: null);

        var due = h.Scheduler.DueProfiles().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "alt", "neu" }.OrderBy(x => x), due.OrderBy(x => x));
        Assert.Equal(h.Now.AddMinutes(20), h.Scheduler.NextRunUtc(recent));
        Assert.Null(h.Scheduler.NextRunUtc(h.Store.All.Single(p => p.Name == "manuell")));
        Assert.Equal(h.Now, h.Scheduler.NextRunUtc(never));
        Assert.True(h.Scheduler.NextRunUtc(old) < h.Now);
    }

    [Fact]
    public void Interval_below_five_minutes_is_clamped()
    {
        var p = new ArchiveProfile { Schedule = ScheduleKind.Interval, IntervalMinutes = 1, LastRunUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        Assert.Equal(p.LastRunUtc!.Value.AddMinutes(5), ArchiveScheduler.NextRunUtc(p, p.LastRunUtc.Value, TimeZoneInfo.Utc));
    }

    [Theory]
    // jetzt (UTC=Ortszeit), letzter Lauf, erwarteter nächster Termin
    [InlineData("2026-03-10 09:00", "2026-03-10 08:00", "2026-03-11 08:00")] // heute schon gelaufen
    [InlineData("2026-03-10 09:00", "2026-03-09 08:00", "2026-03-10 08:00")] // heute verpasst -> fällig (Vergangenheit)
    [InlineData("2026-03-10 07:00", "2026-03-09 08:01", "2026-03-10 08:00")] // vor dem Termin, gestern gelaufen -> heute 8 Uhr
    [InlineData("2026-03-10 07:00", "2026-03-08 08:00", "2026-03-09 08:00")] // gestrigen Termin verpasst -> sofort nachholen
    public void Daily_schedule_catches_up_missed_runs_once(string now, string lastRun, string expected)
    {
        var p = new ArchiveProfile { Schedule = ScheduleKind.Daily, DailyTime = "08:00", LastRunUtc = Utc(lastRun) };
        var next = ArchiveScheduler.NextRunUtc(p, Utc(now), TimeZoneInfo.Utc);
        Assert.Equal(Utc(expected), next);
    }

    [Fact]
    public void Daily_schedule_respects_time_zone_and_daylight_saving()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        // Sommerzeit: 08:00 Ortszeit = 06:00 UTC
        var p = new ArchiveProfile { Schedule = ScheduleKind.Daily, DailyTime = "08:00", LastRunUtc = Utc("2026-07-01 06:00") };
        Assert.Equal(Utc("2026-07-02 06:00"), ArchiveScheduler.NextRunUtc(p, Utc("2026-07-01 12:00"), berlin));
        // Winterzeit: 08:00 Ortszeit = 07:00 UTC
        p.LastRunUtc = Utc("2026-12-01 07:00");
        Assert.Equal(Utc("2026-12-02 07:00"), ArchiveScheduler.NextRunUtc(p, Utc("2026-12-01 12:00"), berlin));
        // Termin in der übersprungenen Stunde (29.03.2026, 02:30 gibt es nicht) -> 03:30 Sommerzeit = 01:30 UTC
        p.DailyTime = "02:30";
        p.LastRunUtc = Utc("2026-03-28 01:30");
        Assert.Equal(Utc("2026-03-29 01:30"), ArchiveScheduler.NextRunUtc(p, Utc("2026-03-28 12:00"), berlin));
    }

    [Theory]
    [InlineData("8:00", true)]
    [InlineData("08:30", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("abc", false)]
    public void Daily_time_parsing(string text, bool ok) => Assert.Equal(ok, ArchiveProfile.TryParseTime(text, out _));

    [Fact]
    public async Task Due_profiles_run_sequentially_and_status_is_recorded()
    {
        using var h = new Harness();
        var a = h.Add("A", ScheduleKind.Interval, 60);
        var b = h.Add("B", ScheduleKind.Interval, 60, lastRun: h.Now.AddMinutes(-5));
        var outcomes = await h.Scheduler.RunDueAsync(default);
        Assert.Equal(new[] { "A" }, h.Runs);
        Assert.Single(outcomes);
        var after = h.Get(a);
        Assert.Equal(h.Now, after.LastRunUtc);
        Assert.Equal(h.Now, after.LastSuccessUtc);
        Assert.Null(after.LastError);
        Assert.Contains("3 archiviert", after.LastResult);

        // eine Stunde später ist A wieder fällig, B auch
        h.Now = h.Now.AddMinutes(61);
        await h.Scheduler.RunDueAsync(default);
        Assert.Equal(new[] { "A", "B", "A" }.Order(), h.Runs.Order());
    }

    [Fact]
    public async Task Failure_notification_after_threshold_reminder_after_repeat_interval_and_recovery_mail()
    {
        using var h = new Harness();
        var p = h.Add("Familie", ScheduleKind.Interval, 60);
        h.Behaviour = _ => throw AuthError();

        await h.Scheduler.RunDueAsync(default);                       // 1. Fehler: unter der Schwelle (2)
        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(1, h.Get(p).ConsecutiveFailures);
        Assert.Contains("Anmeldung abgelehnt", h.Get(p).LastError);

        h.Now = h.Now.AddHours(1);
        var o = (await h.Scheduler.RunDueAsync(default)).Single();    // 2. Fehler: Benachrichtigung
        Assert.True(o.NotificationSent);
        var mail = Assert.Single(h.Notifier.Sent);
        Assert.Contains("Familie", mail.Subject);
        Assert.Contains("2-mal in Folge", mail.Body);
        Assert.Contains("Anmeldung abgelehnt", mail.Body);
        Assert.Contains("nichts gelöscht", mail.Body);

        for (var i = 0; i < 5; i++)                                   // weitere Fehler: keine Mailflut
        {
            h.Now = h.Now.AddHours(1);
            await h.Scheduler.RunDueAsync(default);
        }
        Assert.Single(h.Notifier.Sent);
        Assert.Equal(7, h.Get(p).ConsecutiveFailures);

        h.Now = h.Now.AddHours(20);                                   // > 24 h nach der ersten Mail: Erinnerung
        await h.Scheduler.RunDueAsync(default);
        Assert.Equal(2, h.Notifier.Sent.Count);
        Assert.StartsWith("Erinnerung:", h.Notifier.Sent[1].Subject);

        h.Behaviour = _ => Task.FromResult(new ImportResult { Archived = 12 }); // wieder ok: Entwarnung
        h.Now = h.Now.AddHours(1);
        await h.Scheduler.RunDueAsync(default);
        Assert.Equal(3, h.Notifier.Sent.Count);
        Assert.Contains("wieder archiviert", h.Notifier.Sent[2].Subject);
        Assert.Contains("12 archiviert", h.Notifier.Sent[2].Body);
        var ok = h.Get(p);
        Assert.Equal(0, ok.ConsecutiveFailures);
        Assert.Null(ok.FailureNotifiedUtc);
        Assert.Null(ok.LastError);
    }

    [Fact]
    public async Task Short_outage_below_threshold_sends_nothing_and_resets()
    {
        using var h = new Harness();
        var p = h.Add("Kurz", ScheduleKind.Interval, 60);
        h.Behaviour = _ => throw new SocketException((int)SocketError.TimedOut);
        await h.Scheduler.RunDueAsync(default);
        h.Behaviour = _ => Task.FromResult(new ImportResult());
        h.Now = h.Now.AddHours(1);
        await h.Scheduler.RunDueAsync(default);
        Assert.Empty(h.Notifier.Sent);                // weder Fehler- noch Entwarnungsmail
        Assert.Equal(0, h.Get(p).ConsecutiveFailures);
    }

    [Fact]
    public async Task Disabled_notifications_send_nothing_but_count_failures()
    {
        using var h = new Harness();
        h.Notification.Enabled = false;
        var p = h.Add("X", ScheduleKind.Interval, 60);
        h.Behaviour = _ => throw AuthError();
        for (var i = 0; i < 3; i++) { await h.Scheduler.RunDueAsync(default); h.Now = h.Now.AddHours(1); }
        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(3, h.Get(p).ConsecutiveFailures);
    }

    [Fact]
    public async Task Failed_notification_is_retried_on_next_failure()
    {
        using var h = new Harness();
        h.Notification.FailureThreshold = 1;
        var p = h.Add("Y", ScheduleKind.Interval, 60);
        h.Behaviour = _ => throw AuthError();
        h.Notifier.Fail = true;
        var o = (await h.Scheduler.RunDueAsync(default)).Single();
        Assert.False(o.NotificationSent);
        Assert.Contains("abgelehnt", o.NotificationError);
        Assert.Null(h.Get(p).FailureNotifiedUtc);

        h.Notifier.Fail = false;
        h.Now = h.Now.AddHours(1);
        Assert.True((await h.Scheduler.RunDueAsync(default)).Single().NotificationSent);
    }

    [Fact]
    public async Task Manual_run_failure_is_shown_but_not_mailed()
    {
        using var h = new Harness();
        h.Notification.FailureThreshold = 1;
        var p = h.Add("M", ScheduleKind.Manual);
        h.Behaviour = _ => throw AuthError();
        var o = await h.Scheduler.RunNowAsync(p, null, default);
        Assert.NotNull(o.Error);
        Assert.Empty(h.Notifier.Sent);
        Assert.Equal(0, h.Get(p).ConsecutiveFailures);
        Assert.NotNull(h.Get(p).LastError);
    }

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        using var h = new Harness();
        h.Add("Lang", ScheduleKind.Interval, 60);
        var gate = new TaskCompletionSource();
        h.Behaviour = async _ => { await gate.Task; return new ImportResult(); };
        var first = h.Scheduler.RunDueAsync(default);
        Assert.True(h.Scheduler.IsRunning);
        var second = await h.Scheduler.RunDueAsync(default);           // läuft schon -> sofort leer zurück
        Assert.Empty(second);
        gate.SetResult();
        Assert.Single(await first);
        Assert.False(h.Scheduler.IsRunning);
        Assert.Single(h.Runs);
    }

    [Fact]
    public async Task Cancelled_run_is_not_counted_as_failure()
    {
        using var h = new Harness();
        var p = h.Add("C", ScheduleKind.Interval, 60);
        using var cts = new CancellationTokenSource();
        h.Behaviour = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); };
        await h.Scheduler.RunDueAsync(cts.Token);
        Assert.Equal(0, h.Get(p).ConsecutiveFailures);
        Assert.Null(h.Get(p).LastError);
    }

    [Fact]
    public void Saving_profile_settings_keeps_status_written_by_background_run()
    {
        using var h = new Harness();
        var p = h.Add("S", ScheduleKind.Interval, 60);
        var editorCopy = h.Get(p);                                     // Editor geöffnet
        h.Store.Update(p.Id, x => { x.LastError = "Fehler"; x.ConsecutiveFailures = 3; x.LastRunUtc = h.Now; });
        editorCopy.IntervalMinutes = 15;                               // Editor speichert
        h.Store.Save(editorCopy);
        var stored = h.Get(p);
        Assert.Equal(15, stored.IntervalMinutes);
        Assert.Equal(3, stored.ConsecutiveFailures);
        Assert.Equal("Fehler", stored.LastError);
    }

    [Fact]
    public async Task Deleted_profile_is_skipped()
    {
        using var h = new Harness();
        var p = h.Add("weg", ScheduleKind.Interval, 60);
        h.Behaviour = x => { if (x.Name == "erst") h.Store.Delete(p.Id); return Task.FromResult(new ImportResult()); };
        h.Add("erst", ScheduleKind.Interval, 60, lastRun: h.Now.AddHours(-5)); // älter -> zuerst
        await h.Scheduler.RunDueAsync(default);
        Assert.Equal(new[] { "erst" }, h.Runs);
    }

    [Fact]
    public void Error_texts_are_understandable()
    {
        Assert.Contains("Anmeldung abgelehnt", ErrorText.Describe(AuthError()));
        Assert.Contains("nicht gefunden", ErrorText.Describe(new SocketException((int)SocketError.HostNotFound)));
        Assert.Contains("abgelehnt", ErrorText.Describe(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Contains("Zeitüberschreitung", ErrorText.Describe(new TimeoutException()));
        Assert.Contains("nicht gefunden", ErrorText.Describe(new FileNotFoundException("x", "C:\\a.pst")));
    }

    [Fact]
    public void Notification_settings_and_api_key_are_stored_encrypted()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "settings.json");
        var s = new AppSettings { StartWithWindows = true };
        s.Notification.Password = "smtp-geheim";
        s.Ai.ApiKey = "sk-ant-test-123";
        s.SaveTo(path);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("smtp-geheim", json);
        Assert.DoesNotContain("sk-ant-test-123", json);
        var back = AppSettings.LoadFrom(path);
        Assert.True(back.StartWithWindows);
        Assert.Equal("smtp-geheim", back.Notification.Password);
        Assert.Equal("sk-ant-test-123", back.Ai.ApiKey);
        Assert.Equal(AiModels.Default, back.Ai.Model);
    }

    [Fact]
    public void Old_settings_file_without_new_sections_loads_with_defaults()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "settings.json");
        File.WriteAllText(path, """{ "DataDirectory": "C:\\Mail", "BlockExternalContent": false }""");
        var s = AppSettings.LoadFrom(path);
        Assert.Equal("C:\\Mail", s.DataDirectory);
        Assert.False(s.Notification.Enabled);
        Assert.False(s.Web.Enabled);
        Assert.Equal(8025, s.Web.Port);
    }

    [Fact]
    public void Notification_message_has_auto_submitted_header_and_all_recipients()
    {
        var n = new SmtpNotifier(new NotificationSettings { SmtpHost = "x", From = "absender@example.org", To = "a@example.org; b@example.org" });
        var m = n.BuildMessage("Betreff", "Text");
        Assert.Equal("auto-generated", m.Headers["Auto-Submitted"]);
        Assert.Equal(2, m.To.Count);
        Assert.Throws<FormatException>(() => SmtpNotifier.ParseRecipients("kein-at-zeichen"));
    }

    [Fact]
    public void Autostart_entry_is_written_and_removed()
    {
        var key = @"Software\MailVaultTests\Run" + Guid.NewGuid().ToString("N");
        var a = new Autostart("MailVault Test", key);
        const string exe = @"C:\Programme\MailVault\MailVault.exe";
        try
        {
            Assert.False(a.IsEnabled(exe));
            a.Set(true, exe);
            Assert.True(a.IsEnabled(exe));
            Assert.Equal("\"C:\\Programme\\MailVault\\MailVault.exe\" --background", a.RegisteredCommand);
            Assert.False(a.IsEnabled(@"D:\anders\MailVault.exe"));        // verschobene EXE wird erkannt
            a.Set(false, exe);
            Assert.False(a.IsEnabled(exe));
            Assert.Null(a.RegisteredCommand);
            a.Set(false, exe);                                            // doppelt ausschalten ist ok
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\MailVaultTests", throwOnMissingSubKey: false); }
    }

    static DateTime Utc(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
