using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MailVault.Core.Localization;

namespace MailVault.Core.OAuth;

/// <summary>Fehler bei der OAuth-Anmeldung; die Meldung ist bereits verständlich formuliert.</summary>
public sealed class OAuthException : Exception
{
    /// <summary>Die gespeicherte Anmeldung gilt nicht mehr (widerrufen, abgelaufen, Kennwort geändert): neu anmelden.</summary>
    public bool ReauthRequired { get; }
    public OAuthException(string message, bool reauthRequired = false, Exception? inner = null) : base(message, inner) => ReauthRequired = reauthRequired;
}

/// <summary>Ergebnis einer Anmeldung bzw. Token-Erneuerung.</summary>
public sealed record OAuthTokens(string AccessToken, DateTime ExpiresUtc, string? RefreshToken, string? Account);

/// <summary>
/// OAuth 2.0 für Desktop-Apps nach RFC 8252: Anmeldung im Standardbrowser, Rücksprung an einen nur lokal erreichbaren
/// Port (Loopback), Absicherung mit PKCE (RFC 7636) und einem zufälligen state-Wert. MailVault sieht nie das Kennwort
/// des Kontos, nur ein Zugriffstoken für IMAP/POP3.
/// </summary>
public sealed class OAuthClient
{
    readonly HttpClient _http;

