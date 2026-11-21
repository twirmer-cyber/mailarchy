using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Utils;
using XstReader;
using XstReader.ElementProperties;
using MessageImportance = MimeKit.MessageImportance;

using MailVault.Core.Localization;

namespace MailVault.Core.Sources;

/// <summary>
/// Liest Outlook-Datendateien (.pst/.ost) direkt, ohne Outlook oder MAPI (XstReader.Api, MS-PL).
/// Jede Nachricht wird in eine RFC-822-Nachricht umgewandelt. Liegen die originalen Internet-Kopfzeilen
/// vor (PidTagTransportMessageHeaders, bei empfangenen Internet-E-Mails), werden sie übernommen, damit
/// Message-ID, Datum und Duplikatschlüssel mit einer per IMAP archivierten Kopie übereinstimmen.
/// </summary>
public sealed class OutlookPstSource : IMailSource
{
    readonly string _file;
    XstFile? _xst;

    public OutlookPstSource(string file) => _file = file;

    public string DisplayName => CoreText.F("Source_OutlookFile", Path.GetFileName(_file));

    public Task ConnectAsync(CancellationToken ct)
    {
        if (!File.Exists(_file)) throw new FileNotFoundException(CoreText.F("Source_OutlookMissing", _file), _file);
        try
        {
            _xst = new XstFile(_file);
            _ = _xst.RootFolder.DisplayName; // erzwingt das Lesen des Dateikopfs
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            Release();
            throw new IOException(CoreText.T("Source_OutlookLocked"), ex);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            Release();
            throw new InvalidDataException(CoreText.F("Source_OutlookInvalid", Path.GetFileName(_file)), ex);
        }
        return Task.CompletedTask;
    }

    static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    XstFile File_ => _xst ?? throw new InvalidOperationException(CoreText.T("Source_NotConnected"));

    /// <summary>
    /// Sichtbare Postfachordner. In einer PST liegen sie unter "Oberste Ebene der Outlook-Datendatei",
    /// in einer OST unter "Root - Mailbox/IPM_SUBTREE". Öffentliche Ordner und Systembereiche werden ausgelassen.
    /// </summary>
    IEnumerable<XstFolder> TopFolders()
    {
        var root = File_.RootFolder;
        var queue = new Queue<(XstFolder Folder, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (folder, depth) = queue.Dequeue();
            foreach (var child in folder.Folders)
            {
                if (IsTopOfStore(child.DisplayName)) return child.Folders;
                if (depth < 2 && !IsPublicOrSystemRoot(child.DisplayName)) queue.Enqueue((child, depth + 1));
            }
        }
        return root.Folders.Where(f => !IsHiddenSystemFolder(f.DisplayName) && !IsPublicOrSystemRoot(f.DisplayName));
    }

    static bool IsPublicOrSystemRoot(string? name) => name is not null &&
        (name.StartsWith("Root - Public", StringComparison.OrdinalIgnoreCase) || name.Equals("NON_IPM_SUBTREE", StringComparison.OrdinalIgnoreCase));

