using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using MailVault.Core;
using MailVault.Core.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MailVault.Web;

public sealed class WebServerOptions
{
    /// <summary>TCP-Port; 0 = freien Port wählen (siehe <see cref="WebArchiveServer.Port"/>).</summary>
    public int Port { get; set; } = 8025;
    /// <summary>true: an alle Netzwerkschnittstellen binden (0.0.0.0), sonst nur 127.0.0.1.</summary>
    public bool LanAccess { get; set; } = true;
    /// <summary>Externe Inhalte (Bilder, Schriften, Stylesheets aus dem Internet) in Mails blockieren.</summary>
    public bool BlockExternalContent { get; set; } = true;
    /// <summary>Optionale Protokollausgabe (wird aus Server-Threads aufgerufen).</summary>
    public Action<string>? Log { get; set; }
}

/// <summary>
/// Eingebetteter Webserver (Kestrel) für den Lesezugriff auf das Archiv im Heimnetz.
/// Alle Nachrichten-, Anhang- und Ordner-Endpunkte prüfen die Ordnerfreigaben des Benutzers serverseitig.
/// </summary>
public sealed class WebArchiveServer : IAsyncDisposable
{
    internal const string SessionCookie = "mv_session";
    internal const string CsrfHeader = "X-MailVault";
    const int MaxPageSize = 200;
    const int MaxSearchResults = 500;

    const string AppCsp =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "frame-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    const string ApiCsp = "default-src 'none'; frame-ancestors 'none'; sandbox";

    readonly WebApplication _app;
    readonly MailArchive _archive;
    readonly WebUserStore _users;
    readonly WebServerOptions _options;
    readonly SessionStore _sessions = new();
    readonly LoginThrottle _throttle = new();
    readonly Dictionary<string, (byte[] Data, string ContentType)> _static;
    /// <summary>Sprache der Protokollzeilen = Sprache der Desktop-App beim Start (nicht die des jeweiligen Browsers).</summary>
    readonly CultureInfo _logCulture = CultureInfo.CurrentUICulture;
    int _stopped;

    public int Port { get; private set; }
    /// <summary>Adressen zur Anzeige (localhost und – bei LAN-Zugriff – die IPv4-Adressen dieses Rechners).</summary>
    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();

    WebArchiveServer(WebApplication app, MailArchive archive, WebUserStore users, WebServerOptions options)
    {
        _app = app;
        _archive = archive;
        _users = users;
        _options = options;
        _static = LoadStaticFiles();
    }

