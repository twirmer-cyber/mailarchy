using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MailVault.Core;
using MailVault.Core.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MailVault.Tests;

/// <summary>
/// Update-Funktion gegen einen nachgebauten Update-Server (signierte Update-Listen, Pakete), Erstinstallation und
/// Schutz der Archivdaten bei Formatänderungen.
/// </summary>
public class UpdateTests
{
    /// <summary>Version der gebauten MailVault.Core.dll – echte Pakete enthalten sie, daher wird sie für Testpakete genutzt.</summary>
    static readonly string PackagedVersion = BuildInfo.FromFile(typeof(MailArchive).Assembly.Location).Version;

    sealed class FakeServer : IAsyncDisposable
    {
        readonly WebApplication _app;
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
        public JsonArray Versions { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = new();
        /// <summary>Manipulationen für Tests: Liste nach dem Signieren verändern bzw. keine Signatur ausliefern.</summary>
        public Func<byte[], byte[]>? TamperAfterSigning { get; set; }
        public bool OmitSignature { get; set; }
        public bool FeedMissing { get; set; }
        public string Url { get; }

        public FakeServer()
        {
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
            _app = b.Build();
            _app.MapGet("/updates/{name}", (string name) =>
            {
                if (FeedMissing) return Results.NotFound();
                var channel = name.Replace(".json.sig", "").Replace(".json", "");
                var json = Feed(channel);
                if (name.EndsWith(".sig"))
                    return OmitSignature ? Results.NotFound() : Results.Text(UpdateFeed.Sign(json, Key));
                return Results.Bytes(TamperAfterSigning?.Invoke(json) ?? json, "application/json");
            });
            _app.MapGet("/download/{name}", (string name) => Files.TryGetValue(name, out var d) ? Results.Bytes(d, "application/zip") : Results.NotFound());
            _app.StartAsync().GetAwaiter().GetResult();
            Url = _app.Urls.First() + "/updates/";
        }

        byte[] Feed(string channel)
        {
            var list = new JsonArray(Versions.OfType<JsonObject>()
                .Where(v => channel == "beta" || !SemVersion.Parse((string?)v["version"])!.IsPrerelease)
                .Select(v => (JsonNode)v.DeepClone()).ToArray());
            return Encoding.UTF8.GetBytes(new JsonObject { ["product"] = "MailArchy", ["channel"] = channel, ["versions"] = list }.ToJsonString());
        }

        /// <summary>Trägt eine Version ein. <paramref name="sha"/>: null = korrekte Prüfsumme des Pakets.</summary>
        public void Add(string version, byte[] package, string? sha = null, JsonObject? notes = null)
        {
            var name = $"MailVault-{version}-win-x64.zip";
            Files[name] = package;
            Versions.Add(new JsonObject
            {
                ["version"] = version,
                ["published"] = "2026-11-18T10:00:00Z",
                ["notes"] = notes ?? new JsonObject { ["de"] = new JsonArray($"Neu in {version}"), ["en"] = new JsonArray($"New in {version}") },
                ["package"] = new JsonObject
                {
                    ["url"] = "../download/" + name,
                    ["size"] = package.Length,
                    ["sha256"] = sha ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                },
            });
        }

        public UpdateManager Manager(string current = "0.1.0", string channel = "stable", string? publicKey = null) =>
            new(new InstallInfo { Channel = channel }, @"C:\Programme\MailVault", new BuildInfo(current, "abcdef1234567"), Url, publicKey ?? PublicKey);

        public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); Key.Dispose(); }
    }

    /// <summary>Ein Paket wie vom Veröffentlichungs-Build: MailVault.exe + MailVault.Core.dll (+ optional unerwünschte Benutzerdateien).</summary>
    static byte[] Package(bool withUserFiles = false, bool withProgram = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (withProgram)
            {
                zip.CreateEntryFromFile(typeof(MailArchive).Assembly.Location, "MailVault.Core.dll");
                using (var w = new StreamWriter(zip.CreateEntry("MailVault.exe").Open())) w.Write("MZ fake");
                using (var w = new StreamWriter(zip.CreateEntry("runtimes/win-x64/native/x.dll").Open())) w.Write("native");
                using (var w = new StreamWriter(zip.CreateEntry("Installieren.cmd").Open())) w.Write("@echo off");
            }
            else using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("leer");
            if (withUserFiles)
            {
                using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open())) w.Write("{}");
                using (var w = new StreamWriter(zip.CreateEntry("portable.txt").Open())) w.Write("");
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Offers_newest_finished_version_with_changes_of_all_skipped_versions_in_the_users_language()
    {
        await using var srv = new FakeServer();
        srv.Add("0.1.0", Package());
        srv.Add("0.2.0", Package());
        srv.Add("0.3.0", Package());
        srv.Add("0.4.0-beta.1", Package());

        var check = await srv.Manager("0.1.0").CheckAsync(default, "en");
        Assert.True(check.Available);
        Assert.Equal("0.3.0", check.TargetVersion);
        Assert.Equal(new[] { "New in 0.3.0", "New in 0.2.0" }, check.Changes);
        Assert.EndsWith("/download/MailVault-0.3.0-win-x64.zip", check.Target!.Package.Url);  // relativ zur Liste aufgelöst

        // Französisch fehlt -> Englisch; Deutsch vorhanden -> Deutsch
        Assert.Equal("New in 0.3.0", (await srv.Manager("0.2.0").CheckAsync(default, "fr")).Changes[0]);
        Assert.Equal("Neu in 0.3.0", (await srv.Manager("0.2.0").CheckAsync(default, "de")).Changes[0]);

        // Vorabversionen nur im Beta-Kanal
        Assert.Equal("0.4.0-beta.1", (await srv.Manager("0.3.0", "beta").CheckAsync(default)).TargetVersion);
        Assert.Equal("0.4.0-beta.1", (await srv.Manager("0.3.0", "main").CheckAsync(default)).TargetVersion); // alter Kanalname
        Assert.False((await srv.Manager("0.3.0").CheckAsync(default)).Available);
    }

    [Fact]
    public async Task Same_or_newer_version_gets_no_update()
    {
        await using var srv = new FakeServer();
        srv.Add("0.3.0", Package());
        Assert.False((await srv.Manager("0.3.0").CheckAsync(default)).Available);
        Assert.False((await srv.Manager("0.4.0").CheckAsync(default)).Available);
        Assert.Null((await srv.Manager("0.4.0").CheckAsync(default)).Problem);
    }

    [Fact]
    public async Task Missing_list_and_unreachable_server_are_explained()
    {
        await using var srv = new FakeServer();
        srv.FeedMissing = true;
        Assert.Equal(UpdateText.NoReleaseYet, (await srv.Manager().CheckAsync(default)).Problem);

        var offline = new UpdateManager(new InstallInfo(), @"C:\x", new BuildInfo("0.1.0", "unbekannt"), "http://127.0.0.1:1/updates/", srv.PublicKey);
        Assert.Equal(UpdateText.Unreachable, (await offline.CheckAsync(default)).Problem);
    }

    [Fact]
    public async Task Unsigned_tampered_or_foreign_signed_lists_are_refused()
    {
        await using var srv = new FakeServer();
        srv.Add("0.3.0", Package());

        srv.OmitSignature = true;
        Assert.Equal(UpdateText.BadSignature, (await srv.Manager().CheckAsync(default)).Problem);
        srv.OmitSignature = false;

        // Jemand ändert die Liste auf dem Server (z. B. anderes Paket) -> Signatur passt nicht mehr
        srv.TamperAfterSigning = json => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace("0.3.0", "0.9.0"));
        Assert.Equal(UpdateText.BadSignature, (await srv.Manager().CheckAsync(default)).Problem);
        srv.TamperAfterSigning = null;

        // Mit einem fremden Schlüssel signiert: der eingebaute öffentliche Schlüssel lehnt ab
        var check = await srv.Manager(publicKey: UpdateFeed.PublicKey).CheckAsync(default);
        Assert.Equal(UpdateText.BadSignature, check.Problem);

        Assert.True((await srv.Manager().CheckAsync(default)).Available);
    }

    [Fact]
    public async Task Unencrypted_servers_are_refused_except_this_pc()
    {
        var m = new UpdateManager(new InstallInfo(), @"C:\x", new BuildInfo("0.1.0", "unbekannt"), "http://mailvault.example/updates/");
        Assert.Equal(UpdateText.InsecureServer, (await m.CheckAsync(default)).Problem);
        Assert.Equal("https://mailarchy.zerze.de/updates/stable.json",
            new UpdateManager(new InstallInfo(), @"C:\x").FeedUrl.ToString());
        Assert.Equal("https://mailarchy.zerze.de/updates/beta.json",
            new UpdateManager(new InstallInfo { Channel = "beta" }, @"C:\x").FeedUrl.ToString());
    }

    /// <summary>Antwortet nur für einen Rechnernamen; alle anderen gelten als nicht erreichbar.</summary>
    sealed class OnlyHostHandler(string host, Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host != host) throw new HttpRequestException("nicht erreichbar");
            return Task.FromResult(respond(request.RequestUri));
        }
    }

    [Fact]
    public async Task Updates_come_from_the_former_server_while_the_new_name_is_unreachable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var feed = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["product"] = "MailArchy", ["channel"] = "stable",
            ["versions"] = new JsonArray(new JsonObject
            {
                ["version"] = "9.0.0",
                ["package"] = new JsonObject { ["url"] = "../download/MailArchy-9.0.0-win-x64.zip", ["size"] = 5, ["sha256"] = new string('a', 64) },
            }),
        }.ToJsonString());
        var handler = new OnlyHostHandler("mailvault.zerze.de", u => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = u.AbsolutePath.EndsWith(".sig") ? new StringContent(UpdateFeed.Sign(feed, key)) : new ByteArrayContent(feed),
        });
        var m = new UpdateManager(new InstallInfo(), @"C:\x", new BuildInfo("0.7.2", "abcdef1234567"),
            publicKey: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), handler: handler);

        var check = await m.CheckAsync(default, "de");

        Assert.Null(check.Problem);
        Assert.True(check.Available);
        Assert.Equal("mailarchy.zerze.de", handler.Requested[0]);
        Assert.Equal("https://mailvault.zerze.de/download/MailArchy-9.0.0-win-x64.zip", check.Target!.Package.Url);
    }

    [Fact]
    public async Task Download_checks_checksum_and_strips_user_files_from_package()
    {
        await using var srv = new FakeServer();
        srv.Add(PackagedVersion, Package(withUserFiles: true));
        using var tmp = new TempDir();
        var m = srv.Manager("0.0.1");
        var reports = new List<string>();
        var staging = await m.DownloadAsync(await m.CheckAsync(default), new SyncProgress<(string Text, double? Percent)>(r => reports.Add(r.Text)), default, tmp.Path);
        Assert.True(File.Exists(Path.Combine(staging, "MailVault.exe")));
        Assert.True(File.Exists(Path.Combine(staging, "runtimes", "win-x64", "native", "x.dll")));
        Assert.False(File.Exists(Path.Combine(staging, "settings.json")));
        Assert.False(File.Exists(Path.Combine(staging, "portable.txt")));
        Assert.Contains(reports, r => r == UpdateText.Verifying);
        Assert.Empty(Directory.GetFiles(tmp.Path));
    }

    [Fact]
    public async Task Simultaneous_downloads_share_one_package_and_a_locked_old_file_does_not_block()
    {
        // Hintergrund-Update lädt, während der Benutzer auf „Installieren“ klickt (Fehler in 0.6.0: Datei in Benutzung)
        await using var srv = new FakeServer();
        srv.Add(PackagedVersion, Package());
        using var tmp = new TempDir();
        var m = srv.Manager("0.0.1");
        var check = await m.CheckAsync(default);
        using var locked = new FileStream(Path.Combine(tmp.Path, check.Target!.Package.FileName), FileMode.Create, FileAccess.Write, FileShare.None);
        var both = await Task.WhenAll(m.DownloadAsync(check, null, default, tmp.Path), srv.Manager("0.0.1").DownloadAsync(check, null, default, tmp.Path));
        Assert.Equal(both[0], both[1]);
        Assert.True(File.Exists(Path.Combine(both[0], "MailVault.exe")));
    }

    [Fact]
    public async Task Damaged_packages_are_refused()
    {
        await using var srv = new FakeServer();
        srv.Add(PackagedVersion, Package(), sha: new string('0', 64));
        using var tmp = new TempDir();
        var m = srv.Manager("0.0.1");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await m.DownloadAsync(await m.CheckAsync(default), null, default, tmp.Path));
        Assert.Equal(UpdateText.ChecksumMismatch, ex.Message);
        Assert.Empty(Directory.GetFiles(tmp.Path));
        Assert.Empty(Directory.GetDirectories(tmp.Path));
    }

    [Fact]
    public async Task Package_must_contain_the_announced_program_version()
    {
        await using var srv = new FakeServer();
        srv.Add("9.9.9", Package());
        using var tmp = new TempDir();
        var m = srv.Manager("0.0.1");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await m.DownloadAsync(await m.CheckAsync(default), null, default, tmp.Path));
        Assert.Equal(UpdateText.WrongVersion(PackagedVersion, "9.9.9"), ex.Message);

        await using var srv2 = new FakeServer();
        srv2.Add(PackagedVersion, Package(withProgram: false));
        m = srv2.Manager("0.0.1");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await m.DownloadAsync(await m.CheckAsync(default), null, default, tmp.Path));
        Assert.Equal(UpdateText.NoProgramInPackage, ex.Message);
    }

    [Fact]
    public void Feed_entries_without_valid_checksum_or_version_are_skipped()
    {
        var json = """
            {"versions":[
              {"version":"1.0.0","package":{"url":"https://cdn.example/a.zip","size":5,"sha256":"AB"}},
              {"version":"latest","package":{"url":"a.zip","size":5,"sha256":"%"}},
              {"version":"1.1.0","notes":{"de":["x"]},"package":{"url":"../download/b.zip","size":5,"sha256":"%"}}
            ]}
            """.Replace("%", new string('a', 64));
        var list = UpdateFeed.Parse(json, new Uri("https://mailvault.zerze.de/updates/stable.json"));
        var only = Assert.Single(list);
        Assert.Equal("1.1.0", only.Version.ToString());
        Assert.Equal("https://mailvault.zerze.de/download/b.zip", only.Package.Url);
        Assert.Equal("b.zip", only.Package.FileName);
    }

    [Fact]
    public void Signature_round_trip_and_built_in_key_is_valid()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        var data = Encoding.UTF8.GetBytes("{\"versions\":[]}");
        var sig = UpdateFeed.Sign(data, ec);
        Assert.True(UpdateFeed.VerifySignature(data, sig, pub));
        Assert.False(UpdateFeed.VerifySignature(data, sig, UpdateFeed.PublicKey));
        Assert.False(UpdateFeed.VerifySignature(data, "kein base64!", pub));

        using var builtIn = ECDsa.Create();
        builtIn.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateFeed.PublicKey), out _);
        Assert.Equal(256, builtIn.KeySize);
    }

    [Fact]
    public void First_installation_copies_program_and_keeps_user_files()
    {
        using var tmp = new TempDir();
        var src = Path.Combine(tmp.Path, "Downloads", "MailVault");
        var dst = Path.Combine(tmp.Path, "Programs", "MailVault");
        Directory.CreateDirectory(Path.Combine(src, "runtimes"));
        File.WriteAllText(Path.Combine(src, "MailVault.exe"), "neu");
        File.WriteAllText(Path.Combine(src, "runtimes", "a.dll"), "neu");
        File.WriteAllText(Path.Combine(src, "settings.json"), "aus dem Paket");

        Installer.Install(src, dst);
        Assert.Equal("neu", File.ReadAllText(Path.Combine(dst, "runtimes", "a.dll")));
        Assert.False(File.Exists(Path.Combine(dst, "settings.json")));
        var info = InstallInfo.Load(dst)!;
        Assert.Equal("stable", info.Channel);

        // Erneute Installation: portable Daten, Einstellungen und gewählter Kanal bleiben
        Directory.CreateDirectory(Path.Combine(dst, "Data"));
        File.WriteAllText(Path.Combine(dst, "Data", "archive.db"), "ARCHIV");
        File.WriteAllText(Path.Combine(dst, "settings.json"), "MEINE");
        info.Channel = "beta";
        info.Save(dst);
        Directory.CreateDirectory(Path.Combine(src, "Data"));
        File.WriteAllText(Path.Combine(src, "Data", "archive.db"), "FREMD");
        File.WriteAllText(Path.Combine(src, "MailVault.exe"), "noch neuer");
        Installer.Install(src, dst);
        Assert.Equal("noch neuer", File.ReadAllText(Path.Combine(dst, "MailVault.exe")));
        Assert.Equal("ARCHIV", File.ReadAllText(Path.Combine(dst, "Data", "archive.db")));
        Assert.Equal("MEINE", File.ReadAllText(Path.Combine(dst, "settings.json")));
        Assert.Equal("beta", InstallInfo.Load(dst)!.Channel);
    }

    [Fact]
    public void Old_install_json_from_github_updates_still_loads()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, InstallInfo.FileName),
            """{"RepoUrl":"test/mailvault","Channel":"main","SourceDirectory":"","InstalledUtc":"2026-11-17T10:00:00Z"}""");
        var info = InstallInfo.Load(tmp.Path)!;
        Assert.True(info.IncludesPrereleases);
        Assert.Equal("", info.FeedUrl);
        Assert.Equal("https://mailarchy.zerze.de/updates/beta.json", new UpdateManager(info, tmp.Path).FeedUrl.ToString());
    }

    [Theory]
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("1.0.0", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.1", "1.0.0-alpha.5")]
    [InlineData("v2.0.0", "1.99.99")]
    public void Version_order(string newer, string older) =>
        Assert.True(SemVersion.Parse(newer)!.CompareTo(SemVersion.Parse(older)) > 0, $"{newer} > {older}");

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("v1.2", false)]
    [InlineData("latest", false)]
    public void Version_tags(string tag, bool valid) => Assert.Equal(valid, SemVersion.Parse(tag) is not null);

    [Theory]
    [InlineData("0.3.0+d4430b46238fe874451d17abcb67319570c57a33", "0.3.0", "d4430b46238fe874451d17abcb67319570c57a33")]
    [InlineData("0.3.0", "0.3.0", "unbekannt")]
    [InlineData("0.4.0-beta.1+abcdef1", "0.4.0-beta.1", "abcdef1")]
    [InlineData("1.2.3+local-build", "1.2.3", "unbekannt")]
    [InlineData(null, "0.0.0", "unbekannt")]
    public void Build_info_parsing(string? informational, string version, string commit)
    {
        var b = BuildInfo.Parse(informational);
        Assert.Equal(version, b.Version);
        Assert.Equal(commit, b.Commit);
    }

    [Fact]
    public void Running_build_knows_its_commit()
    {
        // Die Tests werden aus dem Git-Repository gebaut -> das SDK hat den Commit eingetragen
        var b = BuildInfo.FromFile(typeof(MailArchive).Assembly.Location);
        Assert.Matches("^[0-9a-f]{40}$", b.Commit);
    }

    [Fact]
    public void Apply_script_never_touches_data_and_can_roll_back()
    {
        var m = new UpdateManager(new InstallInfo(), @"C:\Programme\MailVault", new BuildInfo("0.3.0", "abcdef1234567"));
        var path = m.WriteApplyScript(@"C:\Temp\staging", 4242, "0.4.0", "--background");
        try
        {
            var s = File.ReadAllText(path);
            Assert.Contains("Wait-Process -Id 4242", s);
            Assert.Contains("/XD Data", s);
            foreach (var name in UpdateManager.PreservedNames.Where(n => n != "Data")) Assert.Contains(name, s);
            Assert.Contains("'--updated --background'", s);         // startet wieder im Infobereich
            Assert.Contains("'--update-failed --background'", s);   // Rückfall auf die alte Version
            Assert.Contains("0.3.0-abcdef1", s);                    // Sicherung der alten Programmdateien
            Assert.Contains("'C:\\Programme\\MailVault'", s);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Archive_is_backed_up_before_format_upgrade_and_newer_format_is_refused()
    {
        using var tmp = new TempDir();
        var raw = ArchiveTests.MakeMessage("Alt", "Text");
        using (var a = MailArchive.Open(tmp.Path)) a.StoreMessage(a.EnsureFolderPath(new[] { "X" }), raw);
        var db = Path.Combine(tmp.Path, MailArchive.DbFileName);

        // Archiv "älter" machen -> beim Öffnen Sicherung und Umstellung
        SetSchema(db, MailArchive.CurrentSchemaVersion - 1);
        using (var a = MailArchive.Open(tmp.Path)) Assert.Single(a.Search(new SearchCriteria { Query = "Alt" }));
        var backup = db + $".vor-update-format{MailArchive.CurrentSchemaVersion - 1}.bak";
        Assert.True(File.Exists(backup));
        using (var cn = new SqliteConnection($"Data Source={backup};Pooling=false;Mode=ReadOnly"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }

        // Archiv aus einer "zukünftigen" Version: nicht öffnen, nicht verändern
        SetSchema(db, MailArchive.CurrentSchemaVersion + 1);
        var before = File.ReadAllBytes(db);
        var ex = Assert.Throws<InvalidOperationException>(() => MailArchive.Open(tmp.Path));
        Assert.Contains("neueren MailArchy-Version", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(db));
    }

    static void SetSchema(string db, int version)
    {
        using var cn = new SqliteConnection($"Data Source={db};Pooling=false");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"UPDATE meta SET value = '{version}' WHERE key = 'schema_version'; PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