    static bool IsTopOfStore(string? name) => name is not null &&
        (name.StartsWith("Top of", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("Oberste Ebene", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("IPM_SUBTREE", StringComparison.OrdinalIgnoreCase));

    static readonly HashSet<string> HiddenFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Search Root", "SPAM Search Folder 2", "Finder", "Views", "Common Views", "Shortcuts", "Schedule",
        "Freebusy Data", "Reminders", "To-Do Search", "Tracked Mail Processing", "Deferred Action", "Spooler Queue",
        "ItemProcSearch", "Recoverable Items", "Sync Issues", "Synchronisierungsprobleme", "Conversation Action Settings",
        "Quick Step Settings", "ExternalContacts", "PersonMetadata", "Yammer Root", "Files", "Social Activity Notifications",
    };

    static bool IsHiddenSystemFolder(string? name) => name is null || name.StartsWith("~") ||
        HiddenFolders.Contains(name.Replace(" (This computer only)", "").Replace(" (Nur dieser Computer)", ""));

    IEnumerable<(XstFolder Folder, IReadOnlyList<string> Path)> Walk()
    {
        var stack = new Stack<(XstFolder, IReadOnlyList<string>)>(
            TopFolders().Reverse().Select(f => (f, (IReadOnlyList<string>)new[] { Clean(f.DisplayName) })));
        while (stack.Count > 0)
        {
            var (folder, path) = stack.Pop();
            if (IsHiddenSystemFolder(folder.DisplayName)) continue;
            yield return (folder, path);
            foreach (var sub in folder.Folders.Reverse())
                stack.Push((sub, path.Append(Clean(sub.DisplayName)).ToArray()));
        }
    }

    static string Clean(string? name) => string.IsNullOrWhiteSpace(name) ? "(ohne Namen)" :
        name.Replace(" (This computer only)", "").Replace(" (Nur dieser Computer)", "").Replace('/', '-').Trim();

    public Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(Walk().Select(w => w.Path).ToList());

    public async IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter,
        IProgress<ProgressInfo>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (folder, path) in Walk())
        {
            ct.ThrowIfCancellationRequested();
            if (!folderFilter(path)) continue;
            progress?.Report(new ProgressInfo(CoreText.F("Source_FolderStatus", string.Join("/", path)), 0, 0, CoreText.F("Source_OutlookReading", string.Join("/", path), folder.ContentCount)));

            foreach (var m in folder.Messages)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsMail(m)) continue;
                HeaderList headers;
                try { headers = OutlookMessageConverter.BuildHeaders(m); }
                catch { continue; } // beschädigtes Element: wird übersprungen
                var msg = m;
                yield return new SourceMessage
                {
                    FolderPath = path,
                    Headers = headers,
                    Size = OutlookMessageConverter.SizeOf(m),
                    Date = OutlookMessageConverter.DateOf(m),
                    LoadRawAsync = _ => Task.FromResult(OutlookMessageConverter.ToMime(msg, headers)),
                };
            }
            folder.ClearContents(); // Speicher freigeben, große PSTs haben Hunderttausende Elemente
            await Task.Yield();
        }
    }

    /// <summary>Nur E-Mails (inkl. Besprechungsanfragen und Berichten), keine Kontakte, Termine, Aufgaben oder Notizen.</summary>
    static bool IsMail(XstMessage m)
    {
        var cls = m.PropertyValue(PropertyCanonicalName.PidTagMessageClass) as string ?? "IPM.Note";
        string[] excluded = { "IPM.Contact", "IPM.DistList", "IPM.Appointment", "IPM.Task", "IPM.StickyNote", "IPM.Activity",
                              "IPM.Configuration", "IPM.Microsoft", "IPM.ContentClassDef", "IPM.InfoPathForm", "IPM.Aggregation" };
        return !excluded.Any(e => cls.StartsWith(e, StringComparison.OrdinalIgnoreCase));
    }


    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// XstFile.Dispose räumt den Ordnerbaum auf und lädt dabei bereits freigegebene Ordner erneut aus der schon
    /// geschlossenen Datei nach – die Ausnahme ("magic cookie is missing") kann den ganzen Prozess beenden.
    /// Deshalb wird nur der Dateistrom geschlossen (die Objekte räumt der Garbage Collector weg).
    /// </summary>
    void Release()
    {
        var x = _xst;
        _xst = null;
        if (x is null) return;
        var stream = typeof(XstFile).GetField("_ReadStream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (stream?.GetValue(x) is Stream fs)
        {
            fs.Dispose();
            return;
        }
        try { x.Dispose(); } catch { /* ältere/neuere XstReader-Version: Schließen darf nicht scheitern */ }
    }

    /// <summary>Outlook-Datendateien an den Standardorten (neueste zuerst).</summary>
    public static IReadOnlyList<string> FindDataFiles()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new[]
        {
            Path.Combine(docs, "Outlook-Dateien"), Path.Combine(docs, "Outlook Files"),
            Path.Combine(local, "Microsoft", "Outlook"),
        };
        return dirs.Where(Directory.Exists)
            .SelectMany(d => SafeEnumerate(d, "*.pst").Concat(SafeEnumerate(d, "*.ost")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();

        static IEnumerable<string> SafeEnumerate(string dir, string pattern)
        {
            try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); }
            catch (Exception) { return Array.Empty<string>(); }
        }
    }
}

