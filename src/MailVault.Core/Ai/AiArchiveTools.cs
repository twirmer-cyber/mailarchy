using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;

namespace MailVault.Core.Ai;

/// <summary>Beschreibung eines Werkzeugs, das die KI aufrufen darf (Name, Zweck, JSON-Schema der Eingaben).</summary>
public sealed record AiToolSpec(string Name, string Description, JsonObject Properties, string[] Required);

/// <summary>
/// Die Werkzeuge, mit denen Claude das Archiv durchsucht. Sie laufen lokal; an Claude gehen nur die Ergebnisse
/// (Kopfdaten, kurze Ausschnitte und – begrenzt – der Text einzelner E-Mails). Ordnerbeschränkungen gelten für jedes Werkzeug.
/// </summary>
public sealed class AiArchiveTools
{
    const int MaxBodyChars = 15_000;
    const int SnippetChars = 220;

    readonly MailArchive _archive;
    readonly HashSet<long>? _allowedFolders;   // inklusive Unterordner; null = alles
    readonly IReadOnlyCollection<long>? _allowedRoots;
    readonly int _maxReads;
    readonly HashSet<long> _read = new();

    public AiArchiveTools(MailArchive archive, IReadOnlyCollection<long>? allowedRootFolders = null, int maxReads = 25)
    {
        _archive = archive;
        _allowedRoots = allowedRootFolders;
        _allowedFolders = allowedRootFolders is null ? null : archive.GetFolderIdsWithDescendants(allowedRootFolders);
        _maxReads = Math.Max(1, maxReads);
    }

    /// <summary>E-Mails, die in Ergebnissen vorkamen (für die Verlinkung in der Antwort).</summary>
    public HashSet<long> SeenIds { get; } = new();
    public int ReadCount => _read.Count;

    /// <summary>Neue Frage: Lesekontingent zurücksetzen (gesehene IDs bleiben für Rückfragen gültig).</summary>
    public void ResetBudget() => _read.Clear();

