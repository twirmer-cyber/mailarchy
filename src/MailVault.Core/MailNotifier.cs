using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Versendet Benachrichtigungen (z. B. "Postfach konnte nicht abgerufen werden").</summary>
public interface INotifier
{
    Task SendAsync(string subject, string body, CancellationToken ct);
}

/// <summary>Versand über einen SMTP-Server (in der Regel der Postausgangsserver des eigenen Postfachs).</summary>
public sealed class SmtpNotifier : INotifier
{
    readonly NotificationSettings _s;
    public SmtpNotifier(NotificationSettings settings) => _s = settings;

    public static IReadOnlyList<MailboxAddress> ParseRecipients(string text) =>
        text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => MailboxAddress.TryParse(a, out var m) && m.Address.Contains('@') ? m : throw new FormatException(CoreText.F("Notify_InvalidRecipient", a)))
            .ToList();

    public MimeMessage BuildMessage(string subject, string body)
    {
        var from = string.IsNullOrWhiteSpace(_s.From) ? _s.UserName : _s.From;
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("MailArchy", MailboxAddress.Parse(from).Address));
        msg.To.AddRange(ParseRecipients(_s.To));
        msg.Subject = subject;
        msg.Headers.Add("Auto-Submitted", "auto-generated"); // RFC 3834: keine Abwesenheitsnotizen zurückschicken
        msg.Headers.Add("X-Mailer", "MailArchy");
        msg.Body = new TextPart("plain") { Text = body };
        return msg;
    }

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        if (!_s.IsConfigured) throw new InvalidOperationException(CoreText.T("Notify_NotConfigured"));
        var msg = BuildMessage(subject, body);
        using var client = new SmtpClient { Timeout = 30_000 };
        if (_s.AcceptInvalidCertificates) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var (port, opts) = _s.Security switch
        {
            ConnectionSecurity.SslOnConnect => (_s.SmtpPort > 0 ? _s.SmtpPort : 465, SecureSocketOptions.SslOnConnect),
            ConnectionSecurity.StartTls => (_s.SmtpPort > 0 ? _s.SmtpPort : 587, SecureSocketOptions.StartTls),
            ConnectionSecurity.None => (_s.SmtpPort > 0 ? _s.SmtpPort : 25, SecureSocketOptions.None),
            _ => (_s.SmtpPort > 0 ? _s.SmtpPort : 587, SecureSocketOptions.Auto),
        };
        await client.ConnectAsync(_s.SmtpHost.Trim(), port, opts, ct);
        if (!string.IsNullOrEmpty(_s.UserName) && client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            await client.AuthenticateAsync(_s.UserName, _s.Password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}
