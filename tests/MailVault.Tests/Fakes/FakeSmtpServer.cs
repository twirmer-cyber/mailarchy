using System.Text;

namespace MailVault.Tests.Fakes;

/// <summary>Eine vom Test-SMTP-Server angenommene Nachricht.</summary>
public sealed record SmtpCapture(string From, IReadOnlyList<string> Recipients, byte[] Raw);

/// <summary>
/// Minimaler ESMTP-Server (RFC 5321) im Prozess, nur Klartext. AUTH PLAIN/LOGIN optional, DATA mit Dot-Unstuffing.
/// Ohne konfigurierte Zugangsdaten wird kein AUTH angeboten.
/// </summary>
public sealed class FakeSmtpServer : FakeServerBase
{
    readonly List<SmtpCapture> _received = new();

    /// <summary>Zugangsdaten; null = kein AUTH anbieten.</summary>
    public string? UserName { get; set; }
    public string? Password { get; set; }
    /// <summary>Jede Anmeldung mit 535 ablehnen.</summary>
    public bool RejectAuth { get; set; }
    /// <summary>MAIL FROM nur nach erfolgreicher Anmeldung (Standard, sobald Zugangsdaten gesetzt sind).</summary>
    public bool RequireAuth { get; set; } = true;

    public FakeSmtpServer(string? userName = null, string? password = null)
    {
        UserName = userName;
        Password = password;
        Start();
    }

    public IReadOnlyList<SmtpCapture> Received { get { lock (_received) return _received.ToList(); } }

    bool AuthOffered => UserName is not null;

    bool CheckCredentials(string user, string pass) => !RejectAuth && user == UserName && pass == Password;

    internal override async Task HandleClientAsync(LineStream io, CancellationToken ct)
    {
        var authed = false;
        string? from = null;
        var rcpts = new List<string>();
        async Task Reply(string text) => await io.WriteLineAsync(text, ct);
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        static string UnB64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

        await Reply("220 fake.smtp.local ESMTP FakeSMTP ready");
        while (!ct.IsCancellationRequested)
        {
            var line = await io.ReadTextLineAsync(ct);
            if (line is null) return;
            var verb = (line.Split(' ', 2)[0]).ToUpperInvariant();
            Record(verb == "AUTH" ? string.Join(' ', line.Split(' ').Take(2)) + " ****" : line);
            var arg = line.Length > verb.Length ? line[(verb.Length + 1)..] : "";

            switch (verb)
            {
                case "EHLO":
                    await Reply("250-fake.smtp.local Hello " + arg);
                    if (AuthOffered) await Reply("250-AUTH PLAIN LOGIN");
                    await Reply("250-8BITMIME");
                    await Reply("250 SIZE 10485760");
                    break;
                case "HELO":
                    await Reply("250 fake.smtp.local");
                    break;
                case "AUTH":
                {
                    if (!AuthOffered) { await Reply("502 5.5.1 AUTH not supported"); break; }
                    if (authed) { await Reply("503 5.5.1 Already authenticated"); break; }
                    var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var mech = a.Length > 0 ? a[0].ToUpperInvariant() : "";
                    try
                    {
                        string user, pass;
                        if (mech == "PLAIN")
                        {
                            var ir = a.Length > 1 ? a[1] : null;
                            if (ir is null) { await Reply("334 "); ir = await io.ReadTextLineAsync(ct) ?? "*"; }
                            if (ir == "*") { await Reply("501 5.0.0 Authentication cancelled"); break; }
                            var p = UnB64(ir).Split('\0');
                            (user, pass) = p.Length == 3 ? (p[1], p[2]) : ("", "");
                        }
                        else if (mech == "LOGIN")
                        {
                            var u = a.Length > 1 ? a[1] : null;
                            if (u is null) { await Reply("334 " + B64("Username:")); u = await io.ReadTextLineAsync(ct) ?? "*"; }
                            if (u == "*") { await Reply("501 5.0.0 Authentication cancelled"); break; }
                            await Reply("334 " + B64("Password:"));
                            var pw = await io.ReadTextLineAsync(ct) ?? "*";
                            if (pw == "*") { await Reply("501 5.0.0 Authentication cancelled"); break; }
                            (user, pass) = (UnB64(u), UnB64(pw));
                        }
                        else { await Reply("504 5.5.4 Unrecognized authentication type"); break; }

                        if (CheckCredentials(user, pass)) { authed = true; await Reply("235 2.7.0 Authentication successful"); }
                        else await Reply("535 5.7.8 Authentication credentials invalid");
                    }
                    catch (FormatException) { await Reply("501 5.5.2 Cannot decode response"); }
                    break;
                }
                case "MAIL":
                    if (AuthOffered && RequireAuth && !authed) { await Reply("530 5.7.0 Authentication required"); break; }
                    from = ExtractPath(arg);
                    rcpts.Clear();
                    await Reply("250 2.1.0 Sender OK");
                    break;
                case "RCPT":
                    if (from is null) { await Reply("503 5.5.1 Need MAIL first"); break; }
                    rcpts.Add(ExtractPath(arg));
                    await Reply("250 2.1.5 Recipient OK");
                    break;
                case "DATA":
                {
                    if (from is null || rcpts.Count == 0) { await Reply("503 5.5.1 Need RCPT first"); break; }
                    await Reply("354 End data with <CR><LF>.<CR><LF>");
                    var ms = new MemoryStream();
                    while (true)
                    {
                        var dl = await io.ReadLineAsync(ct) ?? throw new EndOfStreamException();
                        if (dl.Length == 1 && dl[0] == '.') break;
                        var start = dl.Length > 0 && dl[0] == '.' ? 1 : 0;
                        ms.Write(dl, start, dl.Length - start);
                        ms.Write("\r\n"u8);
                    }
                    lock (_received) _received.Add(new SmtpCapture(from, rcpts.ToList(), ms.ToArray()));
                    from = null;
                    rcpts.Clear();
                    await Reply("250 2.0.0 OK queued");
                    break;
                }
                case "RSET":
                    from = null;
                    rcpts.Clear();
                    await Reply("250 2.0.0 OK");
                    break;
                case "NOOP":
                    await Reply("250 2.0.0 OK");
                    break;
                case "QUIT":
                    await Reply("221 2.0.0 Bye");
                    return;
                default:
                    await Reply("500 5.5.2 Command not recognized");
                    break;
            }
        }
    }

    /// <summary>"FROM:&lt;a@b&gt; SIZE=123" → "a@b".</summary>
    static string ExtractPath(string arg)
    {
        var lt = arg.IndexOf('<');
        var gt = arg.IndexOf('>', lt + 1);
        if (lt >= 0 && gt > lt) return arg[(lt + 1)..gt];
        var colon = arg.IndexOf(':');
        return arg[(colon + 1)..].Split(' ')[0].Trim();
    }
}