    static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    static JsonObject Int(string description, int min, int max) => new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public IReadOnlyList<AiToolSpec> Specs { get; } = new[]
    {
        new AiToolSpec("search_emails",
            "Durchsucht das E-Mail-Archiv (Volltext über Betreff, Absender/Empfänger, Text, Anhangnamen und -inhalte) und liefert Kopfdaten mit kurzem Textausschnitt, neueste zuerst. " +
            "Volltextsyntax: Wörter werden UND-verknüpft und als Präfix gesucht (rechnung findet Rechnungen), \"genaue Phrase\" in Anführungszeichen, OR zwischen Alternativen, -wort schließt aus. " +
            "Für breite Fragen mehrere Suchen mit Synonymen (z. B. Rechnung OR Invoice OR Zahlungserinnerung) durchführen. Alle Filter sind optional.",
            new JsonObject
            {
                ["query"] = Str("Volltext-Suchbegriffe, leer = alle E-Mails (dann mindestens einen Filter angeben)"),
                ["from"] = Str("Absender enthält (Name oder Adresse)"),
                ["to"] = Str("Empfänger enthält (Name oder Adresse)"),
                ["date_from"] = Str("Frühestes Datum, JJJJ-MM-TT"),
                ["date_to"] = Str("Spätestes Datum, JJJJ-MM-TT"),
                ["has_attachments"] = new JsonObject { ["type"] = "boolean", ["description"] = "true = nur mit Anhang, false = nur ohne" },
                ["folder_id"] = new JsonObject { ["type"] = "integer", ["description"] = "Nur in diesem Archivordner (inkl. Unterordner), IDs aus list_folders" },
                ["limit"] = Int("Höchstzahl Treffer (Standard 20)", 1, 50),
            }, Array.Empty<string>()),
        new AiToolSpec("read_email",
            "Liest eine E-Mail vollständig: Kopfzeilen, Text (HTML in Text umgewandelt, sehr lange Texte gekürzt) und Liste der Anhänge. " +
            "Nur für E-Mails verwenden, deren Inhalt für die Antwort wirklich nötig ist – das Lesekontingent pro Frage ist begrenzt.",
            new JsonObject { ["id"] = new JsonObject { ["type"] = "integer", ["description"] = "ID der E-Mail aus search_emails" } }, new[] { "id" }),
        new AiToolSpec("list_folders", "Listet die Archivordner (Postfächer und deren Ordner) mit ID und Anzahl E-Mails.", new JsonObject(), Array.Empty<string>()),
        new AiToolSpec("contact_stats",
            "Statistik der Korrespondenzpartner: wer schreibt wie oft (von/an), erster und letzter Kontakt. Optional eingeschränkt auf einen Namen/Domain-Teil und Zeitraum.",
            new JsonObject
            {
                ["filter"] = Str("Adresse, Name oder Domain enthält (z. B. 'telekom' oder 'mueller')"),
                ["date_from"] = Str("JJJJ-MM-TT"),
                ["date_to"] = Str("JJJJ-MM-TT"),
                ["include_automated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Auch Newsletter/no-reply-Absender zählen (Standard false)" },
                ["limit"] = Int("Anzahl Kontakte (Standard 25)", 1, 100),
            }, Array.Empty<string>()),
        new AiToolSpec("email_volume",
            "Anzahl E-Mails pro Monat oder Jahr, optional gefiltert (gleiche Filter wie search_emails, ohne limit). Gut für Zeitverläufe und 'wann begann/endete der Kontakt'.",
            new JsonObject
            {
                ["group_by"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("month", "year") },
                ["query"] = Str("Volltext, optional"),
                ["from"] = Str("Absender enthält"),
                ["to"] = Str("Empfänger enthält"),
                ["date_from"] = Str("JJJJ-MM-TT"),
                ["date_to"] = Str("JJJJ-MM-TT"),
            }, Array.Empty<string>()),
        new AiToolSpec("find_related",
            "Findet E-Mails, die mit einer E-Mail zusammenhängen: denselben Gesprächsverlauf (Antworten/Weiterleitungen über Message-ID-Verweise), denselben Betreff und E-Mails derselben Beteiligten im zeitlichen Umfeld.",
            new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "integer", ["description"] = "ID der Ausgangs-E-Mail" },
                ["limit"] = Int("Höchstzahl je Kategorie (Standard 15)", 1, 40),
            }, new[] { "id" }),
    };

    /// <summary>Führt ein Werkzeug aus und liefert das Ergebnis als JSON-Text. Ungültige Eingaben werfen <see cref="ArgumentException"/>.</summary>
    public string Execute(string name, JsonElement input) => name switch
    {
        "search_emails" => Search(input),
        "read_email" => Read(input),
        "list_folders" => ListFolders(),
        "contact_stats" => ContactStats(input),
        "email_volume" => Volume(input),
        "find_related" => Related(input),
        _ => throw new ArgumentException($"Unbekanntes Werkzeug: {name}"),
    };

    // ------------------------------------------------------------------ Eingaben

    static string? S(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    static int? I(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    static long? L(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    static bool? B(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static DateTime? D(JsonElement e, string key)
    {
        var s = S(e, key);
        if (s is null) return null;
        if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" }, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)) return d;
        throw new ArgumentException($"Ungültiges Datum '{s}' – bitte JJJJ-MM-TT verwenden.");
    }

    SearchCriteria Criteria(JsonElement input)
    {
        var from = D(input, "date_from");
        var to = D(input, "date_to");
        var folder = L(input, "folder_id");
        if (folder is { } f && !Allowed(f)) throw new ArgumentException($"Ordner {f} ist nicht freigegeben.");
        var c = new SearchCriteria
        {
            Query = S(input, "query"),
            From = S(input, "from"),
            To = S(input, "to"),
            HasAttachments = B(input, "has_attachments"),
            FolderId = folder,
            AllowedFolderIds = _allowedRoots,
            InAttachmentContents = true,
        };
        if (from is not null || to is not null)
        {
            c.DateRange = DateRangeType.Custom;
            c.DateFrom = from ?? new DateTime(1970, 1, 1);
            c.DateTo = to ?? DateTime.Today.AddYears(10);
        }
        return c;
    }

    bool Allowed(long folderId) => _allowedFolders is null || _allowedFolders.Contains(folderId);

    MessageSummary RequireMessage(long id)
    {
        var m = _archive.GetSummary(id);
        if (m is null || !Allowed(m.FolderId)) throw new ArgumentException($"E-Mail {id} gibt es nicht (oder sie ist nicht freigegeben).");
        return m;
    }

    // ------------------------------------------------------------------ Werkzeuge

    string Search(JsonElement input)
    {
        var c = Criteria(input);
        if (c.IsEmpty)
            throw new ArgumentException("Bitte einen Suchbegriff oder Filter angeben.");
        var limit = Math.Clamp(I(input, "limit") ?? 20, 1, 50);
        List<MessageSummary> hits;
        try { hits = _archive.Search(c, limit + 1); }
        catch (ArgumentException ex) { throw new ArgumentException(ex.Message + " Tipp: Sonderzeichen weglassen oder Begriffe in Anführungszeichen setzen."); }
        var more = hits.Count > limit;
        var items = new JsonArray();
        foreach (var m in hits.Take(limit))
        {
            SeenIds.Add(m.Id);
            var o = Summary(m);
            o["snippet"] = Snippet(m.Id);
            items.Add(o);
        }
        return new JsonObject { ["count"] = items.Count, ["more_available"] = more, ["results"] = items }.ToJsonString(JsonOut);
    }

    JsonObject Summary(MessageSummary m) => new()
    {
        ["id"] = m.Id,
        ["date"] = m.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ["from"] = m.From,
        ["to"] = Shorten(m.To, 200),
        ["subject"] = m.Subject,
        ["folder"] = m.FolderPath,
        ["has_attachments"] = m.HasAttachments,
        ["size_kb"] = Math.Max(1, m.Size / 1024),
    };

    string Snippet(long id)
    {
        try
        {
            var (text, _) = SplitQuoted(BodyText(_archive.LoadMessage(id)));
            return Shorten(Regex.Replace(text, @"\s+", " ").Trim(), SnippetChars);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    static string BodyText(MimeMessage msg)
    {
        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body) && msg.HtmlBody is { } html) body = MessageIndexer.HtmlToText(html);
        return body ?? "";
    }

    const int QuotedChars = 2_000;

    static readonly Regex QuoteHeader = new(
        @"^\s*(On .{4,120} wrote:|Am .{4,120} schrieb .{0,120}:|-{2,}\s*(Ursprüngliche Nachricht|Original Message|Weitergeleitete Nachricht|Forwarded message)\s*-{2,}|Von:\s.+|From:\s.+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Trennt den neuen Text einer Antwort vom zitierten Verlauf (Zeilen mit "&gt;", "Am … schrieb …:", "-----Ursprüngliche Nachricht-----",
    /// Outlook-Kopf "Von: … Gesendet: …"). Spart Tokens und stellt das Wesentliche nach vorn; das Zitat bleibt (gekürzt) verfügbar.
    /// </summary>
    public static (string Text, string Quoted) SplitQuoted(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var own = new List<string>();
        var quoted = new List<string>();
        var inQuoteBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (inQuoteBlock) { quoted.Add(line); continue; }
            var t = line.TrimStart();
            if (t.StartsWith('>')) { quoted.Add(line); continue; }
            if (QuoteHeader.IsMatch(line))
            {
                // "Von:" / "From:" zählt nur als Zitatkopf, wenn kurz danach Gesendet/Sent/Datum/Date folgt (Outlook-Stil)
                var isMailHeader = !Regex.IsMatch(t, @"^(Von|From):", RegexOptions.IgnoreCase) ||
                                   lines.Skip(i + 1).Take(3).Any(l => Regex.IsMatch(l.TrimStart(), @"^(Gesendet|Sent|Datum|Date):", RegexOptions.IgnoreCase));
                if (isMailHeader)
                {
                    // alles ab hier ist der zitierte Verlauf (Outlook zitiert ohne ">")
                    inQuoteBlock = !t.StartsWith("On ", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("Am ", StringComparison.OrdinalIgnoreCase);
                    quoted.Add(line);
                    continue;
                }
            }
            own.Add(line);
        }
        var text = string.Join("\n", own).Trim();
        // Nur Zitat ohne eigenen Text (z. B. reine Weiterleitung): Original als Text behalten
        return text.Length == 0 ? (body.Trim(), "") : (text, string.Join("\n", quoted).Trim());
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    string Read(JsonElement input)
    {
        var id = L(input, "id") ?? throw new ArgumentException("id fehlt.");
        var m = RequireMessage(id);
        if (!_read.Contains(id) && _read.Count >= _maxReads)
            throw new ArgumentException($"Lesekontingent für diese Frage erschöpft ({_maxReads} E-Mails). Bitte mit den bisherigen Informationen antworten und erwähnen, dass weitere E-Mails nicht gelesen wurden.");
        _read.Add(id);
        SeenIds.Add(id);
        var msg = _archive.LoadMessage(id);
        var (text, quoted) = SplitQuoted(BodyText(msg));
        var truncated = text.Length > MaxBodyChars;
        var o = Summary(m);
        o["cc"] = MessageIndexer.FormatAddresses(msg.Cc);
        o["reply_to"] = MessageIndexer.FormatAddresses(msg.ReplyTo);
        o["attachments"] = new JsonArray(msg.Attachments.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "(ohne Namen)",
            ["type"] = a.ContentType.MimeType,
        }).ToArray());
        o["text"] = truncated ? text[..MaxBodyChars] : text;
        if (truncated) o["note"] = $"Text gekürzt: {MaxBodyChars:N0} von {text.Length:N0} Zeichen.";
        if (quoted.Length > 0)
            o["quoted_previous_message"] = quoted.Length > QuotedChars ? quoted[..QuotedChars] + $" … [Zitat gekürzt, {quoted.Length:N0} Zeichen]" : quoted;
        o["reads_left"] = _maxReads - _read.Count;
        return o.ToJsonString(JsonOut);
    }

    string ListFolders()
    {
        var arr = new JsonArray();
        void Add(ArchiveFolder f, string prefix)
        {
            var path = prefix.Length == 0 ? f.Name : prefix + "/" + f.Name;
            if (Allowed(f.Id)) arr.Add(new JsonObject { ["id"] = f.Id, ["path"] = path, ["emails_including_subfolders"] = f.TotalCount });
            foreach (var c in f.Children) Add(c, path);
        }
        foreach (var r in _archive.GetFolderTree()) Add(r, "");
        return new JsonObject { ["folders"] = arr }.ToJsonString(JsonOut);
    }

    List<HeaderRow> Rows() => _archive.GetHeaderRows(_allowedFolders);

    string ContactStats(JsonElement input)
    {
        var filter = S(input, "filter");
        var includeAutomated = B(input, "include_automated") ?? false;
        var net = ContactNetwork.Build(Rows(), maxNodes: 2000, minMessages: 1, hideAutomated: !includeAutomated,
            fromUtc: D(input, "date_from"), toUtc: D(input, "date_to")?.AddDays(1));
        var limit = Math.Clamp(I(input, "limit") ?? 25, 1, 100);
        var list = net.Nodes.Where(n => filter is null || n.Address.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                        n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(n => (JsonNode)new JsonObject
            {
                ["address"] = n.Address, ["name"] = n.Name, ["emails_from_them"] = n.FromThem, ["emails_to_them"] = n.ToThem,
                ["first"] = n.FirstUtc.ToString("yyyy-MM-dd"), ["last"] = n.LastUtc.ToString("yyyy-MM-dd"), ["is_archive_owner"] = n.IsOwner,
            }).ToArray();
        return new JsonObject { ["emails_analyzed"] = net.MessageCount, ["owner"] = string.Join(", ", net.OwnerAddresses), ["contacts"] = new JsonArray(list) }.ToJsonString(JsonOut);
    }

    string Volume(JsonElement input)
    {
        var byYear = S(input, "group_by") == "year";
        IEnumerable<DateTime> dates;
        if (S(input, "query") is null && S(input, "from") is null && S(input, "to") is null)
        {
            var from = D(input, "date_from");
            var to = D(input, "date_to")?.AddDays(1);
            dates = Rows().Select(r => r.DateUtc).Where(d => (from is null || d >= from) && (to is null || d < to));
        }
        else dates = _archive.Search(Criteria(input), 100_000).Select(m => m.DateUtc);
        var groups = dates.GroupBy(d => byYear ? d.ToLocalTime().ToString("yyyy") : d.ToLocalTime().ToString("yyyy-MM"))
            .OrderBy(g => g.Key).Select(g => (JsonNode)new JsonObject { ["period"] = g.Key, ["count"] = g.Count() }).ToArray();
        return new JsonObject { ["total"] = groups.Sum(g => (int)g!["count"]!), ["periods"] = new JsonArray(groups) }.ToJsonString(JsonOut);
    }

    static readonly Regex SubjectPrefix = new(@"^\s*((re|aw|wg|fw|fwd|antw|sv|vs)\s*(\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static string NormalizeSubject(string s) => SubjectPrefix.Replace(s ?? "", "").Trim();

    string Related(JsonElement input)
    {
        var id = L(input, "id") ?? throw new ArgumentException("id fehlt.");
        var limit = Math.Clamp(I(input, "limit") ?? 15, 1, 40);
        var m = RequireMessage(id);
        var msg = _archive.LoadMessage(id);

        // 1. Gesprächsverlauf über Message-ID-Verweise (beide Richtungen, soweit im Archiv gespeichert)
        var refs = msg.References.Concat(msg.InReplyTo is null ? Array.Empty<string>() : new[] { msg.InReplyTo }).ToList();
        var thread = _archive.FindByMessageIds(refs).ToHashSet();
        var rows = Rows();
        var subject = NormalizeSubject(m.Subject);
        var sameSubject = subject.Length >= 4
            ? rows.Where(r => r.Id != id && string.Equals(NormalizeSubject(r.Subject), subject, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToList()
            : new List<long>();
        // Antworten auf diese E-Mail (In-Reply-To ist nicht gespeichert -> Betreffgleichheit nach dem Datum)
        thread.UnionWith(sameSubject.Where(x => _archive.GetSummary(x) is { } s && Math.Abs((s.DateUtc - m.DateUtc).TotalDays) < 120));
        thread.Remove(id);

        // 2. Dieselben Beteiligten im Umfeld von ±60 Tagen
        var people = AddressesOf(m.From + "," + m.To + "," + MessageIndexer.FormatAddresses(msg.Cc))
            .Where(a => !ContactNetwork.IsAutomated(a)).ToHashSet();
        var owner = ContactNetwork.Build(rows, maxNodes: 1).OwnerAddresses;
        people.ExceptWith(owner);
        var nearby = rows.Where(r => r.Id != id && !thread.Contains(r.Id) && Math.Abs((r.DateUtc - m.DateUtc).TotalDays) <= 60 &&
                                     AddressesOf(r.From + "," + r.To + "," + r.Cc).Overlaps(people))
            .OrderBy(r => Math.Abs((r.DateUtc - m.DateUtc).TotalDays)).Take(limit).Select(r => r.Id);

        JsonArray List(IEnumerable<long> ids) => new(ids.Select(x => _archive.GetSummary(x)).Where(s => s is not null && Allowed(s.FolderId))
            .OrderBy(s => s!.DateUtc).Take(limit).Select(s => { SeenIds.Add(s!.Id); return (JsonNode)Summary(s); }).ToArray());

        return new JsonObject
        {
            ["email"] = Summary(m),
            ["same_conversation"] = List(thread),
            ["same_people_nearby"] = List(nearby),
        }.ToJsonString(JsonOut);
    }

    static HashSet<string> AddressesOf(string text) =>
        InternetAddressList.TryParse(text, out var list)
            ? list.Mailboxes.Select(mb => mb.Address.ToLowerInvariant()).Where(a => a.Contains('@')).ToHashSet()
            : new HashSet<string>();

    static readonly JsonSerializerOptions JsonOut = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
