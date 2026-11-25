using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Xml.Linq;
using DnsClient;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Ermittelte Servereinstellungen für ein Postfach.</summary>
public sealed record MailServerSettings(string Host, int Port, ConnectionSecurity Security, string UserName, bool PasswordAllowed);

public sealed record AutoConfigResult(
    MailServerSettings? Imap,
    MailServerSettings? Pop3,
    /// <summary>Woher die Einstellungen stammen (für die Anzeige).</summary>
    string Source,
    /// <summary>Hinweis, z. B. auf ein nötiges App-Kennwort.</summary>
    string? Hint,
    /// <summary>Postausgangsserver (für Benachrichtigungen per E-Mail).</summary>
    MailServerSettings? Smtp = null);

/// <summary>
/// Automatische Einrichtung von Postfächern, nach dem Verfahren von Thunderbird:
/// 1. Konfigurationsdatei des Anbieters (autoconfig.&lt;domain&gt;, .well-known),
/// 2. Mozilla-Anbieterdatenbank ISPDB (autoconfig.thunderbird.net, erhält nur die Domain),
/// 3. MX-Eintrag der Domain -> ISPDB für den Mail-Hoster (eigene Domain bei Google, Microsoft, IONOS ...),
/// 4. SRV-Einträge nach RFC 6186 (_imaps._tcp / _pop3s._tcp),
/// 5. Verbindungsversuche zu üblichen Servernamen (imap.&lt;domain&gt;, mail.&lt;domain&gt; ...).
/// Die vollständige E-Mail-Adresse wird an niemanden übertragen.
/// </summary>
public static class AutoConfig
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "MailArchy-Autoconfig/1.0" } },
    };

    public static async Task<AutoConfigResult?> DiscoverAsync(string email, CancellationToken ct = default)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return null;
        var local = email[..at].Trim();
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();

        // 1 + 2: Anbieter-Datei und ISPDB parallel abfragen, Anbieter-Datei hat Vorrang
        var provider = FetchConfigAsync($"https://autoconfig.{domain}/mail/config-v1.1.xml", ct);
        var wellKnown = FetchConfigAsync($"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml", ct);
        var ispdb = FetchConfigAsync($"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(domain)}", ct);
        foreach (var (task, source) in new[] { (provider, CoreText.F("AutoCfg_Provider", domain)), (wellKnown, CoreText.F("AutoCfg_Provider", domain)), (ispdb, CoreText.T("AutoCfg_Ispdb")) })
        {
            var xml = await task;
            if (xml is not null && Parse(xml, email, local, domain, source) is { } r) return WithHint(r, domain);
        }

        // 3: MX -> ISPDB für den Hoster
        var dns = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4), Retries = 1, UseCache = true });
        try
        {
            var mx = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            foreach (var host in mx.Answers.MxRecords().OrderBy(m => m.Preference).Select(m => m.Exchange.Value.TrimEnd('.').ToLowerInvariant()))
            {
                var baseDomain = BaseDomain(host);
                if (baseDomain == domain) continue;
                var xml = await FetchConfigAsync($"https://autoconfig.thunderbird.net/v1.1/{Uri.EscapeDataString(baseDomain)}", ct);
                if (xml is not null && Parse(xml, email, local, domain, CoreText.F("AutoCfg_IspdbMx", baseDomain)) is { } r)
                    return WithHint(r, baseDomain);
                break; // nur der bevorzugte Mailserver
            }
        }
        catch (DnsResponseException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        // 4: SRV-Einträge (RFC 6186)
        try
        {
            MailServerSettings? imap = null, pop = null;
            foreach (var (name, isImap, sec) in new[] { ("_imaps._tcp.", true, ConnectionSecurity.SslOnConnect), ("_imap._tcp.", true, ConnectionSecurity.StartTls),
                                                         ("_pop3s._tcp.", false, ConnectionSecurity.SslOnConnect), ("_pop3._tcp.", false, ConnectionSecurity.StartTls) })
            {
                var srv = (await dns.QueryAsync(name + domain, QueryType.SRV, cancellationToken: ct)).Answers.SrvRecords()
                    .Where(s => s.Target.Value != ".").OrderBy(s => s.Priority).FirstOrDefault();
                if (srv is null) continue;
                var settings = new MailServerSettings(srv.Target.Value.TrimEnd('.'), srv.Port, sec, email, true);
                if (isImap) imap ??= settings; else pop ??= settings;
            }
            if (imap is not null || pop is not null)
            {
                MailServerSettings? smtp = null;
                foreach (var (name, sec) in new[] { ("_submissions._tcp.", ConnectionSecurity.SslOnConnect), ("_submission._tcp.", ConnectionSecurity.StartTls) })
                {
                    var srv = (await dns.QueryAsync(name + domain, QueryType.SRV, cancellationToken: ct)).Answers.SrvRecords()
                        .Where(s => s.Target.Value != ".").OrderBy(s => s.Priority).FirstOrDefault();
                    if (srv is not null) { smtp = new MailServerSettings(srv.Target.Value.TrimEnd('.'), srv.Port, sec, email, true); break; }
                }
                return new AutoConfigResult(imap, pop, CoreText.T("AutoCfg_Dns"), null, smtp);
            }
        }
        catch (DnsResponseException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        // 5: übliche Servernamen ausprobieren
        var guesses = new[] { $"imap.{domain}", $"mail.{domain}", $"imap.mail.{domain}", domain };
        var probes = guesses.Select(h => (Host: h, Task: ProbeTlsAsync(h, 993, ct))).ToList();
        foreach (var (host, task) in probes)
            if (await task)
            {
                var popHost = host.StartsWith("imap.") ? "pop." + host[5..] : host;
                var pop = await ProbeTlsAsync(popHost, 995, ct) ? new MailServerSettings(popHost, 995, ConnectionSecurity.SslOnConnect, email, true) : null;
                var smtpHost = host.StartsWith("imap.") ? "smtp." + host[5..] : host;
                var smtp = await ProbeTlsAsync(smtpHost, 465, ct) ? new MailServerSettings(smtpHost, 465, ConnectionSecurity.SslOnConnect, email, true) : null;
                return new AutoConfigResult(new MailServerSettings(host, 993, ConnectionSecurity.SslOnConnect, email, true), pop,
                    CoreText.T("AutoCfg_Guess"), CoreText.T("AutoCfg_GuessHint"), smtp);
            }
        return null;
    }

    static async Task<string?> FetchConfigAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            return text.Contains("<clientConfig", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Wertet eine Thunderbird-Autoconfig-Datei (clientConfig 1.1) aus.</summary>
    internal static AutoConfigResult? Parse(string xml, string email, string local, string domain, string source)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        MailServerSettings? Pick(string type, string element = "incomingServer")
        {
            var servers = doc.Descendants(element).Where(e => (string?)e.Attribute("type") == type)
                .Select(e =>
                {
                    var host = Fill((string?)e.Element("hostname"));
                    var port = int.TryParse((string?)e.Element("port"), out var p) ? p : 0;
                    var socket = ((string?)e.Element("socketType") ?? "").ToUpperInvariant();
                    var auths = e.Elements("authentication").Select(a => a.Value.Trim()).ToList();
                    var passwordOk = auths.Count == 0 || auths.Any(a => a.StartsWith("password", StringComparison.OrdinalIgnoreCase));
                    var sec = socket switch { "SSL" => ConnectionSecurity.SslOnConnect, "STARTTLS" => ConnectionSecurity.StartTls, "PLAIN" => ConnectionSecurity.None, _ => ConnectionSecurity.Auto };
                    return (Settings: string.IsNullOrEmpty(host) ? null : new MailServerSettings(host, port, sec, Fill((string?)e.Element("username")) is { Length: > 0 } u ? u : email, passwordOk), sec);
                })
                .Where(x => x.Settings is not null)
                .ToList();
            // Verschlüsselte Verbindung bevorzugen, dann Kennwort-Anmeldung
            return servers.OrderBy(x => x.sec == ConnectionSecurity.None).ThenBy(x => !x.Settings!.PasswordAllowed).Select(x => x.Settings).FirstOrDefault();
        }

        string Fill(string? s) => (s ?? "").Trim()
            .Replace("%EMAILADDRESS%", email, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILLOCALPART%", local, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILDOMAIN%", domain, StringComparison.OrdinalIgnoreCase);

        var imap = Pick("imap");
        var pop = Pick("pop3");
        var smtp = Pick("smtp", "outgoingServer");
        if (imap is null && pop is null && smtp is null) return null;
        var oauthOnly = (imap ?? pop ?? smtp)!.PasswordAllowed == false;
        return new AutoConfigResult(imap, pop, source,
            oauthOnly ? CoreText.T("AutoCfg_OAuthOnly") : null, smtp);
    }

    /// <summary>Bekannte Besonderheiten großer Anbieter.</summary>
    static AutoConfigResult WithHint(AutoConfigResult r, string domain)
    {
        var key = domain switch
        {
            "gmail.com" or "googlemail.com" or "google.com" => "AutoCfg_Hint_Google",
            "outlook.com" or "hotmail.com" or "hotmail.de" or "live.com" or "live.de" or "msn.com" or "outlook.de" or "office365.com" or "outlook.office365.com" =>
                "AutoCfg_Hint_Microsoft",
            "icloud.com" or "me.com" or "mac.com" => "AutoCfg_Hint_ICloud",
            "yahoo.com" or "yahoo.de" or "ymail.com" => "AutoCfg_Hint_Yahoo",
            "gmx.de" or "gmx.net" or "gmx.at" or "gmx.ch" or "web.de" => "AutoCfg_Hint_Gmx",
            _ => null,
        };
        // Mit eingerichteter OAuth-Anmeldung ist der Weg über Microsoft/Google der einfachste.
        if (key == "AutoCfg_Hint_Microsoft" && OAuth.OAuthProviders.Microsoft.IsConfigured) key = "AutoCfg_Hint_MicrosoftOAuth";
        else if (key == "AutoCfg_Hint_Google" && OAuth.OAuthProviders.Google.IsConfigured) key = "AutoCfg_Hint_GoogleOAuth";
        var hint = key is null ? null : CoreText.T(key);
        if (hint is null) return r;
        // Ein spezifischer Anbieterhinweis ersetzt den allgemeinen OAuth-Hinweis aus der Konfigurationsdatei.
        return r with { Hint = r.Hint is null || key is "AutoCfg_Hint_Microsoft" or "AutoCfg_Hint_MicrosoftOAuth" ? hint : r.Hint + " " + hint };
    }

    /// <summary>"mx01.ionos.de" -> "ionos.de", "aspmx.l.google.com" -> "google.com" (einfache Heuristik inkl. co.uk usw.).</summary>
    internal static string BaseDomain(string host)
    {
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return host;
        var twoLevel = parts[^2] is "co" or "com" or "net" or "org" or "gov" or "ac" or "or" or "ne" && parts[^1].Length == 2;
        return string.Join('.', parts[^(twoLevel ? 3 : 2)..]);
    }

    static async Task<bool> ProbeTlsAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
