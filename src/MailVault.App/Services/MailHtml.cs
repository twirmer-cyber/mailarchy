using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailVault.App.Localization;
using MailVault.Core;
using MimeKit;

namespace MailVault.App.Services;

/// <summary>
/// Baut aus einer archivierten Nachricht sicheres HTML für die Anzeige (Vorschau) und den PDF-Export:
/// keine Skripte oder Frames, eingebettete Bilder als data:-Adressen, externe Inhalte nur auf Wunsch.
/// </summary>
public static class MailHtml
{
    static readonly Regex ExternalRef = new(@"(src|background|href)\s*=\s*[""']?\s*https?:|url\(\s*[""']?\s*https?:", RegexOptions.IgnoreCase);
    static readonly Regex DangerousTags = new(@"<(script|iframe|object|embed|frame|frameset|base)\b[^>]*>(.*?</\1\s*>)?|<meta[^>]+http-equiv[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex CidRef = new(@"cid:([^""'\s\)>]+)", RegexOptions.IgnoreCase);

    /// <summary>HTML für die Vorschau; HasExternal meldet blockierte Bilder oder Stile aus dem Internet.</summary>
    public static (string Html, bool HasExternal) Build(MimeMessage msg, bool allowExternal)
    {
        var (body, hasExternal) = Body(msg);
        return (Head(allowExternal, "", msg.Subject) + body, hasExternal);
    }

    /// <summary>
    /// HTML für den PDF-Export: Kopfblock mit Betreff, Absender, Empfängern, Datum, Ordner und Anhängen, darunter
    /// die Nachricht. Externe Inhalte werden nie geladen – ein Export soll keine Lesebestätigung auslösen.
    /// </summary>
    public static string BuildForPrint(MimeMessage msg, string? folderPath)
    {
        var (body, _) = Body(msg);
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var subject = string.IsNullOrWhiteSpace(msg.Subject) ? L.T("Common_NoSubject") : msg.Subject;
        var rows = new StringBuilder();
        void Row(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Append($"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");
        }
        Row(L.T("Common_From"), MessageIndexer.FormatAddresses(msg.From));
        Row(L.T("Preview_To"), MessageIndexer.FormatAddresses(msg.To));
        Row(L.T("Preview_CcLabel").TrimEnd(':'), MessageIndexer.FormatAddresses(msg.Cc));
        if (msg.Date != DateTimeOffset.MinValue)
            Row(L.T("Pdf_Date"), string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:dddd}, {0:g}", msg.Date.LocalDateTime));
        Row(L.T("Pdf_Folder"), folderPath ?? "");
        var attachments = msg.Attachments.Select(a => a is MimePart mp ? mp.FileName : (a as MessagePart)?.Message?.Subject + ".eml")
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (attachments.Count > 0) Row(L.T("Pdf_Attachments"), string.Join(", ", attachments));

        const string printCss = ".mv-head{font-family:'Segoe UI',sans-serif;border-bottom:1px solid #c9ced6;margin:0 0 16px;padding:0 0 10px}"
            + ".mv-head h1{font-size:17px;margin:0 0 8px;color:#1e2329}.mv-head table{border-collapse:collapse;font-size:12px}"
            + ".mv-head th{text-align:left;font-weight:600;color:#5b6573;padding:1px 14px 1px 0;vertical-align:top;white-space:nowrap}"
            + ".mv-head td{color:#1e2329;padding:1px 0}"
            + "img[src^=\"http\"],img[src^=\"//\"]{display:none}" // blockierte Internet-Bilder nicht als kaputtes Symbol drucken
            + ".mv-foot{font-family:'Segoe UI',sans-serif;font-size:10px;color:#7a8390;border-top:1px solid #e1e4e8;margin-top:20px;padding-top:6px}";
        var head = $"<div class=\"mv-head\"><h1>{E(subject)}</h1><table>{rows}</table></div>";
        var foot = $"<div class=\"mv-foot\">{E(L.F("Pdf_Footer", DateTime.Now))}</div>";
        // Eigener Titel: sonst nimmt WebView2 die data:-Adresse (die ganze Mail) als Dokumenttitel ins PDF
        return Head(false, printCss, subject) + head + body + foot;
    }

    static string Head(bool allowExternal, string extraCss, string? title)
    {
        var external = allowExternal ? " https: http:" : "";
        var csp = $"default-src 'none'; img-src data: cid:{external}; style-src 'unsafe-inline'{external}; font-src data:{external}; media-src data:{external}";
        return new StringBuilder()
            .Append("<meta charset=\"utf-8\">")
            .Append($"<title>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "MailVault" : title)}</title>")
            .Append($"<meta http-equiv=\"Content-Security-Policy\" content=\"{csp}\">")
            .Append("<meta name=\"color-scheme\" content=\"light\"><style>html{background:#fff;color-scheme:light}body{background:#fff;font-family:'Segoe UI',sans-serif;font-size:13px;margin:14px;color:#1e2329;word-wrap:break-word}img{max-width:100%;height:auto}")
            .Append(extraCss)
            .Append("</style>")
            .ToString();
    }

    static (string Body, bool HasExternal) Body(MimeMessage msg)
    {
        if (msg.HtmlBody is { } html)
        {
            html = DangerousTags.Replace(html, "");
            var cids = msg.BodyParts.OfType<MimePart>()
                .Where(p => !string.IsNullOrEmpty(p.ContentId) && p.ContentType.MediaType == "image")
                .GroupBy(p => p.ContentId.Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            html = CidRef.Replace(html, m =>
            {
                if (!cids.TryGetValue(WebUtility.UrlDecode(m.Groups[1].Value), out var part) || part.Content is null) return m.Value;
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                if (ms.Length > 5 * 1024 * 1024) return m.Value;
                return $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
            });
            var hasExternal = ExternalRef.Matches(html).Any(m => !m.Value.StartsWith("href", StringComparison.OrdinalIgnoreCase));
            return (html, hasExternal);
        }
        var text = WebUtility.HtmlEncode(msg.TextBody ?? "");
        text = Regex.Replace(text, @"(https?://[^\s<>&]+)", "<a href=\"$1\">$1</a>");
        return ($"<pre style=\"white-space:pre-wrap;word-wrap:break-word;font-family:'Segoe UI',sans-serif;font-size:13px;margin:0\">{text}</pre>", false);
    }
}