/// <summary>Wandelt eine Outlook-Nachricht in MIME um.</summary>
internal static partial class OutlookMessageConverter
{
    /// <summary>Kopfzeilen, die aus dem MIME-Inhalt stammen und beim Übernehmen der Transport-Kopfzeilen ersetzt werden.</summary>
    static readonly HeaderId[] ContentHeaders =
    {
        HeaderId.ContentType, HeaderId.ContentTransferEncoding, HeaderId.ContentDisposition, HeaderId.ContentId,
        HeaderId.ContentDescription, HeaderId.ContentLanguage, HeaderId.ContentLocation, HeaderId.ContentMd5, HeaderId.MimeVersion,
    };

    public static DateTimeOffset? DateOf(XstMessage m)
    {
        var dt = m.SubmittedTime ?? m.ReceivedTime ?? m.Date;
        if (dt is null) return null;
        var v = dt.Value;
        return new DateTimeOffset(v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc));
    }

    public static long SizeOf(XstMessage m) =>
        m.PropertyValue(PropertyCanonicalName.PidTagMessageSize) is int s ? s :
        m.PropertyValue(PropertyCanonicalName.PidTagMessageSize) is long l ? l : 0;

    public static HeaderList BuildHeaders(XstMessage m)
    {
        var transport = m.PropertyValue(PropertyCanonicalName.PidTagTransportMessageHeaders) as string;
        if (!string.IsNullOrWhiteSpace(transport))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(transport.TrimEnd() + "\r\n\r\n");
                var parsed = HeaderList.Load(new MemoryStream(bytes));
                if (parsed[HeaderId.From] is not null || parsed[HeaderId.MessageId] is not null)
                {
                    foreach (var id in ContentHeaders) parsed.RemoveAll(id);
                    return parsed;
                }
            }
            catch (FormatException) { /* defekte Kopfzeilen: aus den Eigenschaften neu aufbauen */ }
        }
        return Synthesize(m);
    }

    /// <summary>Kopfzeilen aus den MAPI-Eigenschaften (gesendete Elemente, Entwürfe, Exchange-interne E-Mails).</summary>
    static HeaderList Synthesize(XstMessage m)
    {
        var msg = new MimeMessage();
        var from = Sender(m);
        if (from is not null) msg.From.Add(from);
        AddRecipients(msg.To, m.Recipients.To, m.To);
        AddRecipients(msg.Cc, m.Recipients.Cc, m.Cc);
        AddRecipients(msg.Bcc, m.Recipients.Bcc, m.Bcc);
        msg.Subject = m.Subject ?? "";
        if (DateOf(m) is { } d) msg.Date = d;
        if (!string.IsNullOrWhiteSpace(m.InternetMessageId)) msg.MessageId = m.InternetMessageId.Trim().Trim('<', '>');
        var importance = m.PropertyValue(PropertyCanonicalName.PidTagImportance);
        if (importance is int imp) msg.Importance = imp switch { 2 => MessageImportance.High, 0 => MessageImportance.Low, _ => MessageImportance.Normal };
        msg.Headers.Add("X-MailVault-Source", "Outlook");

        var headers = new HeaderList();
        foreach (var h in msg.Headers)
            if (!ContentHeaders.Contains(h.Id)) headers.Add(h);
        return headers;
    }

    static MailboxAddress? Sender(XstMessage m)
    {
        string? Prop(PropertyCanonicalName n) => m.PropertyValue(n) as string;
        var name = Prop(PropertyCanonicalName.PidTagSentRepresentingName) ?? Prop(PropertyCanonicalName.PidTagSenderName) ?? m.From;
        var addr = Prop(PropertyCanonicalName.PidTagSentRepresentingSmtpAddress) ?? Prop(PropertyCanonicalName.PidTagSenderSmtpAddress);
        if (!IsSmtp(addr))
        {
            var a1 = Prop(PropertyCanonicalName.PidTagSentRepresentingEmailAddress);
            var a2 = Prop(PropertyCanonicalName.PidTagSenderEmailAddress);
            addr = IsSmtp(a1) ? a1 : IsSmtp(a2) ? a2 : null;
        }
        if (name is null && addr is null) return null;
        return Mailbox(name, addr);
    }

    static void AddRecipients(InternetAddressList list, IEnumerable<XstRecipient> recipients, string? fallbackDisplay)
    {
        foreach (var r in recipients)
        {
            var smtp = r.PropertyValue(PropertyCanonicalName.PidTagSmtpAddress) as string;
            if (!IsSmtp(smtp)) smtp = IsSmtp(r.Address) ? r.Address : null;
            list.Add(Mailbox(r.DisplayName, smtp));
        }
        // Manche PSTs haben keine Empfängertabelle, nur die Anzeigezeile ("Max Muster; Erika").
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(fallbackDisplay))
            foreach (var part in fallbackDisplay.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(InternetAddress.TryParse(part, out var a) && a is MailboxAddress mb && IsSmtp(mb.Address) ? mb : Mailbox(part, null));
    }

    static bool IsSmtp(string? a) => !string.IsNullOrWhiteSpace(a) && a.Contains('@') && !a.StartsWith('/');

    /// <summary>Ohne SMTP-Adresse (z. B. Exchange-intern) bleibt nur der Name; RFC 5322 verlangt eine Adresse,
    /// daher eine nicht zustellbare Platzhalteradresse.</summary>
    static MailboxAddress Mailbox(string? name, string? address)
    {
        var n = string.IsNullOrWhiteSpace(name) ? null : name.Trim().Trim('\'', '"');
        if (IsSmtp(address)) return new MailboxAddress(n, address!.Trim());
        var local = Regex.Replace((n ?? "unbekannt").ToLowerInvariant(), "[^a-z0-9._-]+", ".").Trim('.');
        return new MailboxAddress(n, (local.Length == 0 ? "unbekannt" : local) + "@outlook.invalid");
    }

    public static byte[] ToMime(XstMessage m, HeaderList headers)
    {
        var msg = new MimeMessage(headers.Select(h => h.Clone()));
        msg.Body = BuildBody(m, depth: 0);
        using var ms = new MemoryStream();
        msg.WriteTo(FormatOptions.Default, ms);
        m.ClearContents();
        return ms.ToArray();
    }

    static MimeEntity BuildBody(XstMessage m, int depth)
    {
        var cls = m.PropertyValue(PropertyCanonicalName.PidTagMessageClass) as string ?? "";
        var attachments = SafeAttachments(m);

        // S/MIME: Outlook speichert die signierte/verschlüsselte MIME-Struktur als einzigen Anhang.
        if (cls.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase) && attachments.Count == 1 && attachments[0].IsFile)
        {
            var bytes = Content(attachments[0]);
            if (cls.Contains("MultipartSigned", StringComparison.OrdinalIgnoreCase))
            {
                try { return MimeEntity.Load(new MemoryStream(bytes)); } catch (FormatException) { }
            }
            return new ApplicationPkcs7Mime(SecureMimeType.EnvelopedData, new MemoryStream(bytes));
        }

        var builder = new BodyBuilder();
        var body = SafeBody(m);
        switch (body?.Format)
        {
            case XstMessageBodyFormat.Html:
                builder.HtmlBody = body.Text ?? DecodeBytes(body.Bytes, m.Encoding);
                break;
            case XstMessageBodyFormat.Rtf:
                builder.TextBody = RtfToText(body.Text ?? DecodeBytes(body.Bytes, Encoding.Latin1));
                break;
            case XstMessageBodyFormat.PlainText:
                builder.TextBody = body.Text ?? DecodeBytes(body.Bytes, m.Encoding);
                break;
        }

        foreach (var att in attachments)
        {
            try
            {
                if (att.IsEmail && att.AttachedEmailMessage is { } inner && depth < 5)
                {
                    var innerMsg = new MimeMessage(BuildHeaders(inner).Select(h => h.Clone())) { Body = BuildBody(inner, depth + 1) };
                    builder.Attachments.Add(new MessagePart { Message = innerMsg });
                }
                else if (att.IsFile)
                {
                    var name = att.LongFileName ?? att.FileName ?? att.DisplayName ?? "Anhang";
                    var bytes = Content(att);
                    var mime = att.PropertyValue(PropertyCanonicalName.PidTagAttachMimeTag) as string;
                    var type = ContentType.TryParse(mime ?? MimeTypes.GetMimeType(name), out var ct) ? ct : new ContentType("application", "octet-stream");
                    var inline = att.HasContentId && builder.HtmlBody is not null &&
                                 builder.HtmlBody.Contains(att.ContentId!, StringComparison.OrdinalIgnoreCase);
                    var part = inline ? builder.LinkedResources.Add(name, bytes, type) : builder.Attachments.Add(name, bytes, type);
                    if (inline) part.ContentId = att.ContentId;
                }
            }
            catch
            {
                // Ein defekter Anhang soll nicht die ganze Nachricht verhindern.
            }
        }
        return builder.ToMessageBody() ?? new TextPart("plain") { Text = "" };
    }

    static List<XstAttachment> SafeAttachments(XstMessage m)
    {
        try { return m.Attachments.ToList(); } catch { return new(); }
    }

    static XstMessageBody? SafeBody(XstMessage m)
    {
        try { return m.Body; } catch { return null; }
    }

    static byte[] Content(XstAttachment a)
    {
        using var ms = new MemoryStream();
        a.SaveToStream(ms);
        return ms.ToArray();
    }

    static string DecodeBytes(byte[]? bytes, Encoding? enc) => bytes is null ? "" : (enc ?? Encoding.UTF8).GetString(bytes);

    /// <summary>Einfacher RTF-zu-Text-Wandler für Nachrichten, die Outlook nur als RTF speichert.</summary>
    internal static string RtfToText(string rtf)
    {
        if (!rtf.StartsWith("{\\rtf", StringComparison.Ordinal)) return rtf;
        var sb = new StringBuilder();
        var skipDepth = new Stack<bool>();
        var skip = false;
        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{') { skipDepth.Push(skip); continue; }
            if (c == '}') { skip = skipDepth.Count > 0 && skipDepth.Pop(); continue; }
            if (c == '\\')
            {
                if (i + 1 >= rtf.Length) break;
                var n = rtf[i + 1];
                if (n is '\\' or '{' or '}') { if (!skip) sb.Append(n); i++; continue; }
                if (n == '\'' && i + 3 < rtf.Length)
                {
                    if (!skip && byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                        sb.Append(Encoding.Latin1.GetString(new[] { b }));
                    i += 3;
                    continue;
                }
                if (n == '*') { skip = true; i++; continue; }
                var j = i + 1;
                while (j < rtf.Length && char.IsAsciiLetter(rtf[j])) j++;
                var word = rtf[(i + 1)..j];
                var numStart = j;
                if (j < rtf.Length && (rtf[j] == '-' || char.IsAsciiDigit(rtf[j]))) { j++; while (j < rtf.Length && char.IsAsciiDigit(rtf[j])) j++; }
                var num = rtf[numStart..j];
                if (j < rtf.Length && rtf[j] == ' ') j++;
                i = j - 1;
                switch (word)
                {
                    case "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "header" or "footer" or "htmltag" or "mhtmltag":
                        skip = true; break;
                    case "par" or "line" or "row" when !skip: sb.Append('\n'); break;
                    case "tab" or "cell" when !skip: sb.Append('\t'); break;
                    case "u" when !skip && int.TryParse(num, out var u):
                        sb.Append((char)(u < 0 ? u + 65536 : u));
                        if (i + 1 < rtf.Length && rtf[i + 1] == '?') i++;
                        break;
                }
                continue;
            }
            if (!skip && c != '\r' && c != '\n') sb.Append(c);
        }
        return MultiNewline().Replace(sb.ToString(), "\n\n").Trim();
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewline();
}