    public static async Task<WebArchiveServer> StartAsync(MailArchive archive, WebUserStore users, WebServerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), CoreText.T("Web_InvalidPort"));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(WebArchiveServer).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>(),
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>(); // keine Konsolen-/ProcessExit-Hooks im WPF-Prozess
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            k.Listen(options.LanAccess ? IPAddress.Any : IPAddress.Loopback, options.Port);
        });

        var app = builder.Build();
        var server = new WebArchiveServer(app, archive, users, options);
        server.Configure();
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var bound = app.Urls.Select(u => new Uri(u.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost"))).First();
        server.Port = bound.Port;
        server.Urls = BuildDisplayUrls(server.Port, options.LanAccess);
        server.Log(CoreText.F(server._logCulture, "Web_Started", string.Join(", ", server.Urls)));
        return server;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _sessions.Clear();
        try { await _app.StopAsync().ConfigureAwait(false); }
        finally { Log(CoreText.T(_logCulture, "Web_Stopped")); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Beendet alle Sitzungen eines Benutzers (z. B. nach Löschen/Sperren in der Verwaltung).</summary>
    public void SignOutUser(string userName) => _sessions.RemoveUser(userName);

    void Log(string message)
    {
        try { _options.Log?.Invoke(message); } catch { /* Protokoll darf den Server nicht stören */ }
    }

    static IReadOnlyList<string> BuildDisplayUrls(int port, bool lan)
    {
        var list = new List<string> { $"http://localhost:{port}/" };
        if (!lan) return list;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    if (ip.GetAddressBytes() is [169, 254, ..]) continue; // APIPA
                    var url = $"http://{ip}:{port}/";
                    if (!list.Contains(url)) list.Add(url);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    // ------------------------------------------------------------------ Pipeline

    void Configure()
    {
        var app = _app;

        // Sprache der Antworttexte (Fehlermeldungen, Suchhilfe) nach Accept-Language. Die Weboberfläche sendet ihre
        // gewählte Sprache mit; ohne Angabe gilt die Sprache der Desktop-App, bei nicht unterstützter Sprache Englisch.
        app.Use(async (ctx, next) =>
        {
            var wanted = ctx.Request.GetTypedHeaders().AcceptLanguage;
            if (wanted is { Count: > 0 })
            {
                CultureInfo.CurrentUICulture = CoreText.Match(wanted
                        .Where(l => (l.Quality ?? 1) > 0)
                        .OrderByDescending(l => l.Quality ?? 1)
                        .Select(l => l.Value.Value ?? ""))
                    ?? CultureInfo.GetCultureInfo("en");
            }
            await next(ctx);
        });

        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            var isApi = ctx.Request.Path.StartsWithSegments("/api");
            h["Content-Security-Policy"] = isApi ? ApiCsp : AppCsp;
            h["Cache-Control"] = isApi ? "no-store" : "no-cache";
            try
            {
                await next(ctx);
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                Log(CoreText.F(_logCulture, "Web_RequestError", ctx.Request.Method, ctx.Request.Path, ex.Message));
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.Clear();
                    await Error(ctx, StatusCodes.Status500InternalServerError, CoreText.T("Web_InternalError"));
                }
            }
        });

        // Anmeldung und CSRF-Schutz für die API
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(ctx); return; }

            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                if (ctx.Request.Headers[CsrfHeader] != "1" || !OriginMatches(ctx.Request))
                {
                    await Error(ctx, StatusCodes.Status403Forbidden, CoreText.T("Web_Forbidden"));
                    return;
                }
            }

            if (!ctx.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase))
            {
                var user = ResolveUser(ctx);
                if (user is null)
                {
                    await Error(ctx, StatusCodes.Status401Unauthorized, CoreText.T("Web_NotSignedIn"));
                    return;
                }
                ctx.Items[typeof(WebUser)] = user;
            }
            await next(ctx);
        });

        foreach (var (path, _) in _static)
        {
            var p = path;
            app.MapGet(p == "index.html" ? "/" : "/" + p, (HttpContext ctx) => ServeStatic(ctx, p));
        }
        app.MapGet("/index.html", (HttpContext ctx) => ServeStatic(ctx, "index.html"));

        app.MapPost("/api/login", (Delegate)Login);
        app.MapPost("/api/logout", Logout);
        app.MapGet("/api/me", (HttpContext ctx) => { var u = CurrentUser(ctx); return Results.Json(new { user = u.UserName, isAdmin = u.IsAdmin }); });
        app.MapGet("/api/folders", Folders);
        app.MapGet("/api/folders/{id:long}/messages", FolderMessages);
        app.MapGet("/api/search", Search);
        app.MapGet("/api/search/help", SearchHelp);
        app.MapGet("/api/messages/{id:long}", Message);
        app.MapGet("/api/messages/{id:long}/body", MessageBody);
        app.MapGet("/api/messages/{id:long}/attachments/{index:int}", Attachment);
        app.MapGet("/api/messages/{id:long}/eml", Eml);
        app.Map("/api/{**rest}", (HttpContext ctx) => NotFound());
    }

    static bool OriginMatches(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // Nicht-Browser-Clients; der Custom-Header genügt dann
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
               string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(o.Scheme, req.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    static Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = message });
    }

    static IResult NotFound() => Results.Json(new { error = CoreText.T("Web_NotFound") }, statusCode: 404);
    static IResult BadRequest(string msg) => Results.Json(new { error = msg }, statusCode: 400);

    static WebUser CurrentUser(HttpContext ctx) => (WebUser)ctx.Items[typeof(WebUser)]!;

    WebUser? ResolveUser(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[SessionCookie];
        var session = _sessions.Get(token);
        if (session is null) return null;
        var user = _users.Find(session.UserName);
        if (user is null || !user.Enabled || user.PasswordHash != session.PasswordHash)
        {
            _sessions.Remove(token);
            return null;
        }
        return user;
    }

    IResult ServeStatic(HttpContext ctx, string path)
    {
        var (data, type) = _static[path];
        return Results.Bytes(data, type);
    }

    static Dictionary<string, (byte[], string)> LoadStaticFiles()
    {
        var asm = Assembly.GetExecutingAssembly();
        var dict = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var file = name["wwwroot/".Length..];
            var type = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream",
            };
            dict[file] = (ms.ToArray(), type);
        }
        return dict;
    }

    // ------------------------------------------------------------------ Anmeldung

    sealed record LoginRequest(string? User, string? Password);

    async Task<IResult> Login(HttpContext ctx)
    {
        LoginRequest? req;
        try { req = await ctx.Request.ReadFromJsonAsync<LoginRequest>(ctx.RequestAborted); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return BadRequest(CoreText.T("Web_BadRequest"));
        }
        var userName = (req?.User ?? "").Trim();
        var password = req?.Password ?? "";
        if (userName.Length == 0 || userName.Length > 64 || password.Length == 0 || password.Length > 1024)
            return Results.Json(new { error = CoreText.T("Web_LoginFailed") }, statusCode: 401);

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (_throttle.GetLockout(ip, userName) is { } wait)
        {
            ctx.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = CoreText.F("Web_TooManyAttempts", Math.Ceiling(wait.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)) },
                statusCode: 429);
        }

        var user = await Task.Run(() => _users.Verify(userName, password)).ConfigureAwait(false);
        if (user is null)
        {
            _throttle.RegisterFailure(ip, userName);
            Log(CoreText.F(_logCulture, "Web_LoginFailedLog", userName, ip));
            return Results.Json(new { error = CoreText.T("Web_LoginFailed") }, statusCode: 401);
        }

        _throttle.RegisterSuccess(ip, userName);
        _sessions.Remove(ctx.Request.Cookies[SessionCookie]); // keine Sitzungsfixierung
        var token = _sessions.Create(user.UserName, user.PasswordHash);
        ctx.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Secure = ctx.Request.IsHttps, IsEssential = true,
        });
        Log(CoreText.F(_logCulture, "Web_LoginLog", user.UserName, ip));
        return Results.Json(new { user = user.UserName, isAdmin = user.IsAdmin });
    }

    IResult Logout(HttpContext ctx)
    {
        _sessions.Remove(ctx.Request.Cookies[SessionCookie]);
        ctx.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Strict });
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ Ordner & Listen

    sealed record FolderNode(long Id, string Name, string? Path, long Count, long Total, List<FolderNode> Children);

    static FolderNode ToNode(ArchiveFolder f, string? path) =>
        new(f.Id, f.Name, path, f.MessageCount, f.TotalCount, f.Children.Select(c => ToNode(c, null)).ToList());

    /// <summary>Oberste sichtbare Ordner (für Admins die Wurzeln, sonst die obersten freigegebenen Teilbäume).</summary>
    static List<ArchiveFolder> VisibleRoots(List<ArchiveFolder> tree, HashSet<long>? allowed)
    {
        if (allowed is null) return tree;
        var result = new List<ArchiveFolder>();
        void Walk(ArchiveFolder f)
        {
            if (allowed.Contains(f.Id)) { result.Add(f); return; }
            foreach (var c in f.Children) Walk(c);
        }
        foreach (var r in tree) Walk(r);
        return result;
    }

    static ArchiveFolder? FindFolder(IEnumerable<ArchiveFolder> nodes, long id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            if (FindFolder(n.Children, id) is { } hit) return hit;
        }
        return null;
    }

    IResult Folders(HttpContext ctx)
    {
        var user = CurrentUser(ctx);
        var tree = _archive.GetFolderTree();
        var allowed = WebUserStore.GetAllowedFolderIds(user, tree);
        var roots = VisibleRoots(tree, allowed);
        return Results.Json(roots.Select(r => ToNode(r, allowed is null ? null : _archive.GetFolderPath(r.Id))).ToList());
    }

    static object ToJson(MessageSummary m)
    {
        var (name, address) = SplitSender(m.From);
        return new
        {
            id = m.Id, folderId = m.FolderId, folderPath = m.FolderPath, date = DateTime.SpecifyKind(m.DateUtc, DateTimeKind.Utc),
            from = m.From, fromName = name, fromAddress = address,
            to = m.To, subject = m.Subject, size = m.Size, hasAttachments = m.HasAttachments,
        };
    }

    /// <summary>Erster Absender als Anzeigename (ohne &lt;Adresse&gt;) und Adresse; fällt auf den Rohtext zurück.</summary>
    internal static (string Name, string Address) SplitSender(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return ("", "");
        if (InternetAddressList.TryParse(from, out var list) && list.Mailboxes.FirstOrDefault() is { } mb)
            return (string.IsNullOrWhiteSpace(mb.Name) ? mb.Address : mb.Name, mb.Address);
        return (from, "");
    }

    static (int Offset, int Limit) Paging(HttpRequest req, int defLimit, int maxLimit)
    {
        int.TryParse(req.Query["offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset);
        if (!int.TryParse(req.Query["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0) limit = defLimit;
        return (Math.Clamp(offset, 0, 1_000_000), Math.Min(limit, maxLimit));
    }

    IResult FolderMessages(HttpContext ctx, long id)
    {
        var user = CurrentUser(ctx);
        var tree = _archive.GetFolderTree();
        var allowed = WebUserStore.GetAllowedFolderIds(user, tree);
        var folder = FindFolder(tree, id);
        if (folder is null || (allowed is not null && !allowed.Contains(id))) return NotFound();

        var recursive = IsTrue(ctx.Request.Query["recursive"]) == true;
        var (offset, limit) = Paging(ctx.Request, 50, MaxPageSize);
        var items = _archive.GetMessages(id, recursive, offset + limit)
            .Where(m => allowed is null || allowed.Contains(m.FolderId))
            .Skip(offset).Select(ToJson).ToList();
        return Results.Json(new { total = recursive ? folder.TotalCount : folder.MessageCount, offset, limit, items });
    }

    static bool? IsTrue(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "ja" or "yes" => true,
        "0" or "false" or "nein" or "no" => false,
        _ => null,
    };

    static bool TryDate(string? s, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { value = d; return true; }
        return false;
    }

    IResult SearchHelp() =>
        Results.Json(new { examples = SearchSyntax.Examples.Select(e => new { example = e.Example, meaning = e.Meaning }) });

    /// <summary>Zeitraum-Schnellfilter der Weboberfläche (range=30d|thisyear|lastyear).</summary>
    static bool TryRange(string? s, SearchCriteria c)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case null or "" or "all": return true;
            case "30d": c.DateRange = DateRangeType.NotOlderThanDays; c.Days = 30; return true;
            case "thisyear": c.DateRange = DateRangeType.ThisYear; return true;
            case "lastyear": c.DateRange = DateRangeType.LastYear; return true;
            default: return false;
        }
    }

    /// <summary>Freitext-Wörter (ohne Ausschlüsse und OR/AND/NOT) zum Hervorheben in der Trefferliste.</summary>
    internal static List<string> HighlightTerms(SearchCriteria c)
    {
        var terms = new List<string>();
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var tokens = new List<string>();
            var sb = new System.Text.StringBuilder();
            var inQuote = false;
            foreach (var ch in text)
            {
                if (ch == '"') inQuote = !inQuote;
                if (char.IsWhiteSpace(ch) && !inQuote) { if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(ch);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            foreach (var raw in tokens)
            {
                if (raw.StartsWith('-') || raw is "OR" or "AND" or "NOT") continue;
                foreach (var w in raw.Split(new[] { ' ', '"' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = w.Trim('*', '(', ')');
                    if (t.Length >= 2 && !terms.Contains(t, StringComparer.OrdinalIgnoreCase)) terms.Add(t);
                }
            }
        }
        Add(c.Query);
        Add(c.Subject);
        return terms.Take(20).ToList();
    }

    IResult Search(HttpContext ctx)
    {
        var user = CurrentUser(ctx);
        var q = ctx.Request.Query;
        if (!TryDate(q["dateFrom"], out var dFrom) || !TryDate(q["dateTo"], out var dTo)) return BadRequest(CoreText.T("Web_BadDate"));
        long? folder = null;
        if (!string.IsNullOrEmpty(q["folder"]))
        {
            if (!long.TryParse(q["folder"], NumberStyles.None, CultureInfo.InvariantCulture, out var f)) return BadRequest(CoreText.T("Web_BadFolder"));
            folder = f;
        }
        string? Str(string key) { var v = q[key].ToString().Trim(); return v.Length == 0 ? null : v.Length > 500 ? v[..500] : v; }

        var sort = q["sort"].ToString().Trim().ToLowerInvariant();
        if (sort is not ("" or "date" or "relevance")) return BadRequest(CoreText.T("Web_BadSort"));

        var criteria = new SearchCriteria
        {
            Query = Str("q"), From = Str("from"), To = Str("to"),
            HasAttachments = IsTrue(q["attachments"]),
            DateRange = dFrom is not null || dTo is not null ? DateRangeType.Custom : DateRangeType.NotSpecified,
            DateFrom = dFrom, DateTo = dTo,
            SortByRelevance = sort == "relevance",
        };
        if (!TryRange(q["range"], criteria)) return BadRequest(CoreText.T("Web_BadRange"));
        // Operatoren im Suchtext (von:, an:, betreff:, anhang:, jahr:, nach:, vor:) haben Vorrang vor den Schnellfiltern.
        try { SearchSyntax.Apply(criteria); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }

        var (offset, limit) = Paging(ctx.Request, 100, MaxSearchResults);
        var terms = HighlightTerms(criteria);
        var relevance = criteria.SortByRelevance && !string.IsNullOrWhiteSpace(criteria.Query);
        var sortOut = relevance ? "relevance" : "date";
        if (criteria.IsEmpty && folder is null)
            return Results.Json(new { offset, limit, more = false, sort = sortOut, terms, items = Array.Empty<object>() });

        var tree = _archive.GetFolderTree();
        var allowed = WebUserStore.GetAllowedFolderIds(user, tree);
        if (folder is { } fid)
        {
            if (FindFolder(tree, fid) is null || (allowed is not null && !allowed.Contains(fid))) return NotFound();
            criteria.FolderId = fid;
        }
        criteria.FolderRecurse = true;
        // Eingeschränkte Benutzer: nur die sichtbaren Teilbäume – eine Abfrage, damit die Relevanz-Reihenfolge erhalten bleibt.
        if (allowed is not null) criteria.AllowedFolderIds = VisibleRoots(tree, allowed).Select(r => r.Id).ToList();

        List<MessageSummary> results;
        try { results = _archive.Search(criteria, offset + limit + 1); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }

        IEnumerable<MessageSummary> filtered = results
            .Where(m => allowed is null || allowed.Contains(m.FolderId)) // Absicherung, falls ein Ordner zwischenzeitlich gelöscht wurde
            .DistinctBy(m => m.Id);
        if (!relevance) filtered = filtered.OrderByDescending(m => m.DateUtc).ThenByDescending(m => m.Id);
        var page = filtered.Skip(offset).Take(limit + 1).ToList();
        var more = page.Count > limit;
        return Results.Json(new { offset, limit, more, sort = sortOut, terms, items = page.Take(limit).Select(ToJson).ToList() });
    }

    // ------------------------------------------------------------------ Nachrichten

    /// <summary>Liefert die Nachricht nur, wenn sie in einem für den Benutzer freigegebenen Ordner liegt.</summary>
    MessageSummary? Authorize(HttpContext ctx, long id)
    {
        var summary = _archive.GetSummary(id);
        if (summary is null) return null;
        var user = CurrentUser(ctx);
        return user.IsAdmin || WebUserStore.CanAccessFolder(user, summary.FolderId, _archive) ? summary : null;
    }

    IResult Message(HttpContext ctx, long id)
    {
        if (Authorize(ctx, id) is not { } s) return NotFound();
        var msg = _archive.LoadMessage(id);
        var text = MailRenderer.PlainText(msg);
        if (text.Length > 1_000_000) text = text[..1_000_000] + "\n" + CoreText.T("Web_Truncated");
        return Results.Json(new
        {
            id, folderId = s.FolderId, folderPath = s.FolderPath,
            subject = msg.Subject ?? "",
            from = MessageIndexer.FormatAddresses(msg.From),
            to = MessageIndexer.FormatAddresses(msg.To),
            cc = MessageIndexer.FormatAddresses(msg.Cc),
            date = msg.Date == DateTimeOffset.MinValue ? new DateTimeOffset(DateTime.SpecifyKind(s.DateUtc, DateTimeKind.Utc)) : msg.Date,
            size = s.Size,
            hasHtml = msg.HtmlBody is not null,
            attachments = MailRenderer.DescribeAttachments(msg).Select(a => new { index = a.Index, name = a.Name, size = a.Size, contentType = a.ContentType }),
            text,
        });
    }

    IResult MessageBody(HttpContext ctx, long id)
    {
        if (Authorize(ctx, id) is null) return NotFound();
        var msg = _archive.LoadMessage(id);
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = MailRenderer.BodyCsp(!_options.BlockExternalContent);
        h["X-Frame-Options"] = "SAMEORIGIN";
        h["Cache-Control"] = "no-store";
        return Results.Text(MailRenderer.BuildHtml(msg), "text/html; charset=utf-8");
    }

    IResult Attachment(HttpContext ctx, long id, int index)
    {
        if (Authorize(ctx, id) is null) return NotFound();
        var msg = _archive.LoadMessage(id);
        var atts = MailRenderer.GetAttachments(msg);
        if (index < 0 || index >= atts.Count) return NotFound();
        var entity = atts[index];
        return Results.File(MailRenderer.GetAttachmentContent(entity), "application/octet-stream",
            Exporter.SafeName(MailRenderer.AttachmentName(entity)));
    }

    IResult Eml(HttpContext ctx, long id)
    {
        if (Authorize(ctx, id) is not { } s) return NotFound();
        var raw = _archive.GetRawMessage(id);
        var name = Exporter.SafeName(string.IsNullOrWhiteSpace(s.Subject) ? CoreText.T("Web_MessageFileName") : s.Subject, 100) + ".eml";
        return Results.File(raw, "application/octet-stream", name);
    }

    sealed class NoopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