    public OAuthClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ------------------------------------------------------------------ PKCE und Adresse

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>Zufälliger Prüfwert (43 Zeichen) und seine SHA-256-Ableitung für die Anmeldeadresse.</summary>
    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string BuildAuthorizeUrl(OAuthProvider p, string redirectUri, string state, string challenge, string? loginHint)
    {
        var q = new List<KeyValuePair<string, string>>
        {
            new("client_id", p.ClientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri),
            new("scope", p.Scope),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        q.AddRange(p.ExtraAuthorizeParameters);
        if (!string.IsNullOrWhiteSpace(loginHint)) q.Add(new("login_hint", loginHint.Trim()));
        return p.AuthorizeEndpoint + (p.AuthorizeEndpoint.Contains('?') ? "&" : "?") +
               string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    // ------------------------------------------------------------------ Anmeldung

    /// <summary>
    /// Öffnet die Anmeldeseite des Anbieters (über <paramref name="openBrowser"/>), wartet auf den Rücksprung und tauscht
    /// den Code gegen Tokens. Abbruch über <paramref name="ct"/> (z. B. Abbrechen-Knopf oder Zeitlimit).
    /// </summary>
    public async Task<OAuthTokens> SignInAsync(OAuthProvider p, string? loginHint, Action<string> openBrowser, CancellationToken ct)
    {
        if (!p.IsConfigured) throw new OAuthException(CoreText.F("OAuth_NotConfigured", p.DisplayName));
        using var listener = LoopbackListener.Start();
        var redirectUri = $"http://{p.RedirectHost}:{listener.Port}/";
        var (verifier, challenge) = CreatePkce();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        openBrowser(BuildAuthorizeUrl(p, redirectUri, state, challenge, loginHint));

        var query = await listener.WaitForCallbackAsync(ct);
        string? error = query.GetValueOrDefault("error");
        string page;
        try
        {
            if (error is not null)
                throw new OAuthException(error == "access_denied" ? CoreText.T("OAuth_Denied")
                    : CoreText.F("OAuth_ProviderError", query.GetValueOrDefault("error_description") ?? error));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(query.GetValueOrDefault("state") ?? ""), Encoding.UTF8.GetBytes(state)))
                throw new OAuthException(CoreText.T("OAuth_StateMismatch"));
            var code = query.GetValueOrDefault("code");
            if (string.IsNullOrEmpty(code)) throw new OAuthException(CoreText.T("OAuth_NoCode"));

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = p.ClientId,
                ["code_verifier"] = verifier,
            };
            if (p.ClientSecret is not null) form["client_secret"] = p.ClientSecret;
            var tokens = await PostTokenAsync(p, form, ct);
            if (tokens.RefreshToken is null) throw new OAuthException(CoreText.T("OAuth_NoRefreshToken"));
            page = CoreText.T("OAuth_BrowserDone");
            await listener.RespondAsync(page, ct);
            return tokens;
        }
        catch (OAuthException ex)
        {
            try { await listener.RespondAsync(CoreText.F("OAuth_BrowserFailed", ex.Message), ct); } catch (IOException) { } catch (SocketException) { }
            throw;
        }
    }

    /// <summary>Holt mit dem Refresh-Token ein neues Zugriffstoken (für jeden Abruf, auch im Hintergrund).</summary>
    public async Task<OAuthTokens> RefreshAsync(OAuthProvider p, string refreshToken, CancellationToken ct)
    {
        // Behebt sich nicht von selbst (Client-ID entfernt) -> wie eine abgelaufene Anmeldung sofort melden.
        if (!p.IsConfigured) throw new OAuthException(CoreText.F("OAuth_NotConfigured", p.DisplayName), reauthRequired: true);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = p.ClientId,
        };
        if (p.Kind == MailAuth.Microsoft) form["scope"] = p.Scope;
        if (p.ClientSecret is not null) form["client_secret"] = p.ClientSecret;
        var t = await PostTokenAsync(p, form, ct);
        // Google schickt beim Erneuern kein neues Refresh-Token, Microsoft meist schon (das alte bleibt dann trotzdem gültig).
        return t with { RefreshToken = t.RefreshToken ?? refreshToken };
    }

    async Task<OAuthTokens> PostTokenAsync(OAuthProvider p, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.PostAsync(p.TokenEndpoint, new FormUrlEncodedContent(form), ct); }
        catch (HttpRequestException ex) { throw new OAuthException(CoreText.F("OAuth_Network", ex.Message), inner: ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new OAuthException(CoreText.T("OAuth_Timeout"), inner: ex); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try { root = JsonDocument.Parse(body).RootElement; }
            catch (JsonException) { throw new OAuthException(CoreText.F("OAuth_ProviderError", $"HTTP {(int)resp.StatusCode}")); }

            if (!resp.IsSuccessStatusCode || root.TryGetProperty("error", out _))
            {
                var err = Str(root, "error") ?? $"HTTP {(int)resp.StatusCode}";
                var desc = Str(root, "error_description");
                // invalid_grant: Refresh-Token widerrufen/abgelaufen (z. B. Kennwort geändert, Zugriff entzogen)
                if (err == "invalid_grant") throw new OAuthException(CoreText.F("OAuth_Reauth", p.DisplayName), reauthRequired: true);
                // Client-ID/-Secret falsch, gelöscht oder nicht passend zum gespeicherten Token: braucht Eingriff des Benutzers.
                if (err is "invalid_client" or "unauthorized_client")
                    throw new OAuthException(CoreText.F("OAuth_ClientRejected", p.DisplayName, desc is null ? err : $"{err}: {FirstLine(desc)}"), reauthRequired: true);
                throw new OAuthException(CoreText.F("OAuth_ProviderError", desc is null ? err : $"{err}: {FirstLine(desc)}"));
            }
            var access = Str(root, "access_token") ?? throw new OAuthException(CoreText.F("OAuth_ProviderError", "access_token"));
            // Nur die Anmeldung erlaubt, nicht das Postfach (z. B. Gmail-Häkchen nicht gesetzt): klar melden statt später
            // "Authentication failed" vom IMAP-Server. Fehlt das Feld "scope", gilt die angeforderte Berechtigung.
            if (Str(root, "scope") is { } granted && !p.MailScopes.Any(s => granted.Contains(s, StringComparison.OrdinalIgnoreCase)))
                throw new OAuthException(CoreText.F("OAuth_MailScopeMissing", p.DisplayName), reauthRequired: true);
            var expires = root.TryGetProperty("expires_in", out var e) && (e.ValueKind == JsonValueKind.Number ? e.TryGetInt32(out var n) : int.TryParse(e.GetString(), out n))
                ? n : 3600;
            return new OAuthTokens(access, DateTime.UtcNow.AddSeconds(Math.Max(60, expires)), Str(root, "refresh_token"), AccountFromIdToken(Str(root, "id_token")));
        }
    }

    static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string FirstLine(string s) => s.Split('\n')[0].Trim();

    /// <summary>
    /// E-Mail-Adresse aus dem ID-Token. Die Signatur wird nicht geprüft: Das Token kommt direkt per HTTPS vom
    /// Token-Endpunkt und dient nur zum Vorausfüllen der Adresse, nicht zur Zugriffsentscheidung.
    /// </summary>
    internal static string? AccountFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var root = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
            return Str(root, "email") ?? Str(root, "preferred_username");
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}

