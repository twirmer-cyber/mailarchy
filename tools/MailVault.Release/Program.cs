using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailVault.Core.Updates;

// Veröffentlichungswerkzeug für MailVault (siehe docs/UPDATES.md).
//
//   keygen  [--key <datei>]
//       Erzeugt den Signaturschlüssel (ECDSA P-256). Der private Teil bleibt in der Datei, der öffentliche wird
//       ausgegeben und gehört in UpdateFeed.PublicKey. Nur einmal nötig; den Schlüssel sicher aufbewahren.
//
//   feed    --zip <paket.zip> --version <x.y.z> --site <ordner> [--notes <notes.json>] [--key <datei>]
//       Trägt die Version in <site>/updates/beta.json (alle) und – bei fertigen Versionen – stable.json ein,
//       kopiert das Paket nach <site>/download/ und signiert beide Listen (.sig).
//
//   verify  --site <ordner>
//       Prüft die Signaturen der Listen gegen den eingebauten öffentlichen Schlüssel.

var opts = ParseArgs(args.Skip(1).ToArray());
var keyPath = opts.GetValueOrDefault("key") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailVault-Release", "update-signing-key.txt");

switch (args.FirstOrDefault())
{
    case "keygen": return KeyGen(keyPath);
    case "feed": return Feed(opts, keyPath);
    case "verify": return Verify(opts);
    default:
        Console.Error.WriteLine("Aufruf: keygen | feed --zip … --version … --site … [--notes …] | verify --site …");
        return 2;
}

static int KeyGen(string keyPath)
{
    if (File.Exists(keyPath)) { Console.Error.WriteLine($"Es gibt bereits einen Schlüssel: {keyPath} – wird nicht überschrieben."); return 1; }
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllText(keyPath, Convert.ToBase64String(ec.ExportPkcs8PrivateKey()));
    Console.WriteLine($"Privater Schlüssel: {keyPath}");
    Console.WriteLine($"Öffentlicher Schlüssel (für UpdateFeed.PublicKey): {Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo())}");
    return 0;
}

static int Feed(Dictionary<string, string> o, string keyPath)
{
    var zip = Req(o, "zip"); var version = Req(o, "version"); var site = Req(o, "site");
    if (SemVersion.Parse(version) is not { } ver) throw new ArgumentException($"Ungültige Version: {version}");
    if (!File.Exists(keyPath)) throw new FileNotFoundException($"Signaturschlüssel fehlt: {keyPath} (zuerst 'keygen')");
    using var ec = ECDsa.Create();
    ec.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), out _);
    if (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()) != UpdateFeed.PublicKey)
        throw new InvalidOperationException("Der Signaturschlüssel passt nicht zum öffentlichen Schlüssel in UpdateFeed.PublicKey.");

    var updates = Path.Combine(site, "updates");
    var download = Path.Combine(site, "download");
    Directory.CreateDirectory(updates);
    Directory.CreateDirectory(download);
    var zipName = Path.GetFileName(zip);
    File.Copy(zip, Path.Combine(download, zipName), overwrite: true);
    var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();

    var notes = new JsonObject();
    if (o.GetValueOrDefault("notes") is { } notesPath && File.Exists(notesPath))
        notes = JsonNode.Parse(File.ReadAllText(notesPath)) as JsonObject ?? new JsonObject();
    var entry = new JsonObject
    {
        ["version"] = ver.ToString(),
        ["published"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["notes"] = notes,
        ["package"] = new JsonObject
        {
            ["url"] = "../download/" + zipName,
            ["size"] = new FileInfo(zip).Length,
            ["sha256"] = sha,
        },
    };

    foreach (var channel in new[] { "beta", "stable" })
    {
        if (channel == "stable" && ver.IsPrerelease) continue;
        var path = Path.Combine(updates, channel + ".json");
        JsonObject root;
        try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (JsonException) { Console.WriteLine($"{channel}.json war keine gültige Update-Liste – wird neu angelegt."); root = new JsonObject(); }
        var versions = root["versions"] as JsonArray ?? new JsonArray();
        var kept = versions.OfType<JsonObject>()
            .Where(v => (string?)v["version"] != ver.ToString())
            .Select(v => (JsonObject)v.DeepClone()).ToList();
        kept.Add((JsonObject)entry.DeepClone());
        kept = kept.OrderByDescending(v => SemVersion.Parse((string?)v["version"])).Take(20).ToList();
        var feed = new JsonObject
        {
            ["product"] = "MailVault Home",
            ["channel"] = channel,
            ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["versions"] = new JsonArray(kept.ToArray<JsonNode?>()),
        };
        var bytes = Encoding.UTF8.GetBytes(feed.ToJsonString(new JsonSerializerOptions
            { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".sig", UpdateFeed.Sign(bytes, ec));
        Console.WriteLine($"{channel}.json: {kept.Count} Versionen, neueste {kept[0]["version"]}");
    }
    Console.WriteLine($"Paket: download/{zipName} ({new FileInfo(zip).Length / 1048576d:N1} MB, SHA-256 {sha})");
    return 0;
}

static int Verify(Dictionary<string, string> o)
{
    var ok = true;
    foreach (var channel in new[] { "stable", "beta" })
    {
        var path = Path.Combine(Req(o, "site"), "updates", channel + ".json");
        if (!File.Exists(path)) continue;
        var valid = UpdateFeed.VerifySignature(File.ReadAllBytes(path), File.Exists(path + ".sig") ? File.ReadAllText(path + ".sig") : "", UpdateFeed.PublicKey);
        Console.WriteLine($"{channel}.json: {(valid ? "Signatur gültig" : "SIGNATUR UNGÜLTIG")}");
        ok &= valid;
    }
    return ok ? 0 : 1;
}

static string Req(Dictionary<string, string> o, string name) =>
    o.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new ArgumentException($"--{name} fehlt");

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
        if (a[i].StartsWith("--")) d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "";
    return d;
}
