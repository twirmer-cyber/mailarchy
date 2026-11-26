using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailVault.Core;
using MailVault.Core.OAuth;
using MailVault.Core.Sources;
using MailVault.Tests.Fakes;
using Xunit;

namespace MailVault.Tests;

/// <summary>Tests, die <see cref="OAuthService.Current"/> setzen, laufen nacheinander.</summary>
[CollectionDefinition("OAuth", DisableParallelization = true)]
public sealed class OAuthCollection;

/// <summary>
/// OAuth-Anmeldung ohne echten Anbieter: Der Token-Endpunkt ist ein Schein-Handler, der "Browser" ruft die
/// Rücksprungadresse wirklich per HTTP auf. So werden PKCE, state, Loopback, Token-Erneuerung und XOAUTH2 an einem
/// echten (im Prozess laufenden) IMAP- bzw. POP3-Server geprüft.
/// </summary>
[Collection("OAuth")]
public class OAuthTests
{
    static CancellationToken Timeout(int seconds = 15) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    static OAuthProvider Ms(string clientId = "ms-client") => new()
    {
        Kind = MailAuth.Microsoft, DisplayName = "Microsoft",
        AuthorizeEndpoint = "https://login.example/authorize", TokenEndpoint = "https://login.example/token",
        Scope = "https://outlook.office.com/IMAP.AccessAsUser.All offline_access openid email",
        MailScopes = new[] { "IMAP.AccessAsUser.All", "POP.AccessAsUser.All" },
        ImapHost = "127.0.0.1", Pop3Host = "127.0.0.1", RedirectHost = "localhost",
        ExtraAuthorizeParameters = new Dictionary<string, string> { ["prompt"] = "select_account" },
        ClientId = clientId,
    };

    static OAuthProvider Google() => new()
    {
        Kind = MailAuth.Google, DisplayName = "Google",
        AuthorizeEndpoint = "https://accounts.example/auth", TokenEndpoint = "https://accounts.example/token",
        Scope = "https://mail.google.com/ openid email", MailScopes = new[] { "https://mail.google.com/" }, ImapHost = "127.0.0.1", Pop3Host = "127.0.0.1", RedirectHost = "127.0.0.1",
        ClientId = "g-client", ClientSecret = "g-secret",
    };

