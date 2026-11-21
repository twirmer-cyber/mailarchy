using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailVault.Core;
using MailVault.Web;
using MimeKit;
using Xunit;

namespace MailVault.Tests;

public sealed class WebServerFixture : IAsyncLifetime
{
    public const string Password = "Geheim-123!";
    public TempDir Tmp { get; } = new();
    public MailArchive Archive { get; private set; } = null!;
    public WebUserStore Users { get; private set; } = null!;
    public WebArchiveServer Server { get; private set; } = null!;
    public long AliceRoot, AliceInbox, AliceSub, BobInbox;
    public long AliceMsg, AliceSubMsg, BobMsg, HtmlMsg;

    public async Task InitializeAsync()
    {
        Archive = MailArchive.Open(Tmp.Path);
        AliceInbox = Archive.EnsureFolderPath(new[] { "alice@example.org", "INBOX" });
        AliceSub = Archive.EnsureFolderPath(new[] { "alice@example.org", "INBOX", "Urlaub" });
        AliceRoot = Archive.EnsureFolderPath(new[] { "alice@example.org" });
        BobInbox = Archive.EnsureFolderPath(new[] { "bob@example.org", "INBOX" });

        AliceMsg = Archive.StoreMessage(AliceInbox, ArchiveTests.MakeMessage("Rechnung Alice", "Gemeinsames Stichwort Ferienhaus",
            attachment: ("rechnung.txt", "Betrag 42 EUR")))!.Value;
        AliceSubMsg = Archive.StoreMessage(AliceSub, ArchiveTests.MakeMessage("Fotos Strand", "Ferienhaus am Meer <b>nicht fett</b>",
            date: new DateTimeOffset(2023, 8, 1, 9, 0, 0, TimeSpan.Zero)))!.Value;
        BobMsg = Archive.StoreMessage(BobInbox, ArchiveTests.MakeMessage("Geheim Bob", "Ferienhaus von Bob",
            from: "Bob <bob@example.org>", attachment: ("bob.txt", "nur fuer Bob")))!.Value;
        HtmlMsg = Archive.StoreMessage(AliceInbox, MakeHtmlMessage())!.Value;

        Users = new WebUserStore(Tmp.Path);
        Users.Upsert(new WebUser { UserName = "admin", IsAdmin = true }, Password);
        Users.Upsert(new WebUser { UserName = "Alice", AllowedFolderIds = { AliceRoot } }, Password);
        Users.Upsert(new WebUser { UserName = "bob", AllowedFolderIds = { BobInbox } }, Password);
        Users.Upsert(new WebUser { UserName = "carol", AllowedFolderIds = { BobInbox } }, Password);
        Users.Upsert(new WebUser { UserName = "dave", Enabled = false, IsAdmin = true }, Password);

        Server = await WebArchiveServer.StartAsync(Archive, Users, new WebServerOptions { Port = 0, LanAccess = false }, default);
    }

    static byte[] MakeHtmlMessage()
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse("news@example.org"));
        msg.To.Add(MailboxAddress.Parse("alice@example.org"));
        msg.Subject = "Newsletter mit Bild";
        msg.Date = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var b = new BodyBuilder
        {
            HtmlBody = "<html><body><p>Hallo</p><img src=\"cid:logo123@x\"><script>alert('xss')</script>" +
                       "<img src=\"https://tracker.example/pixel.gif\"></body></html>",
        };
        var res = b.LinkedResources.Add("logo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, new ContentType("image", "png"));
        res.ContentId = "logo123@x";
        msg.Body = b.ToMessageBody();
        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        return ms.ToArray();
    }

    public HttpClient NewClient()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{Server.Port}/") };
    }

    public async Task<HttpClient> LoginAsync(string user)
    {
        var c = NewClient();
        var res = await PostLogin(c, user, Password);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return c;
    }

    public static Task<HttpResponseMessage> PostLogin(HttpClient c, string user, string password, bool csrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "api/login") { Content = JsonContent.Create(new { user, password }) };
        if (csrf) req.Headers.Add("X-MailVault", "1");
        return c.SendAsync(req);
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        Archive.Dispose();
        Tmp.Dispose();
    }
}

