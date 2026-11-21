using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MailVault.Tests.Fakes;

/// <summary>
/// Minimaler, aber protokolltreuer IMAP4rev1-Server (RFC 3501) im Prozess, nur Klartext. Hält Ordner und Nachrichten
/// im Speicher, führt auch verändernde Befehle wirklich aus (STORE, EXPUNGE, COPY, MOVE, APPEND, CREATE, DELETE, RENAME)
/// und setzt bei Nicht-PEEK-Abrufen im Lese-/Schreibmodus \Seen – ein Rückschritt im Client wäre damit sichtbar.
/// </summary>
public sealed class FakeImapServer : FakeServerBase
{
    public const char Delimiter = '/';

    public sealed class Message
    {
        public uint Uid { get; init; }
        public required byte[] Raw { get; init; }
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset InternalDate { get; init; }
    }

    public sealed class Folder
    {
        public required string Name { get; set; }
        public string[] Attributes { get; init; } = Array.Empty<string>();
        public uint UidValidity { get; init; }
        public uint UidNext { get; set; } = 1;
        public bool Subscribed { get; set; } = true;
        public List<Message> Messages { get; } = new();
    }

    readonly object _sync = new();
    readonly List<Folder> _folders = new();
    uint _nextUidValidity = 1000;

    public string UserName { get; set; } = "anna@example.org";
    public string Password { get; set; } = "Geheim#123";
    /// <summary>AUTH=PLAIN anbieten (sonst bleibt dem Client nur LOGIN).</summary>
    public bool AdvertiseAuthPlain { get; set; } = true;
    /// <summary>SASL-IR anbieten (AUTHENTICATE PLAIN mit Initial Response in derselben Zeile).</summary>
    public bool AdvertiseSaslIR { get; set; }

    public FakeImapServer()
    {
        AddFolder("INBOX");
        Start();
    }

    string Capabilities(bool authenticated)
    {
        var caps = new List<string> { "IMAP4rev1", "LITERAL+", "NAMESPACE", "UNSELECT", "ID" };
        if (!authenticated)
        {
            if (AdvertiseSaslIR) caps.Add("SASL-IR");
            if (AdvertiseAuthPlain) caps.Add("AUTH=PLAIN");
        }
        return string.Join(' ', caps);
    }

    // ------------------------------------------------------------------ Testdaten

    public Folder AddFolder(string name, params string[] attributes)
    {
        lock (_sync)
        {
            if (FindFolder(name) is { } existing) return existing;
            var f = new Folder { Name = name, Attributes = attributes, UidValidity = _nextUidValidity++ };
            _folders.Add(f);
            return f;
        }
    }

    public uint AddMessage(string folder, byte[] raw, IEnumerable<string>? flags = null, DateTimeOffset? internalDate = null)
    {
        lock (_sync)
        {
            var f = FindFolder(folder) ?? throw new ArgumentException("Ordner fehlt: " + folder);
            var m = new Message { Uid = f.UidNext++, Raw = raw, InternalDate = internalDate ?? new DateTimeOffset(2025, 3, 1, 8, 0, 0, TimeSpan.FromHours(1)) };
            if (flags is not null) foreach (var fl in flags) m.Flags.Add(fl);
            f.Messages.Add(m);
            return m.Uid;
        }
    }

    /// <summary>Kopie der Nachrichten eines Ordners (Rohdaten und Flags).</summary>
    public IReadOnlyList<(uint Uid, byte[] Raw, string[] Flags)> GetMessages(string folder)
    {
        lock (_sync)
        {
            var f = FindFolder(folder) ?? throw new ArgumentException("Ordner fehlt: " + folder);
            return f.Messages.Select(m => (m.Uid, m.Raw.ToArray(), m.Flags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray())).ToList();
        }
    }

    public IReadOnlyList<string> FolderNames { get { lock (_sync) return _folders.Select(f => f.Name).ToList(); } }

