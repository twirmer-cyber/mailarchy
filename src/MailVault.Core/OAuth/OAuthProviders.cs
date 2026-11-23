using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailVault.Core.OAuth;

/// <summary>Anmeldeverfahren eines Server-Profils.</summary>
public enum MailAuth
{
    /// <summary>Benutzername und Kennwort bzw. App-Kennwort.</summary>
    Password,
    /// <summary>OAuth 2.0 mit einem Microsoft-Konto (Outlook.com, Hotmail, Microsoft 365).</summary>
    Microsoft,
    /// <summary>OAuth 2.0 mit einem Google-Konto (Gmail, Google Workspace).</summary>
    Google,
}

/// <summary>
/// Ein OAuth-Anbieter: Endpunkte, Berechtigungen und Server. Die Client-ID (und bei Google das nicht geheime
/// "Client-Secret" einer Desktop-App) stammen aus der App-Registrierung des Herausgebers und stehen nicht im Quelltext,
/// sondern in oauth-clients.json (siehe docs/OAUTH.md).
/// </summary>
public sealed record OAuthProvider
{
    public required MailAuth Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string AuthorizeEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    /// <summary>Angeforderte Berechtigungen (durch Leerzeichen getrennt). Nur Lesezugriff auf das Postfach wird genutzt.</summary>
    public required string Scope { get; init; }
    /// <summary>
    /// Mindestens eine dieser Berechtigungen muss in der Token-Antwort stehen, sonst ist nur die Anmeldung erlaubt,
    /// nicht der Postfachzugriff (Google lässt Nutzer einzelne Berechtigungen abwählen; das Gmail-Häkchen ist nicht vorausgewählt).
    /// Vergleich als Teilzeichenfolge, weil Microsoft die Berechtigungen mit Ressourcen-Präfix zurückgibt.
    /// </summary>
    public required IReadOnlyList<string> MailScopes { get; init; }
    public required string ImapHost { get; init; }
    public required string Pop3Host { get; init; }
    /// <summary>Hostname in der Rücksprungadresse: Microsoft verlangt "localhost", Google empfiehlt "127.0.0.1".</summary>
    public required string RedirectHost { get; init; }
    public IReadOnlyDictionary<string, string> ExtraAuthorizeParameters { get; init; } = new Dictionary<string, string>();
    public string ClientId { get; init; } = "";
    public string? ClientSecret { get; init; }

    public bool IsConfigured => ClientId.Length > 0;
}

/// <summary>Die beiden unterstützten Anbieter und das Laden der Client-IDs.</summary>
public static class OAuthProviders
{
    public const string ConfigFileName = "oauth-clients.json";
    const string ResourceName = "MailVault.Core.oauth-clients.json";

    static OAuthProvider _microsoft = MicrosoftTemplate;
    static OAuthProvider _google = GoogleTemplate;

    static OAuthProviders() => Configure(LoadConfig());

    public static OAuthProvider Microsoft => _microsoft;
    public static OAuthProvider Google => _google;

    static OAuthProvider MicrosoftTemplate => new()
    {
        Kind = MailAuth.Microsoft,
        DisplayName = "Microsoft",
        AuthorizeEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        // IMAP und POP3 für Outlook.com/Hotmail/Microsoft 365; offline_access liefert das Refresh-Token für den Zeitplan.
        Scope = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/POP.AccessAsUser.All offline_access openid email profile",
        MailScopes = new[] { "IMAP.AccessAsUser.All", "POP.AccessAsUser.All" },
        ImapHost = "outlook.office365.com",
        Pop3Host = "outlook.office365.com",
        RedirectHost = "localhost",
        ExtraAuthorizeParameters = new Dictionary<string, string> { ["prompt"] = "select_account" },
    };

    static OAuthProvider GoogleTemplate => new()
    {
        Kind = MailAuth.Google,
        DisplayName = "Google",
        AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth2.googleapis.com/token",
        // Für IMAP/POP3 gibt es bei Google nur diese eine Berechtigung; MailVault liest trotzdem nur (EXAMINE, BODY.PEEK).
        Scope = "https://mail.google.com/ openid email",
        MailScopes = new[] { "https://mail.google.com/" },
        ImapHost = "imap.gmail.com",
        Pop3Host = "pop.gmail.com",
        RedirectHost = "127.0.0.1",
        // offline + consent: Google liefert das Refresh-Token sonst nur bei der allerersten Zustimmung.
        ExtraAuthorizeParameters = new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent select_account" },
    };

    public static OAuthProvider? For(MailAuth kind) => kind switch
    {
        MailAuth.Microsoft => Microsoft,
        MailAuth.Google => Google,
        _ => null,
    };

    /// <summary>Schlägt anhand von Adresse oder Server einen Anbieter vor (auch für eigene Domains bei Microsoft 365/Workspace).</summary>
    public static MailAuth Suggest(string? email, string? host)
    {
        var domain = (email ?? "").Split('@') is { Length: 2 } p ? p[1].Trim().ToLowerInvariant() : "";
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (domain is "gmail.com" or "googlemail.com" || h is "imap.gmail.com" or "pop.gmail.com") return MailAuth.Google;
        if (domain is "outlook.com" or "outlook.de" or "hotmail.com" or "hotmail.de" or "live.com" or "live.de" or "msn.com"
            || h is "outlook.office365.com" or "imap-mail.outlook.com" or "pop-mail.outlook.com" or "outlook.live.com")
            return MailAuth.Microsoft;
        return MailAuth.Password;
    }

    sealed class ClientEntry
    {
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
    }

    sealed class ClientConfig
    {
        [JsonPropertyName("microsoft")] public ClientEntry? Microsoft { get; set; }
        [JsonPropertyName("google")] public ClientEntry? Google { get; set; }
    }

    /// <summary>
    /// Reihenfolge: Umgebungsvariable MAILVAULT_OAUTH_CLIENTS (Pfad), oauth-clients.json neben der MailVault.exe,
    /// beim Bauen eingebettete build/oauth-clients.local.json. Fehlt alles, ist OAuth ausgeblendet.
    /// </summary>
    static string? LoadConfig()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("MAILVAULT_OAUTH_CLIENTS") is { Length: > 0 } env && File.Exists(env))
                return File.ReadAllText(env);
            var local = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            if (File.Exists(local)) return File.ReadAllText(local);
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s is null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Übernimmt Client-IDs aus JSON (auch für Tests); ungültiges JSON lässt OAuth ausgeblendet.</summary>
    internal static void Configure(string? json)
    {
        ClientConfig? cfg = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { cfg = JsonSerializer.Deserialize<ClientConfig>(json); } catch (JsonException) { }
        }
        _microsoft = MicrosoftTemplate with { ClientId = cfg?.Microsoft?.ClientId?.Trim() ?? "", ClientSecret = Blank(cfg?.Microsoft?.ClientSecret) };
        _google = GoogleTemplate with { ClientId = cfg?.Google?.ClientId?.Trim() ?? "", ClientSecret = Blank(cfg?.Google?.ClientSecret) };
    }

    /// <summary>Nur für Tests: Endpunkte auf einen lokalen Schein-Anmeldeserver umlenken.</summary>
    internal static void OverrideForTests(OAuthProvider microsoft, OAuthProvider google)
    {
        _microsoft = microsoft;
        _google = google;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