public class WebServerTests : IClassFixture<WebServerFixture>
{
    readonly WebServerFixture _f;
    public WebServerTests(WebServerFixture f) => _f = f;

    static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    static IEnumerable<long> AllFolderIds(JsonElement nodes)
    {
        foreach (var n in nodes.EnumerateArray())
        {
            yield return n.GetProperty("id").GetInt64();
            foreach (var c in AllFolderIds(n.GetProperty("children"))) yield return c;
        }
    }

    [Fact]
    public async Task Login_succeeds_and_me_returns_user()
    {
        using var c = await _f.LoginAsync("alice"); // Groß-/Kleinschreibung egal
        var me = await Json(await c.GetAsync("api/me"));
        Assert.Equal("Alice", me.GetProperty("user").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.NotNull(_f.Users.Find("alice")!.LastLoginUtc);
    }

    [Fact]
    public async Task Login_fails_for_bad_password_and_disabled_user()
    {
        using var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebServerFixture.PostLogin(c, "bob", "falsches-Kennwort")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebServerFixture.PostLogin(c, "dave", WebServerFixture.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WebServerFixture.PostLogin(c, "niemand", WebServerFixture.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("api/me")).StatusCode);
    }

    [Fact]
    public async Task Login_is_throttled_after_repeated_failures()
    {
        using var c = _f.NewClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await WebServerFixture.PostLogin(c, "carol", "falsch-" + i)).StatusCode);
        var locked = await WebServerFixture.PostLogin(c, "carol", WebServerFixture.Password); // selbst korrektes Kennwort
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("api/folders")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        using var c = _f.NewClient();
        foreach (var url in new[] { "api/me", "api/folders", $"api/folders/{_f.AliceInbox}/messages", "api/search?q=ferienhaus",
                     $"api/messages/{_f.AliceMsg}", $"api/messages/{_f.AliceMsg}/body", $"api/messages/{_f.AliceMsg}/attachments/0",
                     $"api/messages/{_f.AliceMsg}/eml" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
        // Die App-Hülle selbst ist öffentlich und trägt strenge Sicherheitsheader
        var shell = await c.GetAsync("");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        var csp = string.Join(";", shell.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("<script src=\"/app.js\"", await shell.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("app.js")).StatusCode);
    }

    [Fact]
    public async Task Post_requires_csrf_header()
    {
        using var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await WebServerFixture.PostLogin(c, "bob", WebServerFixture.Password, csrf: false)).StatusCode);