    static string IdToken(string email) =>
        "eyJhbGciOiJub25lIn0." + OAuthClient.Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { email, sub = "1" }))) + ".sig";

    /// <summary>Schein-Token-Endpunkt: zeichnet jede Anfrage auf und antwortet mit der übergebenen Funktion.</summary>
    sealed class TokenEndpoint : HttpMessageHandler
    {
        readonly Func<Dictionary<string, string>, (HttpStatusCode, object)> _respond;
        public List<Dictionary<string, string>> Requests { get; } = new();
        public TokenEndpoint(Func<Dictionary<string, string>, (HttpStatusCode, object)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            lock (Requests) Requests.Add(form);
            var (status, body) = _respond(form);
            return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }

    static Dictionary<string, string> QueryOf(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    /// <summary>Simulierter Browser: ruft die Rücksprungadresse mit den angegebenen Parametern auf.</summary>
    static Action<string> Browser(Func<Dictionary<string, string>, string> callbackQuery, List<string>? pages = null, bool faviconFirst = false) => url =>
    {
        var q = QueryOf(url);
        var redirect = q["redirect_uri"];
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            if (faviconFirst)
            {
                var fav = await http.GetAsync(redirect + "favicon.ico");
                Assert.Equal(HttpStatusCode.NotFound, fav.StatusCode);
            }
            var page = await http.GetStringAsync(redirect + "?" + callbackQuery(q));
            lock (pages ?? new List<string>()) pages?.Add(page);
        });
    };

    [Fact]
    public void Pkce_challenge_is_sha256_of_verifier()
    {
        var (verifier, challenge) = OAuthClient.CreatePkce();
        Assert.Equal(43, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.Equal(OAuthClient.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);
        Assert.NotEqual(verifier, OAuthClient.CreatePkce().Verifier);
    }

    [Fact]
    public void Authorize_url_contains_pkce_state_scope_and_hint()
    {
        var url = OAuthClient.BuildAuthorizeUrl(Ms(), "http://localhost:5000/", "st+ate", "chal", "anna+test@outlook.com");
        var q = QueryOf(url);
        Assert.Equal("ms-client", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("http://localhost:5000/", q["redirect_uri"]);
        Assert.Equal("st+ate", q["state"]);
        Assert.Equal("chal", q["code_challenge"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("anna+test@outlook.com", q["login_hint"]);
        Assert.Equal("select_account", q["prompt"]);
        Assert.Contains("offline_access", q["scope"]);
    }

    [Fact]
    public async Task Sign_in_end_to_end_exchanges_code_with_matching_verifier()
    {
        string? challenge = null;
        var endpoint = new TokenEndpoint(form =>
        {
            // PKCE: Der Verifier aus dem Token-Tausch muss zur Challenge der Anmeldeadresse passen.
            Assert.Equal(challenge, OAuthClient.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
            return (HttpStatusCode.OK, new { access_token = "at-1", expires_in = 3599, refresh_token = "rt-1", id_token = IdToken("anna@outlook.com") });
        });
        var pages = new List<string>();
        var client = new OAuthClient(endpoint);
        var tokens = await client.SignInAsync(Ms(), "anna@outlook.com", Browser(q =>
        {
            challenge = q["code_challenge"];
            Assert.StartsWith("http://localhost:", q["redirect_uri"]);
            return "code=abc123&state=" + Uri.EscapeDataString(q["state"]);
        }, pages, faviconFirst: true), Timeout());

        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.Equal("anna@outlook.com", tokens.Account);
        Assert.True(tokens.ExpiresUtc > DateTime.UtcNow.AddMinutes(50));
        var form = Assert.Single(endpoint.Requests);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("abc123", form["code"]);
        Assert.Equal("ms-client", form["client_id"]);
        Assert.False(form.ContainsKey("client_secret"));
        await WaitFor(() => pages.Count == 1);
        Assert.Contains("MailArchy", pages[0]);
    }

    [Fact]
    public async Task Form_post_callback_from_microsoft_is_accepted()
    {
        // response_mode=form_post: Microsoft schickt code/state per POST an die Rücksprungadresse
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.OK, new { access_token = "at-p", expires_in = 3599, refresh_token = "rt-p" }));
        var pages = new List<string>();
        var tokens = await new OAuthClient(endpoint).SignInAsync(Ms(), null, url =>
        {
            var q = QueryOf(url);
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "post-code", ["state"] = q["state"], ["session_state"] = "x" });
                var page = await (await http.PostAsync(q["redirect_uri"], body)).Content.ReadAsStringAsync();
                lock (pages) pages.Add(page);
            });
        }, Timeout());
        Assert.Equal("at-p", tokens.AccessToken);
        Assert.Equal("post-code", Assert.Single(endpoint.Requests)["code"]);
        await WaitFor(() => pages.Count == 1);
        Assert.Contains("MailArchy", pages[0]);
    }

    [Fact]
    public async Task Google_sends_client_secret_and_uses_ipv4_loopback()
    {
        var endpoint = new TokenEndpoint(form =>
        {
            Assert.Equal("g-secret", form["client_secret"]);
            Assert.StartsWith("http://127.0.0.1:", form["redirect_uri"]);
            return (HttpStatusCode.OK, new { access_token = "g-at", expires_in = 3600, refresh_token = "g-rt", id_token = IdToken("anna@gmail.com"),
                scope = "https://mail.google.com/ openid https://www.googleapis.com/auth/userinfo.email" });
        });
        var tokens = await new OAuthClient(endpoint).SignInAsync(Google(), null,
            Browser(q => "state=" + Uri.EscapeDataString(q["state"]) + "&code=xyz&scope=email"), Timeout());
        Assert.Equal("anna@gmail.com", tokens.Account);
    }

    [Fact]
    public async Task Google_consent_without_gmail_checkbox_is_reported_clearly()
    {
        // Echt beobachtet: Google lässt das Gmail-Häkchen abwählen und gibt dann nur Anmelde-Berechtigungen frei.
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.OK, new { access_token = "g-at", expires_in = 3600, refresh_token = "g-rt",
            scope = "email https://www.googleapis.com/auth/userinfo.email openid" }));
        var ex = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(endpoint).SignInAsync(Google(), null,
            Browser(q => "state=" + Uri.EscapeDataString(q["state"]) + "&code=xyz"), Timeout()));
        Assert.True(ex.ReauthRequired);
        Assert.Equal(MailVault.Core.Localization.CoreText.F("OAuth_MailScopeMissing", "Google"), ex.Message);

        // Auch beim Erneuern eines so erteilten Tokens: klare Meldung statt "Authentication failed" vom Server
        var refresh = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(endpoint).RefreshAsync(Google(), "g-rt", Timeout()));
        Assert.True(refresh.ReauthRequired);
    }

    [Fact]
    public async Task Microsoft_scopes_with_resource_prefix_are_accepted()
    {
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.OK, new { access_token = "at", expires_in = 3600,
            scope = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/User.Read" }));
        var t = await new OAuthClient(endpoint).RefreshAsync(Ms(), "rt", Timeout());
        Assert.Equal("at", t.AccessToken);
    }

    [Fact]
    public async Task Wrong_state_is_rejected_and_no_token_requested()
    {
        var endpoint = new TokenEndpoint(_ => throw new InvalidOperationException("darf nicht aufgerufen werden"));
        var pages = new List<string>();
        var ex = await Assert.ThrowsAsync<OAuthException>(() =>
            new OAuthClient(endpoint).SignInAsync(Ms(), null, Browser(_ => "code=abc&state=fremd", pages), Timeout()));
        Assert.False(ex.ReauthRequired);
        Assert.Empty(endpoint.Requests);
        await WaitFor(() => pages.Count == 1); // der Browser bekommt trotzdem eine Antwort
    }

    [Fact]
    public async Task Denied_consent_gives_clear_error()
    {
        var endpoint = new TokenEndpoint(_ => throw new InvalidOperationException());
        var ex = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(endpoint).SignInAsync(Ms(), null,
            Browser(q => "error=access_denied&error_description=The+user+declined&state=" + Uri.EscapeDataString(q["state"])), Timeout()));
        Assert.Equal(MailVault.Core.Localization.CoreText.T("OAuth_Denied"), ex.Message);
    }

    [Fact]
    public async Task Missing_refresh_token_is_an_error()
    {
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.OK, new { access_token = "at", expires_in = 3600 }));
        await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(endpoint).SignInAsync(Ms(), null,
            Browser(q => "code=c&state=" + Uri.EscapeDataString(q["state"])), Timeout()));
    }

    [Fact]
    public async Task Sign_in_can_be_cancelled_while_waiting_for_browser()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OAuthClient(new TokenEndpoint(_ => throw new InvalidOperationException())).SignInAsync(Ms(), null, _ => { }, cts.Token));
    }

    [Fact]
    public async Task Unconfigured_provider_is_refused()
    {
        var ex = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient().SignInAsync(Ms(clientId: ""), null, _ => { }, Timeout()));
        Assert.Contains("Microsoft", ex.Message);
    }

    [Fact]
    public async Task Refresh_sends_scope_for_microsoft_and_keeps_old_token_if_none_returned()
    {
        var endpoint = new TokenEndpoint(form =>
        {
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("rt-old", form["refresh_token"]);
            return (HttpStatusCode.OK, new { access_token = "at-2", expires_in = 3600 });
        });
        var t = await new OAuthClient(endpoint).RefreshAsync(Ms(), "rt-old", Timeout());
        Assert.Equal("at-2", t.AccessToken);
        Assert.Equal("rt-old", t.RefreshToken);
        Assert.Contains("IMAP.AccessAsUser.All", Assert.Single(endpoint.Requests)["scope"]);
    }

    [Fact]
    public async Task Revoked_refresh_token_requires_new_sign_in()
    {
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "AADSTS70000: revoked" }));
        var ex = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(endpoint).RefreshAsync(Google(), "rt", Timeout()));
        Assert.True(ex.ReauthRequired);
        Assert.Contains("Google", ex.Message);
    }

    [Theory]
    [InlineData("invalid_client")]
    [InlineData("unauthorized_client")]
    public async Task Rejected_client_id_requires_action_and_is_reported_as_sign_in_problem(string error)
    {
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.Unauthorized, new { error, error_description = "The OAuth client was not found." }));
        var ex = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient(endpoint).RefreshAsync(Google(), "rt", Timeout()));
        Assert.True(ex.ReauthRequired);
        Assert.Contains(error, ex.Message);
        Assert.True(ArchiveScheduler.IsSignInRequired(new ArchiveProfile { AuthMethod = MailAuth.Google }, ex));
    }

    [Fact]
    public async Task Removed_client_id_is_reported_as_sign_in_problem()
    {
        var ex = await Assert.ThrowsAsync<OAuthException>(() => new OAuthClient().RefreshAsync(Ms(clientId: ""), "rt", Timeout()));
        Assert.True(ex.ReauthRequired);
    }

    [Fact]
    public async Task Changed_client_id_invalidates_the_stored_sign_in()
    {
        using var tmp = new TempDir();
        var calls = 0;
        var endpoint = new TokenEndpoint(_ => { calls++; return (HttpStatusCode.OK, new { access_token = "at-" + calls, expires_in = 3600 }); });
        var store = new OAuthTokenStore(tmp.Path);
        var profile = new ArchiveProfile { Type = ProfileType.Imap, AuthMethod = MailAuth.Microsoft, UserName = "anna@outlook.com" };
        store.Set(profile.Id, MailAuth.Microsoft, "anna@outlook.com", "rt", clientId: "ms-client");

        var service = new OAuthService(store, endpoint, _ => Ms("ms-client"));
        Assert.Equal("at-1", await service.GetAccessTokenAsync(profile, Timeout()));
        Assert.Empty(store.StaleFor(MailAuth.Microsoft, "ms-client"));

        // Andere Client-ID in den Einstellungen: auch das zwischengespeicherte Zugriffstoken gilt nicht mehr.
        var changed = new OAuthService(store, endpoint, _ => Ms("firma-client"));
        Assert.Equal(new[] { profile.Id }, store.StaleFor(MailAuth.Microsoft, "firma-client"));
        var ex = await Assert.ThrowsAsync<OAuthException>(() => changed.GetAccessTokenAsync(profile, Timeout()));
        Assert.True(ex.ReauthRequired);
        Assert.Equal(1, calls);

        // Anmeldungen ohne gespeicherte Client-ID (ältere Stände) werden nicht verworfen.
        store.Set(profile.Id, MailAuth.Microsoft, "anna@outlook.com", "rt");
        Assert.Empty(store.StaleFor(MailAuth.Microsoft, "firma-client"));
        Assert.Equal("at-2", await changed.GetAccessTokenAsync(profile, Timeout()));
    }

    [Fact]
    public void Own_client_ids_from_settings_take_precedence_and_can_be_cleared()
    {
        try
        {
            OAuthProviders.Configure("""{ "google": { "clientId": "eingebaut.apps.googleusercontent.com", "clientSecret": "GOCSPX-eingebaut" } }""");
            OAuthProviders.ApplyUserClients(null);
            Assert.True(OAuthProviders.HasBuiltIn(MailAuth.Google));
            Assert.False(OAuthProviders.HasBuiltIn(MailAuth.Microsoft));
            Assert.False(OAuthProviders.Microsoft.IsConfigured);
            Assert.Equal("GOCSPX-eingebaut", OAuthProviders.Google.ClientSecret);

            var own = new OAuthClientSettings { GoogleClientId = " firma.apps.googleusercontent.com ", MicrosoftClientId = "11111111-2222-3333-4444-555555555555" };
            own.GoogleClientSecret = "GOCSPX-firma";
            OAuthProviders.ApplyUserClients(own);
            Assert.Equal("firma.apps.googleusercontent.com", OAuthProviders.Google.ClientId);
            Assert.Equal("GOCSPX-firma", OAuthProviders.Google.ClientSecret);
            Assert.True(OAuthProviders.UsesOwnClient(MailAuth.Google));
            Assert.True(OAuthProviders.Microsoft.IsConfigured);
            Assert.Null(OAuthProviders.Microsoft.ClientSecret);
            Assert.Equal("https://oauth2.googleapis.com/token", OAuthProviders.Google.TokenEndpoint);

            // Geheimnis steht verschlüsselt in den Einstellungen, nicht im Klartext.
            var json = System.Text.Json.JsonSerializer.Serialize(own);
            Assert.DoesNotContain("GOCSPX-firma", json);

            OAuthProviders.ApplyUserClients(new OAuthClientSettings());
            Assert.Equal("eingebaut.apps.googleusercontent.com", OAuthProviders.Google.ClientId);
            Assert.Equal("GOCSPX-eingebaut", OAuthProviders.Google.ClientSecret);
            Assert.False(OAuthProviders.Microsoft.IsConfigured);
        }
        finally
        {
            OAuthProviders.ApplyUserClients(null);
            OAuthProviders.Configure(OAuthProviders.LoadConfig());
        }
    }

    [Theory]
    [InlineData(MailAuth.Google, "", true)]
    [InlineData(MailAuth.Google, "123-abc.apps.googleusercontent.com", true)]
    [InlineData(MailAuth.Google, "11111111-2222-3333-4444-555555555555", false)]
    [InlineData(MailAuth.Microsoft, "11111111-2222-3333-4444-555555555555", true)]
    [InlineData(MailAuth.Microsoft, "123-abc.apps.googleusercontent.com", false)]
    public void Client_id_format_is_checked(MailAuth kind, string id, bool ok) =>
        Assert.Equal(ok, OAuthProviders.IsPlausibleClientId(kind, id));

    [Fact]
    public void Token_store_encrypts_refresh_token_and_can_restore()
    {
        using var tmp = new TempDir();
        var store = new OAuthTokenStore(tmp.Path);
        var id = Guid.NewGuid();
        store.Set(id, MailAuth.Google, "anna@gmail.com", "1//geheimes-refresh-token");
        var file = File.ReadAllText(Path.Combine(tmp.Path, OAuthTokenStore.FileName));
        Assert.DoesNotContain("geheimes-refresh-token", file);
        Assert.Contains("anna@gmail.com", file);

        var reloaded = new OAuthTokenStore(tmp.Path);
        Assert.Equal("1//geheimes-refresh-token", reloaded.GetRefreshToken(id));
        var before = reloaded.Get(id);
        reloaded.Set(id, MailAuth.Microsoft, "other@outlook.com", "rt-new");
        reloaded.Restore(id, before);
        Assert.Equal(MailAuth.Google, reloaded.Get(id)!.Provider);
        Assert.Equal("1//geheimes-refresh-token", reloaded.GetRefreshToken(id));
        reloaded.Restore(id, null);
        Assert.Null(new OAuthTokenStore(tmp.Path).Get(id));
    }

    [Fact]
    public async Task Service_caches_access_token_and_stores_rotated_refresh_token()
    {
        using var tmp = new TempDir();
        var calls = 0;
        var endpoint = new TokenEndpoint(form =>
        {
            calls++;
            return (HttpStatusCode.OK, new { access_token = "at-" + calls, expires_in = 3600, refresh_token = "rt-rotated" });
        });
        var service = new OAuthService(new OAuthTokenStore(tmp.Path), endpoint, k => k == MailAuth.Microsoft ? Ms() : null);
        var profile = new ArchiveProfile { Type = ProfileType.Imap, AuthMethod = MailAuth.Microsoft, UserName = "anna@outlook.com" };

        await Assert.ThrowsAsync<OAuthException>(() => service.GetAccessTokenAsync(profile, Timeout())); // noch nicht angemeldet
        service.Store.Set(profile.Id, MailAuth.Microsoft, "anna@outlook.com", "rt-first");

        Assert.Equal("at-1", await service.GetAccessTokenAsync(profile, Timeout()));
        Assert.Equal("at-1", await service.GetAccessTokenAsync(profile, Timeout()));
        Assert.Equal(1, calls);
        Assert.Equal("rt-rotated", new OAuthTokenStore(tmp.Path).GetRefreshToken(profile.Id));

        // Gespeichert ist eine Microsoft-Anmeldung, das Profil verlangt Google: neu anmelden statt falsches Token zu benutzen
        var google = profile.Clone();
        google.AuthMethod = MailAuth.Google;
        var svcBoth = new OAuthService(service.Store, endpoint, k => k == MailAuth.Microsoft ? Ms() : Google());
        var ex = await Assert.ThrowsAsync<OAuthException>(() => svcBoth.GetAccessTokenAsync(google, Timeout()));
        Assert.True(ex.ReauthRequired);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Imap_archiving_uses_xoauth2_and_stays_read_only()
    {
        using var server = new FakeImapServer { AccessToken = "at-imap", AdvertiseAuthPlain = false, UserName = "anna@outlook.com" };
        server.AddFolder("Sent", "\\Sent");
        server.AddMessage("INBOX", ImapReadOnlyTests.Msg("Rechnung", "Hallo", 2));
        server.AddMessage("Sent", ImapReadOnlyTests.Msg("Antwort", "Danke", 3), new[] { "\\Seen" });
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(Path.Combine(tmp.Path, "archiv"));
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.OK, new { access_token = "at-imap", expires_in = 3600 }));
        var previous = OAuthService.Current;
        OAuthService.Current = new OAuthService(new OAuthTokenStore(tmp.Path), endpoint, k => k == MailAuth.Microsoft ? Ms() : null);
        try
        {
            var profile = new ArchiveProfile
            {
                Name = "Outlook", Type = ProfileType.Imap, Host = server.Host, Port = server.Port, Security = ConnectionSecurity.None,
                UserName = server.UserName, AuthMethod = MailAuth.Microsoft,
            };
            OAuthService.Current.Store.Set(profile.Id, MailAuth.Microsoft, server.UserName, "rt-imap");

            var result = await ImportJob.RunAsync(archive, profile, null, Timeout());
            Assert.Equal(0, result.Errors);
            Assert.Equal(2, result.Archived);
            Assert.Equal(1, server.OAuthLogins);
            Assert.DoesNotContain(server.Commands, c => c.Contains(" LOGIN ", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(server.MutatingCommands);
            Assert.DoesNotContain(server.Commands, c => c.Contains(" SELECT ", StringComparison.OrdinalIgnoreCase));

            // Falsches Token: verständlicher Fehler, kein Absturz
            server.AccessToken = "anderes-token";
            OAuthService.Current.InvalidateCache(profile.Id);
            await using var source = SourceFactory.Create(profile);
            await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => source.ConnectAsync(Timeout()));
        }
        finally { OAuthService.Current = previous; }
    }

    [Fact]
    public async Task Pop3_archiving_uses_xoauth2_and_never_deletes()
    {
        using var server = new FakePop3Server { AccessToken = "at-pop", UserName = "anna@gmail.com" };
        server.AddMessage(ImapReadOnlyTests.Msg("Eins", "a", 2));
        server.AddMessage(ImapReadOnlyTests.Msg("Zwei", "b", 3));
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(Path.Combine(tmp.Path, "archiv"));
        var endpoint = new TokenEndpoint(_ => (HttpStatusCode.OK, new { access_token = "at-pop", expires_in = 3600 }));
        var previous = OAuthService.Current;
        OAuthService.Current = new OAuthService(new OAuthTokenStore(tmp.Path), endpoint, k => k == MailAuth.Google ? Google() : null);
        try
        {
            var profile = new ArchiveProfile
            {
                Name = "Gmail POP", Type = ProfileType.Pop3, Host = server.Host, Port = server.Port, Security = ConnectionSecurity.None,
                UserName = server.UserName, AuthMethod = MailAuth.Google,
            };
            OAuthService.Current.Store.Set(profile.Id, MailAuth.Google, server.UserName, "rt-pop");
            var result = await ImportJob.RunAsync(archive, profile, null, Timeout());
            Assert.Equal(2, result.Archived);
            Assert.Equal(1, server.OAuthLogins);
            Assert.Equal(2, server.Messages.Count);
            Assert.Empty(server.MutatingCommands);
            Assert.DoesNotContain(server.Commands, c => c.StartsWith("PASS", StringComparison.Ordinal));
        }
        finally { OAuthService.Current = previous; }
    }

    [Fact]
    public void Old_profiles_without_auth_method_use_password()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"),
            """[{"Id":"6b0f7a52-2b54-4a5e-9a1e-0d3a7d1f0c11","Name":"Alt","Type":"Imap","Host":"imap.example.org","UserName":"a@example.org"}]""");
        var p = Assert.Single(new ProfileStore(tmp.Path).All);
        Assert.Equal(MailAuth.Password, p.AuthMethod);
    }

    [Theory]
    [InlineData("anna@gmail.com", "", MailAuth.Google)]
    [InlineData("chef@firma.de", "imap.gmail.com", MailAuth.Google)]
    [InlineData("anna@hotmail.de", "", MailAuth.Microsoft)]
    [InlineData("chef@firma.de", "outlook.office365.com", MailAuth.Microsoft)]
    [InlineData("anna@gmx.de", "imap.gmx.net", MailAuth.Password)]
    public void Suggests_provider_from_address_or_server(string email, string host, MailAuth expected) =>
        Assert.Equal(expected, OAuthProviders.Suggest(email, host));

    [Fact]
    public void Account_is_read_from_id_token()
    {
        Assert.Equal("anna@outlook.com", OAuthClient.AccountFromIdToken(IdToken("anna@outlook.com")));
        Assert.Null(OAuthClient.AccountFromIdToken("kaputt"));
        Assert.Null(OAuthClient.AccountFromIdToken(null));
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