/// <summary>
/// Nimmt genau einen Rücksprung des Browsers entgegen. Lauscht nur auf der Loopback-Adresse (IPv4 und, wenn möglich,
/// IPv6 auf demselben Port), damit "localhost" in jedem Browser funktioniert und niemand von außen zugreifen kann.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    readonly TcpListener _v4;
    readonly TcpListener? _v6;
    TcpClient? _client;
    public int Port { get; }

    LoopbackListener(TcpListener v4, TcpListener? v6, int port) { _v4 = v4; _v6 = v6; Port = port; }

    public static LoopbackListener Start()
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        var port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener? v6 = null;
        if (Socket.OSSupportsIPv6)
        {
            try { v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); }
            catch (SocketException) { v6 = null; }
        }
        return new LoopbackListener(v4, v6, port);
    }

    /// <summary>
    /// Wartet auf "GET /?code=…&amp;state=…" oder (response_mode=form_post) "POST /" mit Formular-Daten;
    /// andere Anfragen (z. B. favicon.ico) werden mit 404 beantwortet.
    /// </summary>
    public async Task<Dictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        while (true)
        {
            var accepts = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(ct).AsTask() };
            if (_v6 is not null) accepts.Add(_v6.AcceptTcpClientAsync(ct).AsTask());
            Task<TcpClient> done;
            try { done = await Task.WhenAny(accepts); }
            catch (OperationCanceledException) { throw; }
            ct.ThrowIfCancellationRequested();
            var client = await done;
            var req = await ReadRequestAsync(client, ct);
            Dictionary<string, string>? values = null;
            if (req is { Method: "GET" } g && g.Target.IndexOf('?') is var q and >= 0 && g.Target[..q] == "/")
                values = ParseQuery(g.Target[(q + 1)..]);
            else if (req is { Method: "POST", Target: "/" } p)
                values = ParseQuery(p.Body);
            if (values is null || (!values.ContainsKey("code") && !values.ContainsKey("error")))
            {
                try { await WriteAsync(client, "404 Not Found", "", ct); } catch (IOException) { }
                client.Dispose();
                continue;
            }
            _client = client;
            return values;
        }
    }

    public Task RespondAsync(string message, CancellationToken ct)
    {
        if (_client is null) return Task.CompletedTask;
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>MailArchy</title></head>" +
                   "<body style=\"font-family:Segoe UI,system-ui,sans-serif;margin:48px;color:#1a1f26\"><h1 style=\"font-size:22px\">MailArchy</h1><p style=\"font-size:16px;max-width:36em\">" +
                   WebUtility.HtmlEncode(message) + "</p></body></html>";
        return WriteAsync(_client, "200 OK", html, ct);
    }

    internal sealed record Request(string Method, string Target, string Body);

    /// <summary>Liest Anfragezeile, Kopfzeilen und (bei POST) den Inhalt laut Content-Length – höchstens 64 KB.</summary>
    internal static async Task<Request?> ReadRequestAsync(TcpClient client, CancellationToken ct)
    {
        const int Max = 64 * 1024;
        var stream = client.GetStream();
        var buf = new byte[Max];
        var len = 0;
        int headerEnd = -1, contentLength = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (len < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(len), timeout.Token);
                if (n == 0) break;
                len += n;
                if (headerEnd < 0)
                {
                    var text = Encoding.ASCII.GetString(buf, 0, len);
                    var i = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (i < 0) continue;
                    headerEnd = i + 4;
                    foreach (var line in text[..i].Split("\r\n").Skip(1))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(line[15..].Trim(), out var cl) && cl is >= 0 and <= Max)
                            contentLength = cl;
                }
                if (headerEnd >= 0 && len >= headerEnd + contentLength) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
        if (headerEnd < 0) return null;
        var first = Encoding.ASCII.GetString(buf, 0, headerEnd).Split("\r\n")[0].Split(' ');
        if (first.Length < 2) return null;
        var body = Encoding.ASCII.GetString(buf, headerEnd, Math.Min(contentLength, len - headerEnd));
        return new Request(first[0], first[1], body);
    }

    static async Task WriteAsync(TcpClient client, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                                           "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        var s = client.GetStream();
        await s.WriteAsync(head, ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            result.TryAdd(k, v);
        }
        return result;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _v4.Stop();
        _v6?.Stop();
    }
}