    /// <summary>Vollständiger Zustand des Postfachs: Ordner, UIDs, Flags und SHA-256 jeder Nachricht.</summary>
    public string Snapshot()
    {
        lock (_sync)
        {
            var sb = new StringBuilder();
            foreach (var f in _folders.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                sb.Append("[").Append(f.Name).Append("] uidvalidity=").Append(f.UidValidity).Append(" uidnext=").Append(f.UidNext).Append('\n');
                foreach (var m in f.Messages)
                    sb.Append("  ").Append(m.Uid).Append(" (").Append(string.Join(' ', m.Flags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)))
                      .Append(") ").Append(Convert.ToHexString(SHA256.HashData(m.Raw))).Append('\n');
            }
            return sb.ToString();
        }
    }

    Folder? FindFolder(string name) =>
        _folders.FirstOrDefault(f => name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? f.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            : f.Name == name);

    // ------------------------------------------------------------------ Protokoll

    sealed class Session
    {
        public bool Authenticated;
        public Folder? Selected;
        public bool ReadOnly = true;
    }

    sealed record Atom(string V);
    sealed record Str(string V);
    sealed record Lit(byte[] V);

    static string S(object o) => o switch
    {
        Atom a => a.V,
        Str s => s.V,
        Lit l => Encoding.UTF8.GetString(l.V),
        _ => throw new FormatException("String erwartet"),
    };

    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static readonly Regex LiteralAtEnd = new(@"\{(\d+)(\+?)\}$", RegexOptions.Compiled);

    /// <summary>Liest einen vollständigen Befehl inklusive Literale. Literale werden im Text durch \u0001 ersetzt.</summary>
    static async Task<(string Text, List<byte[]> Literals, string Log)?> ReadCommandAsync(LineStream io, CancellationToken ct)
    {
        var text = new StringBuilder();
        var log = new StringBuilder();
        var lits = new List<byte[]>();
        while (true)
        {
            var line = await io.ReadTextLineAsync(ct);
            if (line is null) return null;
            var m = LiteralAtEnd.Match(line);
            if (!m.Success)
            {
                text.Append(line);
                log.Append(line);
                return (text.ToString(), lits, log.ToString());
            }
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            text.Append(line, 0, m.Index).Append('\u0001');
            log.Append(line, 0, m.Index).Append('{').Append(n).Append(" bytes}");
            if (m.Groups[2].Value != "+") await io.WriteLineAsync("+ Ready for literal data", ct);
            lits.Add(await io.ReadExactAsync(n, ct));
        }
    }

    static List<object> Tokenize(string text, List<byte[]> lits)
    {
        var i = 0;
        var k = 0;
        return ParseList(text, lits, ref i, ref k, null);
    }

    static List<object> ParseList(string t, List<byte[]> lits, ref int i, ref int k, char? end)
    {
        var list = new List<object>();
        while (i < t.Length)
        {
            var c = t[i];
            if (c == ' ') { i++; continue; }
            if (end is { } e && c == e) { i++; return list; }
            if (c == '(') { i++; list.Add(ParseList(t, lits, ref i, ref k, ')')); continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < t.Length && t[i] != '"')
                {
                    if (t[i] == '\\' && i + 1 < t.Length) i++;
                    sb.Append(t[i++]);
                }
                i++;
                list.Add(new Str(sb.ToString()));
                continue;
            }
            if (c == '\u0001') { i++; list.Add(new Lit(lits[k++])); continue; }
            var start = i;
            while (i < t.Length && t[i] != ' ' && t[i] != ')' && t[i] != '(')
            {
                if (t[i] == '[')
                {
                    var depth = 0;
                    while (i < t.Length)
                    {
                        if (t[i] == '[') depth++;
                        else if (t[i] == ']' && --depth == 0) break;
                        i++;
                    }
                }
                i++;
            }
            list.Add(new Atom(t[start..Math.Min(i, t.Length)]));
        }
        return list;
    }

    static string MaskSecrets(string log, string verb)
    {
        var parts = log.Split(' ');
        if (verb == "LOGIN" && parts.Length >= 4) return string.Join(' ', parts.Take(3)) + " ****";
        if (verb == "AUTHENTICATE" && parts.Length >= 4) return string.Join(' ', parts.Take(3)) + " ****";
        return log;
    }

    internal override async Task HandleClientAsync(LineStream io, CancellationToken ct)
    {
        var s = new Session();
        await io.WriteLineAsync($"* OK [CAPABILITY {Capabilities(false)}] FakeIMAP ready", ct);
        while (!ct.IsCancellationRequested)
        {
            var cmd = await ReadCommandAsync(io, ct);
            if (cmd is null) return;
            var (text, lits, log) = cmd.Value;
            List<object> tok;
            try { tok = Tokenize(text, lits); }
            catch { Record(log); await io.WriteLineAsync("* BAD Parse error", ct); continue; }
            if (tok.Count < 2 || tok[0] is not Atom tagAtom)
            {
                Record(log);
                await io.WriteLineAsync("* BAD Missing tag or command", ct);
                continue;
            }
            var tag = tagAtom.V;
            var verb = S(tok[1]).ToUpperInvariant();
            var uid = false;
            var args = tok.Skip(2).ToList();
            if (verb == "UID" && args.Count > 0)
            {
                uid = true;
                verb = S(args[0]).ToUpperInvariant();
                args = args.Skip(1).ToList();
            }
            var fullVerb = uid ? "UID " + verb : verb;
            Record(MaskSecrets(log, verb), IsMutatingVerb(verb) || (verb is "FETCH" && IsNonPeekFetch(args)));
            if (verb == "CLOSE" && s.Selected is not null && !s.ReadOnly) MarkMutating(log + "   (CLOSE nach SELECT löscht \\Deleted-Nachrichten)");

            try
            {
                var keepGoing = await DispatchAsync(io, s, tag, verb, fullVerb, uid, args, ct);
                if (!keepGoing) return;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidCastException)
            {
                await io.WriteLineAsync($"{tag} BAD {fullVerb} arguments invalid: {ex.Message}", ct);
            }
        }
    }

    static bool IsMutatingVerb(string verb) =>
        verb is "STORE" or "EXPUNGE" or "COPY" or "MOVE" or "APPEND" or "DELETE" or "RENAME" or "CREATE" or "SELECT"
            or "SUBSCRIBE" or "UNSUBSCRIBE";

    static List<string> FetchItems(List<object> args)
    {
        if (args.Count < 2) return new();
        var items = args[1] is List<object> l ? l.Select(S).ToList() : new List<string> { S(args[1]) };
        var result = new List<string>();
        foreach (var it in items)
        {
            switch (it.ToUpperInvariant())
            {
                case "ALL": result.AddRange(new[] { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE" }); break;
                case "FAST": result.AddRange(new[] { "FLAGS", "INTERNALDATE", "RFC822.SIZE" }); break;
                case "FULL": result.AddRange(new[] { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY" }); break;
                default: result.Add(it); break;
            }
        }
        return result;
    }

    static bool IsNonPeek(string item)
    {
        var u = item.ToUpperInvariant();
        return u is "RFC822" or "RFC822.TEXT" || u.StartsWith("BODY[", StringComparison.Ordinal);
    }

    static bool IsNonPeekFetch(List<object> args)
    {
        try { return FetchItems(args).Any(IsNonPeek); }
        catch { return false; }
    }

    async Task<bool> DispatchAsync(LineStream io, Session s, string tag, string verb, string fullVerb, bool uid, List<object> args, CancellationToken ct)
    {
        async Task Ok(string text = "completed") => await io.WriteLineAsync($"{tag} OK {fullVerb} {text}", ct);
        async Task No(string text) => await io.WriteLineAsync($"{tag} NO {text}", ct);
        async Task Line(string text) => await io.WriteLineAsync(text, ct);

        // Befehle in jedem Zustand
        switch (verb)
        {
            case "CAPABILITY":
                await Line("* CAPABILITY " + Capabilities(s.Authenticated));
                await Ok();
                return true;
            case "NOOP":
                await Ok();
                return true;
            case "LOGOUT":
                await Line("* BYE FakeIMAP logging out");
                await Ok();
                return false;
            case "ID":
                await Line("* ID (\"name\" \"FakeIMAP\")");
                await Ok();
                return true;
        }

        if (!s.Authenticated)
        {
            switch (verb)
            {
                case "LOGIN":
                    if (S(args[0]) == UserName && S(args[1]) == Password)
                    {
                        s.Authenticated = true;
                        await io.WriteLineAsync($"{tag} OK [CAPABILITY {Capabilities(true)}] LOGIN completed", ct);
                    }
                    else await No("[AUTHENTICATIONFAILED] Invalid credentials");
                    return true;
                case "AUTHENTICATE":
                    if (!AdvertiseAuthPlain || !S(args[0]).Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        await No("Unsupported authentication mechanism");
                        return true;
                    }
                    string response;
                    if (args.Count > 1) response = S(args[1]);
                    else
                    {
                        await Line("+ ");
                        response = await io.ReadTextLineAsync(ct) ?? "*";
                    }
                    if (response == "*") { await io.WriteLineAsync($"{tag} BAD AUTHENTICATE cancelled", ct); return true; }
                    string[] parts;
                    try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(response)).Split('\0'); }
                    catch (FormatException) { await io.WriteLineAsync($"{tag} BAD Invalid base64", ct); return true; }
                    if (parts.Length == 3 && parts[1] == UserName && parts[2] == Password)
                    {
                        s.Authenticated = true;
                        await io.WriteLineAsync($"{tag} OK [CAPABILITY {Capabilities(true)}] AUTHENTICATE completed", ct);
                    }
                    else await No("[AUTHENTICATIONFAILED] Invalid credentials");
                    return true;
                default:
                    await io.WriteLineAsync($"{tag} BAD {fullVerb} not allowed before authentication", ct);
                    return true;
            }
        }

        switch (verb)
        {
            case "LOGIN":
            case "AUTHENTICATE":
                await io.WriteLineAsync($"{tag} BAD Already authenticated", ct);
                return true;

            case "ENABLE":
                await Line("* ENABLED");
                await Ok();
                return true;

            case "NAMESPACE":
                await Line($"* NAMESPACE ((\"\" \"{Delimiter}\")) NIL NIL");
                await Ok();
                return true;

            case "LIST":
            case "LSUB":
            {
                var a = args.SkipWhile(x => x is List<object>).ToList();
                var reference = S(a[0]);
                var pattern = a[1] is List<object> pl ? S(pl[0]) : S(a[1]);
                var lines = new List<string>();
                lock (_sync)
                {
                    if (pattern.Length == 0)
                        lines.Add($"* {verb} (\\Noselect) \"{Delimiter}\" \"\"");
                    else
                    {
                        var full = reference + pattern;
                        var rx = new Regex("^" + Regex.Escape(full).Replace("\\*", ".*").Replace("%", "[^/]*") + "$");
                        foreach (var f in _folders)
                        {
                            var isInbox = f.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase);
                            var match = rx.IsMatch(f.Name) || (isInbox && Regex.IsMatch(f.Name, rx.ToString(), RegexOptions.IgnoreCase));
                            if (!match || (verb == "LSUB" && !f.Subscribed)) continue;
                            var attrs = f.Attributes.ToList();
                            attrs.Add(_folders.Any(o => o.Name.StartsWith(f.Name + Delimiter, StringComparison.Ordinal)) ? "\\HasChildren" : "\\HasNoChildren");
                            lines.Add($"* {verb} ({string.Join(' ', attrs)}) \"{Delimiter}\" {Q(f.Name)}");
                        }
                    }
                }
                foreach (var l in lines) await Line(l);
                await Ok();
                return true;
            }

            case "STATUS":
            {
                var name = S(args[0]);
                var items = ((List<object>)args[1]).Select(x => S(x).ToUpperInvariant()).ToList();
                string? resp = null;
                lock (_sync)
                {
                    if (FindFolder(name) is { } f)
                    {
                        var vals = items.Select(it => it switch
                        {
                            "MESSAGES" => $"MESSAGES {f.Messages.Count}",
                            "RECENT" => "RECENT 0",
                            "UIDNEXT" => $"UIDNEXT {f.UidNext}",
                            "UIDVALIDITY" => $"UIDVALIDITY {f.UidValidity}",
                            "UNSEEN" => $"UNSEEN {f.Messages.Count(m => !m.Flags.Contains("\\Seen"))}",
                            _ => null,
                        }).Where(v => v is not null);
                        resp = $"* STATUS {Q(f.Name)} ({string.Join(' ', vals)})";
                    }
                }
                if (resp is null) { await No("[NONEXISTENT] No such mailbox"); return true; }
                await Line(resp);
                await Ok();
                return true;
            }

            case "SELECT":
            case "EXAMINE":
            {
                var name = S(args[0]);
                var lines = new List<string>();
                Folder? f;
                lock (_sync)
                {
                    f = FindFolder(name);
                    if (f is not null)
                    {
                        lines.Add("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                        lines.Add(verb == "SELECT"
                            ? "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Flags permitted"
                            : "* OK [PERMANENTFLAGS ()] Read-only mailbox");
                        lines.Add($"* {f.Messages.Count} EXISTS");
                        lines.Add("* 0 RECENT");
                        lines.Add($"* OK [UIDVALIDITY {f.UidValidity}] UIDs valid");
                        lines.Add($"* OK [UIDNEXT {f.UidNext}] Predicted next UID");
                    }
                }
                s.Selected = null;
                if (f is null) { await No("[NONEXISTENT] No such mailbox"); return true; }
                foreach (var l in lines) await Line(l);
                s.Selected = f;
                s.ReadOnly = verb == "EXAMINE";
                await io.WriteLineAsync($"{tag} OK [{(s.ReadOnly ? "READ-ONLY" : "READ-WRITE")}] {verb} completed", ct);
                return true;
            }

            case "CREATE":
            {
                var name = S(args[0]).TrimEnd(Delimiter);
                bool created;
                lock (_sync)
                {
                    created = FindFolder(name) is null;
                    if (created) _folders.Add(new Folder { Name = name, UidValidity = _nextUidValidity++ });
                }
                if (created) await Ok(); else await No("[ALREADYEXISTS] Mailbox exists");
                return true;
            }

            case "DELETE":
            {
                var name = S(args[0]);
                bool removed;
                lock (_sync) removed = FindFolder(name) is { } f && !f.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) && _folders.Remove(f);
                if (removed) await Ok(); else await No("[NONEXISTENT] Cannot delete mailbox");
                return true;
            }

            case "RENAME":
            {
                bool ok;
                lock (_sync)
                {
                    var f = FindFolder(S(args[0]));
                    ok = f is not null && FindFolder(S(args[1])) is null;
                    if (ok) f!.Name = S(args[1]);
                }
                if (ok) await Ok(); else await No("Cannot rename mailbox");
                return true;
            }

            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                lock (_sync) if (FindFolder(S(args[0])) is { } f) f.Subscribed = verb == "SUBSCRIBE";
                await Ok();
                return true;

            case "APPEND":
            {
                var name = S(args[0]);
                var flags = args.OfType<List<object>>().FirstOrDefault()?.Select(S).ToList() ?? new List<string>();
                var raw = args.OfType<Lit>().LastOrDefault()?.V ?? throw new FormatException("Literal fehlt");
                bool ok;
                lock (_sync)
                {
                    ok = FindFolder(name) is not null;
                    if (ok) AddMessage(name, raw, flags);
                }
                if (ok) await Ok(); else await No("[TRYCREATE] No such mailbox");
                return true;
            }
        }

        // Ab hier: Ordner muss geöffnet sein.
        if (s.Selected is null)
        {
            await io.WriteLineAsync($"{tag} BAD {fullVerb} requires a selected mailbox", ct);
            return true;
        }
        var folder = s.Selected;

        switch (verb)
        {
            case "CLOSE":
                if (!s.ReadOnly) lock (_sync) folder.Messages.RemoveAll(m => m.Flags.Contains("\\Deleted"));
                s.Selected = null;
                await Ok();
                return true;

            case "UNSELECT":
                s.Selected = null;
                await Ok();
                return true;

            case "CHECK":
                await Ok();
                return true;

            case "SEARCH":
            {
                List<uint> hits;
                lock (_sync)
                {
                    IEnumerable<(Message M, int Seq)> all = folder.Messages.Select((m, i) => (m, i + 1));
                    var crit = args.Select(a => a is List<object> ? "" : S(a).ToUpperInvariant()).ToList();
                    var uidIdx = crit.IndexOf("UID");
                    if (uidIdx >= 0 && uidIdx + 1 < args.Count)
                    {
                        var set = ParseSet(S(args[uidIdx + 1]), folder.Messages.Count == 0 ? 0 : folder.Messages.Max(m => m.Uid));
                        all = all.Where(x => set(x.M.Uid));
                    }
                    if (crit.Contains("UNSEEN")) all = all.Where(x => !x.M.Flags.Contains("\\Seen"));
                    if (crit.Contains("SEEN") ) all = all.Where(x => x.M.Flags.Contains("\\Seen"));
                    hits = all.Select(x => uid ? x.M.Uid : (uint)x.Seq).ToList();
                }
                await Line("* SEARCH" + string.Concat(hits.Select(h => " " + h)));
                await Ok();
                return true;
            }

            case "FETCH":
            {
                var items = FetchItems(args);
                if (uid && !items.Any(i => i.Equals("UID", StringComparison.OrdinalIgnoreCase))) items.Insert(0, "UID");
                var unknown = items.FirstOrDefault(i => !IsSupportedFetchItem(i));
                if (unknown is not null) { await io.WriteLineAsync($"{tag} BAD Unsupported fetch item {unknown}", ct); return true; }
                var responses = new List<byte[]>();
                lock (_sync)
                {
                    foreach (var (m, seq) in Resolve(folder, S(args[0]), uid))
                        responses.Add(BuildFetch(m, seq, items, s.ReadOnly));
                }
                foreach (var r in responses) await io.WriteAsync(r, ct);
                await Ok();
                return true;
            }

            case "STORE":
            {
                if (s.ReadOnly) { await No("[READ-ONLY] Mailbox is read-only"); return true; }
                var op = S(args[1]).ToUpperInvariant();
                var flags = args[2] is List<object> fl ? fl.Select(S).ToList() : args.Skip(2).Select(S).ToList();
                var lines = new List<string>();
                lock (_sync)
                {
                    foreach (var (m, seq) in Resolve(folder, S(args[0]), uid))
                    {
                        if (op.StartsWith("+")) foreach (var f in flags) m.Flags.Add(f);
                        else if (op.StartsWith("-")) foreach (var f in flags) m.Flags.Remove(f);
                        else { m.Flags.Clear(); foreach (var f in flags) m.Flags.Add(f); }
                        if (!op.EndsWith(".SILENT"))
                            lines.Add($"* {seq} FETCH ({(uid ? $"UID {m.Uid} " : "")}FLAGS ({string.Join(' ', m.Flags)}))");
                    }
                }
                foreach (var l in lines) await Line(l);
                await Ok();
                return true;
            }

            case "EXPUNGE":
            {
                if (s.ReadOnly) { await No("[READ-ONLY] Mailbox is read-only"); return true; }
                var lines = new List<string>();
                lock (_sync)
                {
                    Func<uint, bool> inSet = uid && args.Count > 0 ? ParseSet(S(args[0]), MaxUid(folder)) : _ => true;
                    for (var i = 0; i < folder.Messages.Count;)
                    {
                        var m = folder.Messages[i];
                        if (m.Flags.Contains("\\Deleted") && inSet(m.Uid))
                        {
                            folder.Messages.RemoveAt(i);
                            lines.Add($"* {i + 1} EXPUNGE");
                        }
                        else i++;
                    }
                }
                foreach (var l in lines) await Line(l);
                await Ok();
                return true;
            }

            case "COPY":
            case "MOVE":
            {
                if (verb == "MOVE" && s.ReadOnly) { await No("[READ-ONLY] Mailbox is read-only"); return true; }
                var lines = new List<string>();
                bool ok;
                lock (_sync)
                {
                    var target = FindFolder(S(args[1]));
                    ok = target is not null;
                    if (ok)
                    {
                        var msgs = Resolve(folder, S(args[0]), uid).Select(x => x.M).ToList();
                        foreach (var m in msgs)
                        {
                            var copy = new Message { Uid = target!.UidNext++, Raw = m.Raw.ToArray(), InternalDate = m.InternalDate };
                            foreach (var f in m.Flags) copy.Flags.Add(f);
                            target.Messages.Add(copy);
                        }
                        if (verb == "MOVE")
                            foreach (var m in msgs)
                            {
                                var idx = folder.Messages.IndexOf(m);
                                folder.Messages.RemoveAt(idx);
                                lines.Add($"* {idx + 1} EXPUNGE");
                            }
                    }
                }
                if (!ok) { await No("[TRYCREATE] No such mailbox"); return true; }
                foreach (var l in lines) await Line(l);
                await Ok();
                return true;
            }

            default:
                await io.WriteLineAsync($"{tag} BAD Unknown command {fullVerb}", ct);
                return true;
        }
    }

    static uint MaxUid(Folder f) => f.Messages.Count == 0 ? 0 : f.Messages.Max(m => m.Uid);

    static Func<uint, bool> ParseSet(string set, uint star)
    {
        var ranges = new List<(uint Lo, uint Hi)>();
        foreach (var part in set.Split(','))
        {
            var ab = part.Split(':');
            uint P(string x) => x == "*" ? star : uint.Parse(x, CultureInfo.InvariantCulture);
            var a = P(ab[0]);
            var b = ab.Length > 1 ? P(ab[1]) : a;
            ranges.Add((Math.Min(a, b), Math.Max(a, b)));
        }
        return n => ranges.Any(r => n >= r.Lo && n <= r.Hi);
    }

    static IEnumerable<(Message M, int Seq)> Resolve(Folder f, string set, bool uid)
    {
        var inSet = ParseSet(set, uid ? MaxUid(f) : (uint)f.Messages.Count);
        return f.Messages.Select((m, i) => (m, i + 1)).Where(x => inSet(uid ? x.m.Uid : (uint)x.Item2)).ToList();
    }

    static readonly Regex BodyItem = new(@"^(BODY|BODY\.PEEK)\[([^\]]*)\](?:<(\d+)(?:\.(\d+))?>)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool IsSupportedFetchItem(string item)
    {
        var u = item.ToUpperInvariant();
        return u is "UID" or "FLAGS" or "INTERNALDATE" or "RFC822.SIZE" or "RFC822" or "RFC822.HEADER" or "RFC822.TEXT"
               || (BodyItem.Match(item) is { Success: true } m && IsSupportedSection(m.Groups[2].Value));
    }

    static bool IsSupportedSection(string section)
    {
        var u = section.ToUpperInvariant();
        return u is "" or "HEADER" or "TEXT" || u.StartsWith("HEADER.FIELDS", StringComparison.Ordinal);
    }

    static int HeaderEnd(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == '\r' && raw[1] == '\n') return 2;
        for (var i = 0; i + 3 < raw.Length; i++)
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n') return i + 4;
        return raw.Length;
    }

    static byte[] Section(byte[] raw, string section)
    {
        var u = section.ToUpperInvariant();
        var he = HeaderEnd(raw);
        if (u == "") return raw;
        if (u == "HEADER") return raw[..he];
        if (u == "TEXT") return raw[he..];
        // HEADER.FIELDS (A B) / HEADER.FIELDS.NOT (A B)
        var not = u.StartsWith("HEADER.FIELDS.NOT", StringComparison.Ordinal);
        var open = u.IndexOf('(');
        var names = u[(open + 1)..u.LastIndexOf(')')].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var header = LineStream.Latin1.GetString(raw, 0, he);
        var sb = new StringBuilder();
        var keep = false;
        foreach (var line in header.Split("\r\n"))
        {
            if (line.Length == 0) continue;
            if (line[0] is ' ' or '\t') { if (keep) sb.Append(line).Append("\r\n"); continue; }
            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim().ToUpperInvariant() : "";
            keep = names.Contains(name) != not;
            if (keep) sb.Append(line).Append("\r\n");
        }
        sb.Append("\r\n");
        return LineStream.Latin1.GetBytes(sb.ToString());
    }

    static string FormatDate(DateTimeOffset d)
    {
        var off = d.Offset;
        var sign = off < TimeSpan.Zero ? '-' : '+';
        off = off.Duration();
        return d.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + sign + off.Hours.ToString("00") + off.Minutes.ToString("00");
    }

    static byte[] BuildFetch(Message m, int seq, List<string> items, bool readOnly)
    {
        var nonPeek = items.Any(IsNonPeek);
        // Wie ein echter Server: Nicht-PEEK-Abruf in einem schreibbaren Ordner setzt \Seen.
        if (nonPeek && !readOnly) m.Flags.Add("\\Seen");
        if (nonPeek && !readOnly && !items.Any(i => i.Equals("FLAGS", StringComparison.OrdinalIgnoreCase))) items = items.Append("FLAGS").ToList();

        var ms = new MemoryStream();
        void W(string t) { var b = LineStream.Latin1.GetBytes(t); ms.Write(b, 0, b.Length); }
        void Literal(byte[] data) { W("{" + data.Length + "}\r\n"); ms.Write(data, 0, data.Length); }

        W($"* {seq} FETCH (");
        var first = true;
        foreach (var item in items)
        {
            if (!first) W(" ");
            first = false;
            var u = item.ToUpperInvariant();
            switch (u)
            {
                case "UID": W($"UID {m.Uid}"); break;
                case "FLAGS": W($"FLAGS ({string.Join(' ', m.Flags)})"); break;
                case "INTERNALDATE": W($"INTERNALDATE \"{FormatDate(m.InternalDate)}\""); break;
                case "RFC822.SIZE": W($"RFC822.SIZE {m.Raw.Length}"); break;
                case "RFC822": W("RFC822 "); Literal(m.Raw); break;
                case "RFC822.HEADER": W("RFC822.HEADER "); Literal(Section(m.Raw, "HEADER")); break;
                case "RFC822.TEXT": W("RFC822.TEXT "); Literal(Section(m.Raw, "TEXT")); break;
                default:
                {
                    var bm = BodyItem.Match(item);
                    var section = bm.Groups[2].Value;
                    var data = Section(m.Raw, section);
                    var label = $"BODY[{section.ToUpperInvariant()}]";
                    if (bm.Groups[3].Success)
                    {
                        var off = Math.Min(int.Parse(bm.Groups[3].Value, CultureInfo.InvariantCulture), data.Length);
                        var len = bm.Groups[4].Success ? Math.Min(int.Parse(bm.Groups[4].Value, CultureInfo.InvariantCulture), data.Length - off) : data.Length - off;
                        data = data.AsSpan(off, len).ToArray();
                        label += $"<{off}>";
                    }
                    W(label + " ");
                    Literal(data);
                    break;
                }
            }
        }
        W(")\r\n");
        return ms.ToArray();
    }
}
