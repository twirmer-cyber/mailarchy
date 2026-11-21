using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailVault.Core.Localization;
using MimeKit;

namespace MailVault.Web;

/// <summary>Aufbereitung einer Nachricht für die Web-Ansicht (analog zur Vorschau der Desktop-App).</summary>
internal static class MailRenderer
{
    internal sealed record AttachmentInfo(int Index, string Name, long Size, string ContentType);

    public static List<MimeEntity> GetAttachments(MimeMessage msg) => msg.Attachments.ToList();

    public static string AttachmentName(MimeEntity a)
    {
        var name = a switch
        {
            MimePart mp => mp.FileName,
            MessagePart mp => (string.IsNullOrWhiteSpace(mp.Message?.Subject) ? CoreText.T("Web_MessageFileName") : mp.Message!.Subject) + ".eml",
            _ => null,
        };
        return string.IsNullOrWhiteSpace(name) ? CoreText.T("Web_AttachmentFileName") : name!;
    }

    public static IReadOnlyList<AttachmentInfo> DescribeAttachments(MimeMessage msg) =>
        GetAttachments(msg).Select((a, i) => new AttachmentInfo(i, AttachmentName(a), EstimateSize(a), a.ContentType.MimeType)).ToList();

    static long EstimateSize(MimeEntity a) => a switch
    {
        MimePart { Content.Stream: { } st } mp => mp.ContentTransferEncoding == ContentEncoding.Base64 ? st.Length * 3 / 4 : st.Length,
        _ => 0,
    };

    public static byte[] GetAttachmentContent(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        if (entity is MimePart { Content: { } c }) c.DecodeTo(ms);
        else if (entity is MessagePart { Message: { } m }) m.WriteTo(ms);
        return ms.ToArray();
    }

    public static string PlainText(MimeMessage msg) =>
        msg.TextBody ?? Core.MessageIndexer.HtmlToText(msg.HtmlBody ?? "");

    /// <summary>Content-Security-Policy der Mail-Ansicht: Sandbox ohne Skripte, nur eingebettete Bilder.</summary>
    public static string BodyCsp(bool allowExternal)
    {
        var ext = allowExternal ? " https: http:" : "";
        return "sandbox allow-popups allow-popups-to-escape-sandbox; default-src 'none'; " +
               $"img-src data:{ext}; style-src 'unsafe-inline'{ext}; font-src data:{ext}; media-src data:{ext}; " +
               "form-action 'none'; frame-ancestors 'self'";
    }

    static readonly Regex DangerousTags = new(
        @"<(script|iframe|object|embed|frame|frameset|base|form|link)\b[^>]*>(.*?</\1\s*>)?|</?(script|iframe|object|embed|frame|frameset|form)\b[^>]*>|<meta[^>]+http-equiv[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex CidRef = new(@"cid:([^""'\s\)>]+)", RegexOptions.IgnoreCase);
    static readonly Regex UrlRegex = new(@"(https?://[^\s<>&""]+)");

    const string BaseStyle =
        "html{background:#fff;color-scheme:light}body{background:#fff;font-family:'Segoe UI',system-ui,sans-serif;" +
        "font-size:14px;margin:14px;color:#1e2329;word-wrap:break-word;overflow-wrap:anywhere}img{max-width:100%;height:auto}" +
        "pre{white-space:pre-wrap;word-wrap:break-word;font-family:inherit;margin:0}";

    /// <summary>Erzeugt das HTML-Dokument für den Iframe. Skripte werden entfernt und zusätzlich per CSP-Sandbox blockiert.</summary>
    public static string BuildHtml(MimeMessage msg)
    {
        string body;
        if (msg.HtmlBody is { } html)
        {
            // Mehrfach anwenden, damit verschachtelte Konstrukte wie "<scr<script></script>ipt>" nicht übrig bleiben.
            for (var i = 0; i < 8; i++)
            {
                var cleaned = DangerousTags.Replace(html, "");
                if (cleaned.Length == html.Length) break;
                html = cleaned;
            }
            var cids = msg.BodyParts.OfType<MimePart>()
                .Where(p => !string.IsNullOrEmpty(p.ContentId) && p.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.ContentId!.Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            html = CidRef.Replace(html, m =>
            {
                if (!cids.TryGetValue(WebUtility.UrlDecode(m.Groups[1].Value), out var part) || part.Content is null) return m.Value;
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                if (ms.Length > 5 * 1024 * 1024) return m.Value;
                var mime = Regex.IsMatch(part.ContentType.MimeType, @"^image/[\w.+-]+$") ? part.ContentType.MimeType : "application/octet-stream";
                return $"data:{mime};base64,{Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length)}";
            });
            body = html;
        }
        else
        {
            var text = WebUtility.HtmlEncode(msg.TextBody ?? "");
            text = UrlRegex.Replace(text, "<a href=\"$1\" rel=\"noopener noreferrer\">$1</a>");
            body = $"<pre>{text}</pre>";
        }

        return new StringBuilder()
            .Append("<!DOCTYPE html><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">")
            .Append("<base target=\"_blank\"><meta name=\"color-scheme\" content=\"light\"><style>").Append(BaseStyle).Append("</style>")
            .Append(body)
            .ToString();
    }
}