        using var logged = await _f.LoginAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await logged.PostAsync("api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await logged.GetAsync("api/me")).StatusCode); // noch angemeldet

        var foreign = new HttpRequestMessage(HttpMethod.Post, "api/logout");
        foreign.Headers.Add("X-MailVault", "1");
        foreign.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await logged.SendAsync(foreign)).StatusCode);

        var ok = new HttpRequestMessage(HttpMethod.Post, "api/logout");
        ok.Headers.Add("X-MailVault", "1");
        Assert.Equal(HttpStatusCode.NoContent, (await logged.SendAsync(ok)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await logged.GetAsync("api/me")).StatusCode);
    }

    [Fact]
    public async Task Restricted_user_sees_only_allowed_folder_tree()
    {
        using var c = await _f.LoginAsync("alice");
        var tree = await Json(await c.GetAsync("api/folders"));
        var ids = AllFolderIds(tree).ToHashSet();
        Assert.Equal(new HashSet<long> { _f.AliceRoot, _f.AliceInbox, _f.AliceSub }, ids);
        Assert.Equal(3, tree[0].GetProperty("total").GetInt64());

        using var b = await _f.LoginAsync("bob");
        var bobTree = await Json(await b.GetAsync("api/folders"));
        var root = Assert.Single(bobTree.EnumerateArray());
        Assert.Equal(_f.BobInbox, root.GetProperty("id").GetInt64());
        Assert.Equal("bob@example.org/INBOX", root.GetProperty("path").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"api/folders/{_f.BobInbox}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("api/folders/999999/messages")).StatusCode);
        var list = await Json(await c.GetAsync($"api/folders/{_f.AliceRoot}/messages?recursive=1&limit=2&offset=1"));
        Assert.Equal(3, list.GetProperty("total").GetInt64());
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Restricted_user_cannot_fetch_foreign_message_by_id()
    {
        using var c = await _f.LoginAsync("alice");
        foreach (var suffix in new[] { "", "/body", "/attachments/0", "/eml" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"api/messages/{_f.BobMsg}{suffix}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"api/messages/987654{suffix}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"api/messages/{_f.AliceSubMsg}")).StatusCode); // Unterordner erlaubt
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"api/search?q=geheim&folder={_f.BobInbox}")).StatusCode);
    }

    [Fact]
    public async Task Search_results_are_filtered_to_allowed_folders()
    {
        using var alice = await _f.LoginAsync("alice");
        var r = await Json(await alice.GetAsync("api/search?q=ferienhaus"));
        var ids = r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToHashSet();
        Assert.Equal(new HashSet<long> { _f.AliceMsg, _f.AliceSubMsg }, ids);
        Assert.Empty((await Json(await alice.GetAsync("api/search?q=geheim"))).GetProperty("items").EnumerateArray());
        var withAtt = await Json(await alice.GetAsync("api/search?attachments=1"));
        Assert.Equal(_f.AliceMsg, Assert.Single(withAtt.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        var dated = await Json(await alice.GetAsync("api/search?q=ferienhaus&dateFrom=2023-07-01&dateTo=2023-08-31"));
        Assert.Equal(_f.AliceSubMsg, Assert.Single(dated.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("api/search?q=x&dateFrom=gestern")).StatusCode);

        using var admin = await _f.LoginAsync("admin");
        var all = await Json(await admin.GetAsync("api/search?q=ferienhaus"));
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
        Assert.Equal(2, AllFolderIds(await Json(await admin.GetAsync("api/folders"))).Count(id => id == _f.AliceRoot || id == _f.BobInbox));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"api/messages/{_f.BobMsg}/eml")).StatusCode);
    }

    static HashSet<long> Ids(JsonElement r) =>
        r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToHashSet();

    [Fact]
    public async Task Search_understands_operators_in_query()
    {
        using var admin = await _f.LoginAsync("admin");
        Assert.Equal(new HashSet<long> { _f.BobMsg }, Ids(await Json(await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("von:bob ferienhaus")))));
        Assert.Equal(new HashSet<long> { _f.AliceSubMsg }, Ids(await Json(await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("ferienhaus jahr:2023")))));
        Assert.Equal(new HashSet<long> { _f.AliceMsg }, Ids(await Json(await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("betreff:rechnung")))));
        Assert.Equal(new HashSet<long> { _f.AliceMsg, _f.BobMsg },
            Ids(await Json(await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("ferienhaus anhang:ja")))));

        // Operatoren ändern nichts an den Ordnerfreigaben
        using var alice = await _f.LoginAsync("alice");
        Assert.Empty(Ids(await Json(await alice.GetAsync("api/search?q=" + Uri.EscapeDataString("von:bob")))));

        // Absender ohne <Adresse> für die Listenanzeige, Suchwörter zum Hervorheben
        var r = await Json(await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("von:bob ferienhaus")));
        var item = Assert.Single(r.GetProperty("items").EnumerateArray());
        Assert.Equal("Bob", item.GetProperty("fromName").GetString());
        Assert.Equal("bob@example.org", item.GetProperty("fromAddress").GetString());
        Assert.Equal(new[] { "ferienhaus" }, r.GetProperty("terms").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public async Task Search_rejects_invalid_operator_with_german_message()
    {
        using var admin = await _f.LoginAsync("admin");
        var res = await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("rechnung jahr:abc"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var err = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
        Assert.Contains("kein gültiges Jahr", err);

        res = await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("anhang:vielleicht"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("anhang:ja", await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("api/search?q=x&sort=zufall")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("api/search?q=x&range=morgen")).StatusCode);
    }

    [Fact]
    public async Task Search_accepts_relevance_sort_and_range_filters()
    {
        using var admin = await _f.LoginAsync("admin");
        var rel = await Json(await admin.GetAsync("api/search?q=ferienhaus&sort=relevance"));
        Assert.Equal("relevance", rel.GetProperty("sort").GetString());
        Assert.Equal(3, rel.GetProperty("items").GetArrayLength());
        var date = await Json(await admin.GetAsync("api/search?q=ferienhaus&sort=date"));
        Assert.Equal("date", date.GetProperty("sort").GetString());
        // Ohne Suchwort gibt es keine Relevanz – dann nach Datum
        Assert.Equal("date", (await Json(await admin.GetAsync("api/search?attachments=1&sort=relevance"))).GetProperty("sort").GetString());

        using var alice = await _f.LoginAsync("alice");
        var relAlice = await Json(await alice.GetAsync("api/search?q=ferienhaus&sort=relevance"));
        Assert.Equal(new HashSet<long> { _f.AliceMsg, _f.AliceSubMsg }, Ids(relAlice));

        // Testnachrichten stammen aus 2023/2024 – „letzte 30 Tage“ findet nichts, „alle“ alles
        Assert.Empty(Ids(await Json(await admin.GetAsync("api/search?q=ferienhaus&range=30d"))));
        Assert.Equal(3, Ids(await Json(await admin.GetAsync("api/search?q=ferienhaus&range=all"))).Count);
        // Operator im Suchtext hat Vorrang vor dem Zeitraum-Chip
        Assert.Equal(new HashSet<long> { _f.AliceSubMsg },
            Ids(await Json(await admin.GetAsync("api/search?q=" + Uri.EscapeDataString("ferienhaus jahr:2023") + "&range=30d"))));
    }

    [Fact]
    public async Task Search_help_lists_syntax_examples()
    {
        using var c = await _f.LoginAsync("bob");
        var r = await Json(await c.GetAsync("api/search/help"));
        var examples = r.GetProperty("examples").EnumerateArray().ToList();
        Assert.Equal(SearchSyntax.Examples.Count, examples.Count);
        Assert.Contains(examples, e => e.GetProperty("example").GetString() == "von:bank" && e.GetProperty("meaning").GetString()!.Length > 0);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().GetAsync("api/search/help")).StatusCode);
    }

    [Fact]
    public async Task Message_details_and_downloads()
    {
        using var c = await _f.LoginAsync("alice");
        var m = await Json(await c.GetAsync($"api/messages/{_f.AliceMsg}"));
        Assert.Equal("Rechnung Alice", m.GetProperty("subject").GetString());
        Assert.Equal("alice@example.org/INBOX", m.GetProperty("folderPath").GetString());
        Assert.Contains("Ferienhaus", m.GetProperty("text").GetString());
        var att = Assert.Single(m.GetProperty("attachments").EnumerateArray());
        Assert.Equal("rechnung.txt", att.GetProperty("name").GetString());

        var dl = await c.GetAsync($"api/messages/{_f.AliceMsg}/attachments/0");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal("attachment", dl.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("rechnung.txt", dl.Content.Headers.ContentDisposition.FileNameStar ?? dl.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.Equal("application/octet-stream", dl.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Betrag 42 EUR", await dl.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"api/messages/{_f.AliceMsg}/attachments/5")).StatusCode);

        var eml = await c.GetAsync($"api/messages/{_f.AliceMsg}/eml");
        Assert.Equal("attachment", eml.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(_f.Archive.GetRawMessage(_f.AliceMsg), await eml.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Html_body_is_sandboxed_and_inlines_cid_images()
    {
        using var c = await _f.LoginAsync("alice");
        var res = await c.GetAsync($"api/messages/{_f.HtmlMsg}/body");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var csp = string.Join(";", res.Headers.GetValues("Content-Security-Policy"));
        Assert.StartsWith("sandbox", csp);
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("img-src data:;", csp); // keine externen Bilder
        Assert.DoesNotContain("allow-scripts", csp);
        Assert.Equal("nosniff", string.Join("", res.Headers.GetValues("X-Content-Type-Options")));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("cid:logo123", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plain_text_body_is_html_escaped()
    {
        using var c = await _f.LoginAsync("alice");
        var res = await c.GetAsync($"api/messages/{_f.AliceSubMsg}/body");
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("<pre>", html);
        Assert.Contains("&lt;b&gt;nicht fett&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>nicht fett", html);
        Assert.StartsWith("sandbox", string.Join(";", res.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Disabling_user_ends_session()
    {
        var store = _f.Users;
        store.Upsert(new WebUser { UserName = "erin", AllowedFolderIds = { _f.BobInbox } }, WebServerFixture.Password);
        using var c = await _f.LoginAsync("erin");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("api/me")).StatusCode);
        var erin = store.Find("erin")!;
        erin.Enabled = false;
        store.Upsert(erin, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("api/me")).StatusCode);
    }

    [Fact]
    public void Server_reports_real_port_and_urls()
    {
        Assert.NotEqual(0, _f.Server.Port);
        Assert.Contains($"http://localhost:{_f.Server.Port}/", _f.Server.Urls);
    }
}

public class WebUserStoreTests
{
    [Fact]
    public void Round_trip_hashes_passwords_and_validates()
    {
        using var tmp = new TempDir();
        var store = new WebUserStore(tmp.Path);
        Assert.Throws<ArgumentException>(() => store.Upsert(new WebUser { UserName = "neu" }, null));      // Kennwort Pflicht
        Assert.Throws<ArgumentException>(() => store.Upsert(new WebUser { UserName = "neu" }, "kurz"));    // zu kurz
        store.Upsert(new WebUser { UserName = "Oma", AllowedFolderIds = { 5, 7, 5 } }, "Kennwort-Oma-1");
        store.Upsert(new WebUser { UserName = "oma", AllowedFolderIds = { 9 } }, null); // gleicher Benutzer, Kennwort bleibt
        Assert.Single(store.All);

        var json = File.ReadAllText(Path.Combine(tmp.Path, WebUserStore.FileName));
        Assert.DoesNotContain("Kennwort-Oma-1", json);
        Assert.Contains("pbkdf2-sha256$v1$", json);

        var reloaded = new WebUserStore(tmp.Path);
        var u = Assert.Single(reloaded.All);
        Assert.Equal("oma", u.UserName);
        Assert.Equal(new List<long> { 9 }, u.AllowedFolderIds);
        Assert.NotNull(reloaded.Verify("OMA", "Kennwort-Oma-1"));
        Assert.Null(reloaded.Verify("oma", "Kennwort-Oma-2"));
        var parts = u.PasswordHash.Split('$');
        Assert.True(int.Parse(parts[2]) >= 210_000);

        reloaded.Upsert(new WebUser { UserName = "zweite", Enabled = false }, "Kennwort-Zwei");
        Assert.NotEqual(reloaded.Find("oma")!.PasswordHash.Split('$')[3], reloaded.Find("zweite")!.PasswordHash.Split('$')[3]); // eigenes Salz
        Assert.Null(reloaded.Verify("zweite", "Kennwort-Zwei")); // deaktiviert
        reloaded.Delete("ZWEITE");
        Assert.Single(new WebUserStore(tmp.Path).All);
    }

    [Fact]
    public void Allowed_folder_set_includes_descendants()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var a = archive.EnsureFolderPath(new[] { "A" });
        var ab = archive.EnsureFolderPath(new[] { "A", "B" });
        var abc = archive.EnsureFolderPath(new[] { "A", "B", "C" });
        var x = archive.EnsureFolderPath(new[] { "X" });
        var user = new WebUser { UserName = "u", AllowedFolderIds = { ab, 424242 } };
        Assert.Equal(new HashSet<long> { ab, abc }, WebUserStore.GetAllowedFolderIds(user, archive));
        Assert.True(WebUserStore.CanAccessFolder(user, abc, archive));
        Assert.False(WebUserStore.CanAccessFolder(user, a, archive));
        Assert.False(WebUserStore.CanAccessFolder(user, x, archive));
        Assert.Null(WebUserStore.GetAllowedFolderIds(new WebUser { IsAdmin = true }, archive));
    }
}
