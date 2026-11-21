using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;
using UglyToad.PdfPig;

namespace MailVault.Core;

/// <summary>Aus einer MIME-Nachricht extrahierte Metadaten und Volltext.</summary>
public sealed class IndexedMessage
{
    public string? MessageId { get; init; }
    public DateTime DateUtc { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Bcc { get; init; } = "";
    public string Subject { get; init; } = "";
    public bool HasAttachments { get; init; }
    /// <summary>1 = hoch, 3 = normal, 5 = niedrig.</summary>
    public int Priority { get; init; } = 3;
    public string Body { get; init; } = "";
    public string AttachmentNames { get; init; } = "";
    public string AttachmentContent { get; init; } = "";
}

public static partial class MessageIndexer
{
    static MessageIndexer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    const int MaxBodyChars = 2_000_000;
    const int MaxAttachmentChars = 1_000_000;
    const int MaxAttachmentBytesForExtraction = 30 * 1024 * 1024;

    public static IndexedMessage Index(MimeMessage msg)
    {
        var names = new List<string>();
        var content = new StringBuilder();
        foreach (var att in msg.Attachments)
        {
            var name = att is MimePart mp ? mp.FileName : (att as MessagePart)?.Message?.Subject;
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            if (content.Length < MaxAttachmentChars)
            {
                try
                {
                    var text = ExtractAttachmentText(att);
                    if (!string.IsNullOrWhiteSpace(text))
                        content.AppendLine(Truncate(text, MaxAttachmentChars - content.Length));
                }
                catch
                {
                    // Defekte oder verschlüsselte Anhänge werden nicht indiziert, die Nachricht bleibt archiviert.
                }
            }
        }

        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body) && msg.HtmlBody is { } html) body = HtmlToText(html);

        return new IndexedMessage
        {
            MessageId = NormalizeMessageId(msg.MessageId),
            DateUtc = msg.Date == DateTimeOffset.MinValue ? DateTime.UtcNow : msg.Date.UtcDateTime,
            From = FormatAddresses(msg.From),
            To = FormatAddresses(msg.To),
            Cc = FormatAddresses(msg.Cc),
            Bcc = FormatAddresses(msg.Bcc),
            Subject = msg.Subject ?? "",
            HasAttachments = names.Count > 0 || msg.Attachments.Any(),
            Priority = msg.Priority switch
            {
                MessagePriority.Urgent => 1,
                MessagePriority.NonUrgent => 5,
                _ => msg.Importance switch { MessageImportance.High => 1, MessageImportance.Low => 5, _ => 3 },
            },
            Body = Truncate(body ?? "", MaxBodyChars),
            AttachmentNames = string.Join(" ", names),
            AttachmentContent = content.ToString(),
        };
    }

    public static string FormatAddresses(InternetAddressList list) =>
        string.Join(", ", list.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));

    static string? NormalizeMessageId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().Trim('<', '>').ToLowerInvariant();

    /// <summary>
    /// Duplikatschlüssel. Das Original nutzt Message-ID + HeaderHash (MessageArchiveInfo);
    /// wir hashen die identitätsstiftenden Kopfzeilen, damit der Schlüssel bereits aus den
    /// Kopfzeilen allein (IMAP/POP3 ohne Body-Download) berechnet werden kann.
    /// </summary>
    public static string ComputeHeaderHash(HeaderList headers, long size)
    {
        static string H(HeaderList h, HeaderId id) => Regex.Replace(h[id] ?? "", @"\s+", " ").Trim();
        var messageId = H(headers, HeaderId.MessageId);
        var sb = new StringBuilder()
            .Append("mid:").Append(messageId.ToLowerInvariant()).Append('\n')
            .Append("date:").Append(H(headers, HeaderId.Date)).Append('\n')
            .Append("from:").Append(H(headers, HeaderId.From)).Append('\n')
            .Append("to:").Append(H(headers, HeaderId.To)).Append('\n')
            .Append("cc:").Append(H(headers, HeaderId.Cc)).Append('\n')
            .Append("subject:").Append(H(headers, HeaderId.Subject)).Append('\n');
        if (messageId.Length == 0) sb.Append("size:").Append(size);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static string HtmlToText(string html)
    {
        var s = ScriptStyleRegex().Replace(html, " ");
        s = BlockTagRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = SpaceRegex().Replace(s, " ");
        return MultiNewlineRegex().Replace(s, "\n").Trim();
    }

    static string? ExtractAttachmentText(MimeEntity entity)
    {
        if (entity is MessagePart { Message: { } inner })
        {
            var idx = Index(inner);
            return $"{idx.Subject}\n{idx.From}\n{idx.Body}\n{idx.AttachmentContent}";
        }
        if (entity is not MimePart part || part.Content is null) return null;

        using var ms = new MemoryStream();
        part.Content.DecodeTo(ms);
        if (ms.Length == 0 || ms.Length > MaxAttachmentBytesForExtraction) return null;
        var bytes = ms.ToArray();
        var ext = Path.GetExtension(part.FileName ?? "").ToLowerInvariant();
        var mime = part.ContentType.MimeType.ToLowerInvariant();

        if (mime == "application/pdf" || ext == ".pdf") return ExtractPdf(bytes);
        if (ext is ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp") return ExtractOfficeXml(bytes);
        if (mime == "text/html" || ext is ".html" or ".htm") return HtmlToText(DecodeText(part, bytes));
        if (mime.StartsWith("text/") || ext is ".txt" or ".csv" or ".log" or ".md" or ".xml" or ".json" or ".ics" or ".vcf")
            return DecodeText(part, bytes);
        return null;
    }

    static string DecodeText(MimePart part, byte[] bytes)
    {
        var charset = part.ContentType.Charset;
        Encoding enc;
        try { enc = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch { enc = Encoding.UTF8; }
        return enc.GetString(bytes);
    }

    static string ExtractPdf(byte[] bytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(bytes);
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            if (sb.Length > MaxAttachmentChars) break;
        }
        return sb.ToString();
    }

    /// <summary>OOXML/ODF sind ZIP-Container mit XML; wir sammeln den Text aller relevanten XML-Teile.</summary>
    static string ExtractOfficeXml(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sb = new StringBuilder();
        foreach (var e in zip.Entries)
        {
            var n = e.FullName;
            bool relevant = n == "word/document.xml" || n == "content.xml" || n == "xl/sharedStrings.xml" ||
                            (n.StartsWith("ppt/slides/slide") && n.EndsWith(".xml"));
            if (!relevant || e.Length > 20 * 1024 * 1024) continue;
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            var xml = r.ReadToEnd();
            xml = Regex.Replace(xml, "</(w:p|a:p|text:p|si)>", "\n");
            sb.AppendLine(WebUtility.HtmlDecode(TagRegex().Replace(xml, " ")));
            if (sb.Length > MaxAttachmentChars) break;
        }
        return SpaceRegex().Replace(sb.ToString(), " ");
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max)];

    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();
    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h[1-6])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex SpaceRegex();
    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex MultiNewlineRegex();
}
