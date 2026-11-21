using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MailVault.Core.Updates;

/// <summary>Installationsdaten neben der MailVault.exe (install.json), angelegt beim Installieren.</summary>
public sealed class InstallInfo
{
    public const string FileName = "install.json";

    /// <summary>"stable" = nur fertige Versionen, "beta" = auch Vorabversionen (alt: "main").</summary>
    public string Channel { get; set; } = "stable";
    /// <summary>Anderer Update-Server (leer = mailvault.zerze.de). Nur für Tests oder einen eigenen Spiegel.</summary>
    public string FeedUrl { get; set; } = "";
    /// <summary>Nicht mehr verwendet (frühere Updates über GitHub); bleibt für ältere install.json lesbar.</summary>
    public string RepoUrl { get; set; } = "";
    /// <summary>Nicht mehr verwendet (frühere Updates aus dem Quelltext); bleibt für ältere install.json lesbar.</summary>
    public string SourceDirectory { get; set; } = "";
    public DateTime? InstalledUtc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IncludesPrereleases => Channel is "beta" or "main";

    public static string PathFor(string installDir) => Path.Combine(installDir, FileName);

    public static InstallInfo? Load(string installDir)
    {
        var p = PathFor(installDir);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<InstallInfo>(File.ReadAllText(p)) : null; }
        catch (JsonException) { return null; }
    }

    public void Save(string installDir) =>
        File.WriteAllText(PathFor(installDir), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Version und Git-Stand dieses Programms (aus der Produktversion "0.5.0+&lt;commit&gt;").</summary>
public sealed record BuildInfo(string Version, string Commit)
{
    public string ShortCommit => Commit.Length >= 7 && Commit != "unbekannt" ? Commit[..7] : Commit;
    public string Display => Commit == "unbekannt" ? Version : $"{Version} ({ShortCommit})";

    public static BuildInfo Current { get; } =
        Parse(typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Liest eine gebaute DLL, ohne sie zu laden.</summary>
    public static BuildInfo FromFile(string path) => Parse(FileVersionInfo.GetVersionInfo(path).ProductVersion);

    public static BuildInfo Parse(string? informational)
    {
        var parts = (informational ?? "0.0.0").Split('+', 2);
        var commit = parts.Length == 2 && Regex.IsMatch(parts[1], "^[0-9a-fA-F]{7,40}$") ? parts[1].ToLowerInvariant() : "unbekannt";
        return new BuildInfo(parts[0], commit);
    }
}

/// <summary>Versionsnummer nach SemVer (1.2.3 oder 1.2.3-beta.1); Vorabversionen sind kleiner als die fertige Version.</summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string Pre) : IComparable<SemVersion>
{
    static readonly Regex Pattern = new(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$", RegexOptions.Compiled);

    public bool IsPrerelease => Pre.Length > 0;

    public static SemVersion? Parse(string? s)
    {
        var m = Pattern.Match(s?.Trim() ?? "");
        return m.Success ? new SemVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Value) : null;
    }

    public int CompareTo(SemVersion? o)
    {
        if (o is null) return 1;
        var c = Major.CompareTo(o.Major);
        if (c == 0) c = Minor.CompareTo(o.Minor);
        if (c == 0) c = Patch.CompareTo(o.Patch);
        if (c != 0) return c;
        if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;   // 1.0.0 > 1.0.0-beta
        if (o.Pre.Length == 0) return -1;
        var a = Pre.Split('.');
        var b = o.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], out var ai);
            var bn = int.TryParse(b[i], out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}

/// <summary>Ein Update-Paket aus der Update-Liste.</summary>
public sealed record ReleasePackage(string Url, long Size, string Sha256)
{
    public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath);
}

/// <summary>Eine Version aus der Update-Liste mit ihren Änderungen je Sprache ("de", "en", …).</summary>
public sealed record ReleaseInfo(SemVersion Version, DateTime? PublishedUtc, IReadOnlyDictionary<string, IReadOnlyList<string>> Notes, ReleasePackage Package)
{
    public bool Prerelease => Version.IsPrerelease;

    /// <summary>Änderungen in der gewünschten Sprache, sonst Englisch, sonst Deutsch, sonst die erste vorhandene.</summary>
    public IReadOnlyList<string> NotesFor(string language)
    {
        foreach (var l in new[] { language, "en", "de" })
            if (Notes.TryGetValue(l, out var n) && n.Count > 0) return n;
        return Notes.Values.FirstOrDefault() ?? Array.Empty<string>();
    }
}

public sealed record UpdateCheck(bool Available, ReleaseInfo? Target, IReadOnlyList<string> Changes, string? Problem = null)
{
    public string TargetVersion => Target?.Version.ToString() ?? "";
}

/// <summary>Die Update-Liste (stable.json / beta.json) mit Signaturprüfung.</summary>
public static class UpdateFeed
{
    /// <summary>
    /// Öffentlicher Schlüssel (ECDSA P-256, SubjectPublicKeyInfo, Base64), mit dem jede Update-Liste signiert sein muss.
    /// Der private Schlüssel liegt nur auf dem Rechner, der Versionen veröffentlicht (siehe docs/UPDATES.md). Ohne gültige
    /// Signatur wird nichts installiert – auch dann nicht, wenn jemand den Server übernimmt.
    /// </summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYJvntFRjqYq+mxIXEiJEWIsTE8mlduT7LzoXb78WwfrlG+FV/sdgydVR/moly0xXg/6Y6DFoNgzjqat0Bb6MHg==";

    public static bool VerifySignature(byte[] data, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ec.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public static string Sign(byte[] data, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Liest die Versionen der Update-Liste; relative Paketadressen werden gegen <paramref name="feedUrl"/> aufgelöst.</summary>
    public static List<ReleaseInfo> Parse(string json, Uri feedUrl)
    {
        var list = new List<ReleaseInfo>();
        if (JsonNode.Parse(json) is not JsonObject root || root["versions"] is not JsonArray versions) return list;
        foreach (var v in versions.OfType<JsonObject>())
        {
            if (SemVersion.Parse((string?)v["version"]) is not { } ver) continue;
            if (v["package"] is not JsonObject pkg || (string?)pkg["url"] is not { Length: > 0 } url) continue;
            var sha = ((string?)pkg["sha256"] ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(sha, "^[0-9a-f]{64}$")) continue;
            var notes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (v["notes"] is JsonObject n)
                foreach (var (lang, lines) in n)
                    if (lines is JsonArray arr) notes[lang] = arr.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList();
            DateTime? published = DateTime.TryParse((string?)v["published"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var p) ? p : null;
            list.Add(new ReleaseInfo(ver, published, notes, new ReleasePackage(new Uri(feedUrl, url).ToString(), (long?)pkg["size"] ?? 0, sha)));
        }
        return list;
    }
}

/// <summary>
/// Updates von mailvault.zerze.de: Dort liegen die Update-Listen (updates/stable.json, updates/beta.json), jeweils mit
/// Signatur (.sig), und die fertigen Pakete (MailVault-x.y.z-win-x64.zip). MailVault prüft die Signatur, vergleicht die
/// Versionen, lädt das Paket, kontrolliert die Prüfsumme aus der signierten Liste und lässt es von einem kleinen Skript
/// nach dem Beenden über die Programmdateien kopieren. Archiv, Einstellungen und portable Daten werden nie angefasst;
/// die alten Programmdateien bleiben als Sicherung.
/// </summary>
public sealed class UpdateManager
{
    public const string DefaultFeedBase = "https://mailvault.zerze.de/updates/";
    public const string PackageSuffix = "-win-x64.zip";

    readonly InstallInfo _install;
    readonly string _installDir;
    readonly BuildInfo _current;
    readonly Uri _feedBase;
    readonly string _publicKey;
    readonly HttpMessageHandler _handler;

    public UpdateManager(InstallInfo install, string installDir, BuildInfo? current = null,
        string? feedBase = null, string? publicKey = null, HttpMessageHandler? handler = null)
    {
        _install = install;
        _installDir = installDir;
        _current = current ?? BuildInfo.Current;
        var baseUrl = feedBase ?? FeedBaseFromEnvironment ?? (install.FeedUrl is { Length: > 0 } f ? f : DefaultFeedBase);
        _feedBase = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        _publicKey = publicKey ?? UpdateFeed.PublicKey;
        _handler = handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    }

    /// <summary>Nur für Tests der Update-Funktion: anderer Update-Server.</summary>
    public static string? FeedBaseFromEnvironment => Environment.GetEnvironmentVariable("MAILVAULT_UPDATE_URL") is { Length: > 0 } s ? s : null;

    public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailVault", "update");
    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailVault", "backup");
    /// <summary>Installationsordner (MAILVAULT_INSTALL_DIR nur zum Testen der Installation).</summary>
    public static string DefaultInstallDir => Environment.GetEnvironmentVariable("MAILVAULT_INSTALL_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MailVault");

    public Uri FeedUrl => new(_feedBase, _install.IncludesPrereleases ? "beta.json" : "stable.json");

    /// <summary>Nur verschlüsselte Verbindungen; unverschlüsselt nur zu diesem PC (Tests).</summary>
    static bool IsAllowed(Uri u) => u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);

    HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient(_handler, disposeHandler: false) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MailVault-Updater/{_current.Version}");
        return http;
    }

    /// <summary>Fragt die Update-Liste ab, prüft die Signatur und vergleicht mit der laufenden Version.</summary>
    /// <param name="language">Sprache der Änderungsliste ("de", "en", "fr", "es").</param>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct, string? language = null)
    {
        language ??= CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var feedUrl = FeedUrl;
        if (!IsAllowed(feedUrl)) return Problem(UpdateText.InsecureServer);
        using var http = Client(TimeSpan.FromSeconds(60));
        byte[] json;
        string signature;
        try
        {
            using var resp = await http.GetAsync(feedUrl, ct);
            if (resp.StatusCode is HttpStatusCode.NotFound) return Problem(UpdateText.NoReleaseYet);
            if (!resp.IsSuccessStatusCode) return Problem(UpdateText.ServerError((int)resp.StatusCode));
            json = await resp.Content.ReadAsByteArrayAsync(ct);
            using var sigResp = await http.GetAsync(feedUrl + ".sig", ct);
            signature = sigResp.IsSuccessStatusCode ? await sigResp.Content.ReadAsStringAsync(ct) : "";
        }
        catch (HttpRequestException) { return Problem(UpdateText.Unreachable); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return Problem(UpdateText.Unreachable); }

        if (!UpdateFeed.VerifySignature(json, signature, _publicKey))
            return Problem(UpdateText.BadSignature);

        var releases = UpdateFeed.Parse(Encoding.UTF8.GetString(json), feedUrl)
            .Where(r => !r.Prerelease || _install.IncludesPrereleases)
            .OrderByDescending(r => r.Version).ToList();
        if (releases.Count == 0) return Problem(UpdateText.NoReleaseYet);

        var target = releases[0];
        var current = SemVersion.Parse(_current.Version);
        if (current is not null && target.Version.CompareTo(current) <= 0)
            return new UpdateCheck(false, target, Array.Empty<string>());

        // Änderungen aller übersprungenen Versionen (neueste zuerst)
        var changes = releases.Where(r => current is null || r.Version.CompareTo(current) > 0)
            .SelectMany(r => r.NotesFor(language)).Distinct().Take(60).ToList();
        return new UpdateCheck(true, target, changes);

        static UpdateCheck Problem(string text) => new(false, null, Array.Empty<string>(), text);
    }

    /// <summary>Lädt das Paket, prüft die Prüfsumme und entpackt es in einen Bereitstellungsordner. Liefert dessen Pfad.</summary>
    public async Task<string> DownloadAsync(UpdateCheck check, IProgress<(string Text, double? Percent)>? progress, CancellationToken ct, string? workRoot = null)
    {
        var release = check.Target ?? throw new InvalidOperationException(UpdateText.NothingSelected);
        var url = new Uri(release.Package.Url);
        if (!IsAllowed(url)) throw new InvalidOperationException(UpdateText.InsecureServer);
        var root = workRoot ?? WorkRoot;
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, release.Package.FileName);
        using var http = Client(Timeout.InfiniteTimeSpan);

        progress?.Report((UpdateText.Downloading(release.Version.ToString()), 0));
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(UpdateText.PackageMissing((int)resp.StatusCode));
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var total = resp.Content.Headers.ContentLength ?? release.Package.Size;
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            var lastReport = 0d;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0 && (done * 100d / total) - lastReport >= 1)
                {
                    lastReport = done * 100d / total;
                    progress?.Report((UpdateText.DownloadingProgress(release.Version.ToString(), done / 1048576d, total / 1048576d), lastReport));
                }
            }
        }

        progress?.Report((UpdateText.Verifying, null));
        string actual;
        await using (var f = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (actual != release.Package.Sha256)
        {
            File.Delete(zipPath);
            throw new InvalidOperationException(UpdateText.ChecksumMismatch);
        }

        progress?.Report((UpdateText.Extracting, null));
        var staging = Path.Combine(root, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging); // verweigert Pfade außerhalb des Zielordners
        File.Delete(zipPath);

        // Pakete mit einem einzelnen Unterordner akzeptieren
        var appDir = staging;
        if (!File.Exists(Path.Combine(appDir, "MailVault.exe")) && Directory.GetFiles(appDir).Length == 0 && Directory.GetDirectories(appDir) is [var only])
            appDir = only;
        if (!File.Exists(Path.Combine(appDir, "MailVault.exe")) || !File.Exists(Path.Combine(appDir, "MailVault.Core.dll")))
            throw new InvalidOperationException(UpdateText.NoProgramInPackage);
        var built = BuildInfo.FromFile(Path.Combine(appDir, "MailVault.Core.dll"));
        if (built.Version != release.Version.ToString())
            throw new InvalidOperationException(UpdateText.WrongVersion(built.Version, release.Version.ToString()));
        foreach (var name in PreservedNames)
        {
            var p = Path.Combine(appDir, name);
            if (File.Exists(p)) File.Delete(p); // ein Paket darf keine Benutzerdateien mitbringen
            else if (Directory.Exists(p)) Directory.Delete(p, true);
        }
        return appDir;
    }

    /// <summary>Dateien und Ordner im Programmordner, die ein Update nie überschreibt oder löscht.</summary>
    public static readonly string[] PreservedNames = { "Data", "portable.txt", "settings.json", InstallInfo.FileName };

    /// <summary>
    /// Schreibt das Installationsskript: wartet auf das Programmende, sichert die alten Programmdateien, kopiert die neuen,
    /// startet MailVault neu und stellt bei einem Fehlstart die alte Version wieder her.
    /// </summary>
    public string WriteApplyScript(string staging, int processId, string newVersion, string restartArguments = "")
    {
        var backup = Path.Combine(BackupRoot, $"{_current.Version}-{_current.ShortCommit}-{DateTime.Now:yyyyMMdd-HHmmss}");
        var log = Path.Combine(BackupRoot, "update.log");
        var exclude = string.Join(" ", PreservedNames.Where(n => n != "Data"));
        var updatedArgs = ("--updated " + restartArguments).Trim();
        var failedArgs = ("--update-failed " + restartArguments).Trim();
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$install = {Ps(_installDir)}; $staging = {Ps(staging)}; $backup = {Ps(backup)}; $log = {Ps(log)}");
        script.AppendLine("New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null");
        script.AppendLine("function Log($m) { Add-Content -Path $log -Value (\"{0:u} {1}\" -f (Get-Date), $m) }");
        script.AppendLine($"Log 'Update auf {newVersion} beginnt'");
        script.AppendLine($"try {{ Wait-Process -Id {processId} -Timeout 120 -ErrorAction SilentlyContinue }} catch {{}}");
        script.AppendLine("Start-Sleep -Milliseconds 500");
        // Sichern (ohne Daten), dann neue Dateien spiegeln (ohne die geschützten Namen)
        script.AppendLine($"robocopy $install $backup /E /XD Data /XF {exclude} /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null");
        script.AppendLine("if ($LASTEXITCODE -ge 8) { Log 'Sicherung fehlgeschlagen'; Start-Process (Join-Path $install 'MailVault.exe'); exit 1 }");
        script.AppendLine($"robocopy $staging $install /MIR /XD Data /XF {exclude} /R:5 /W:1 /NFL /NDL /NJH /NJS | Out-Null");
        script.AppendLine("$copyOk = $LASTEXITCODE -lt 8");
        script.AppendLine("$started = $false");
        script.AppendLine($"if ($copyOk) {{ $p = Start-Process (Join-Path $install 'MailVault.exe') -ArgumentList {Ps(updatedArgs)} -PassThru; Start-Sleep 12; $started = -not $p.HasExited -or $p.ExitCode -eq 0 }}");
        script.AppendLine("if (-not $started) {");
        script.AppendLine("  Log 'Neue Version startet nicht - alte Version wird wiederhergestellt'");
        script.AppendLine($"  robocopy $backup $install /MIR /XD Data /XF {exclude} /R:5 /W:1 /NFL /NDL /NJH /NJS | Out-Null");
        script.AppendLine($"  Start-Process (Join-Path $install 'MailVault.exe') -ArgumentList {Ps(failedArgs)}");
        script.AppendLine("  exit 2");
        script.AppendLine("}");
        script.AppendLine($"Log 'Update auf {newVersion} abgeschlossen'");
        script.AppendLine("Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue");
        // Nur die letzten drei Sicherungen behalten
        script.AppendLine("Get-ChildItem (Split-Path $backup) -Directory | Sort-Object CreationTime -Descending | Select-Object -Skip 3 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue");
        var path = Path.Combine(Path.GetTempPath(), $"mailvault-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>PowerShell-String in einfachen Anführungszeichen.</summary>
    static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Startet das Installationsskript unsichtbar; danach muss sich MailVault beenden.</summary>
    public static void LaunchApplyScript(string scriptPath) =>
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
}

/// <summary>Erstinstallation aus einem entpackten Paket (MailVault.exe --install bzw. Installieren.cmd).</summary>
public static class Installer
{
    /// <summary>
    /// Kopiert die Programmdateien aus <paramref name="sourceDir"/> nach <paramref name="targetDir"/> und legt install.json an
    /// (eine vorhandene bleibt erhalten). Benutzerdateien im Zielordner werden nie überschrieben.
    /// </summary>
    public static void Install(string sourceDir, string targetDir)
    {
        sourceDir = Path.GetFullPath(sourceDir).TrimEnd('\\', '/');
        targetDir = Path.GetFullPath(targetDir).TrimEnd('\\', '/');
        if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var top = rel.Split(Path.DirectorySeparatorChar)[0];
            if (UpdateManager.PreservedNames.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            var dst = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
        var info = InstallInfo.Load(targetDir) ?? new InstallInfo { Channel = "stable" };
        info.InstalledUtc ??= DateTime.UtcNow;
        info.Save(targetDir);
    }
}
