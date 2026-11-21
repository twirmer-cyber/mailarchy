using MailVault.Core;
using MailVault.Tests.Fakes;
using MimeKit;
using Xunit;

namespace MailVault.Tests;

/// <summary>
/// Gesamtablauf ohne Attrappen im Programmcode: echter IMAP-Abruf (gegen den Test-IMAP-Server), Zeitplaner,
/// echte SMTP-Benachrichtigung (gegen den Test-SMTP-Server).
/// </summary>
public class NotificationEndToEndTests
{
    [Fact]
    public async Task Wrong_password_triggers_mail_and_recovery_mail_after_fix_without_touching_the_mailbox()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        using var imap = new FakeImapServer();
        imap.AddMessage("INBOX", ImapReadOnlyTests.Msg("Rechnung März", "Bitte zahlen", 3));
        var before = imap.Snapshot();
        using var smtp = new FakeSmtpServer("notify@example.org", "smtp-pw");

        var store = new ProfileStore(tmp.Path);
        var profile = new ArchiveProfile
        {
            Name = "Annas Postfach", Type = ProfileType.Imap, Host = imap.Host, Port = imap.Port, Security = ConnectionSecurity.None,
            UserName = imap.UserName, Password = "falsch", Schedule = ScheduleKind.Interval, IntervalMinutes = 30,
        };
        store.Save(profile);

        var notification = new NotificationSettings
        {
            Enabled = true, SmtpHost = smtp.Host, SmtpPort = smtp.Port, Security = ConnectionSecurity.None,
            UserName = "notify@example.org", Password = "smtp-pw", To = "anna@example.org", FailureThreshold = 1,
        };
        var now = DateTime.UtcNow;
        var scheduler = new ArchiveScheduler(store, (p, prog, ct) => ImportJob.RunAsync(archive, p, prog, ct), () => notification, utcNow: () => now);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var failed = (await scheduler.RunDueAsync(cts.Token)).Single();
        Assert.Contains("Anmeldung abgelehnt", failed.Error);
        Assert.True(failed.NotificationSent, failed.NotificationError);
        var mail = MimeMessage.Load(new MemoryStream(Assert.Single(smtp.Received).Raw));
        Assert.Contains("Annas Postfach", mail.Subject);
        Assert.Equal("anna@example.org", ((MailboxAddress)mail.To[0]).Address);
        Assert.Equal("auto-generated", mail.Headers["Auto-Submitted"]);
        Assert.Contains("Anmeldung abgelehnt", mail.TextBody);

        // Kennwort korrigiert -> nächster Lauf archiviert und schickt Entwarnung
        store.Save(store.Get(profile.Id)!.Also(p => p.Password = imap.Password));
        now = now.AddMinutes(31);
        var ok = (await scheduler.RunDueAsync(cts.Token)).Single();
        Assert.Null(ok.Error);
        Assert.Equal(1, ok.Result!.Archived);
        Assert.True(ok.NotificationSent);
        Assert.Equal(2, smtp.Received.Count);
        Assert.Contains("wieder archiviert", MimeMessage.Load(new MemoryStream(smtp.Received[1].Raw)).Subject);

        Assert.Equal(before, imap.Snapshot());       // Postfach unverändert
        Assert.Empty(imap.MutatingCommands);
    }

    [Fact]
    public async Task Smtp_rejecting_login_is_reported_as_notification_error()
    {
        using var smtp = new FakeSmtpServer("notify@example.org", "smtp-pw") { RejectAuth = true };
        var s = new NotificationSettings { Enabled = true, SmtpHost = smtp.Host, SmtpPort = smtp.Port, Security = ConnectionSecurity.None, UserName = "notify@example.org", Password = "x", To = "a@example.org" };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ArchiveScheduler.SendTestAsync(s, default));
        Assert.Contains("Anmeldung abgelehnt", ErrorText.Describe(ex));
        Assert.Empty(smtp.Received);
    }

    [Fact]
    public async Task Test_message_is_delivered()
    {
        using var smtp = new FakeSmtpServer();
        var s = new NotificationSettings { SmtpHost = smtp.Host, SmtpPort = smtp.Port, Security = ConnectionSecurity.None, From = "pc@example.org", To = "a@example.org, b@example.org" };
        await ArchiveScheduler.SendTestAsync(s, default);
        var got = Assert.Single(smtp.Received);
        Assert.Equal(new[] { "a@example.org", "b@example.org" }, got.Recipients);
    }
}

static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> change) { change(value); return value; }
}
