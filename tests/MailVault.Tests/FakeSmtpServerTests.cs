using MailKit.Net.Smtp;
using MailKit.Security;
using MailVault.Tests.Fakes;
using MimeKit;
using Xunit;

namespace MailVault.Tests;

/// <summary>Selbsttest des Test-SMTP-Servers mit MailKits SmtpClient.</summary>
public class FakeSmtpServerTests
{
    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    static MimeMessage Notification()
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse("MailVault <mailvault@example.org>"));
        m.To.Add(MailboxAddress.Parse("anna@example.org"));
        m.Subject = "Archivierung abgeschlossen";
        m.Body = new TextPart("plain") { Text = "7 Nachrichten archiviert.\r\n.Zeile mit Punkt\r\nEnde" };
        return m;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Captures_message_sent_by_MailKit(bool withAuth)
    {
        using var server = withAuth ? new FakeSmtpServer("mailer", "pw") : new FakeSmtpServer();
        var ct = Timeout();
        using (var client = new SmtpClient())
        {
            await client.ConnectAsync(server.Host, server.Port, SecureSocketOptions.None, ct);
            if (withAuth) await client.AuthenticateAsync("mailer", "pw", ct);
            await client.SendAsync(Notification(), ct);
            await client.DisconnectAsync(true, ct);
        }

        var got = Assert.Single(server.Received);
        Assert.Equal("mailvault@example.org", got.From);
        Assert.Equal(new[] { "anna@example.org" }, got.Recipients);
        var parsed = MimeMessage.Load(new MemoryStream(got.Raw));
        Assert.Equal("Archivierung abgeschlossen", parsed.Subject);
        Assert.Contains("\r\n.Zeile mit Punkt\r\n", parsed.TextBody); // Dot-Unstuffing korrekt
        Assert.Equal(withAuth, server.Commands.Any(c => c.StartsWith("AUTH", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rejected_auth_throws_AuthenticationException()
    {
        using var server = new FakeSmtpServer("mailer", "pw") { RejectAuth = true };
        var ct = Timeout();
        using var client = new SmtpClient();
        await client.ConnectAsync(server.Host, server.Port, SecureSocketOptions.None, ct);
        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("mailer", "pw", ct));
        Assert.Empty(server.Received);
    }
}
