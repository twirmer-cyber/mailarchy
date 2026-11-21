using MailVault.App.Localization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MailVault.App.Services;

/// <summary>
/// Wandelt das einfache Markdown der KI-Antworten sicher in HTML um: Zuerst wird alles HTML-maskiert,
/// danach werden nur wenige Muster (Überschriften, Listen, Tabellen, fett, Code, [#ID]-Belege) erzeugt.
/// </summary>
public static class MiniMarkdown
{
    static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    static readonly Regex Italic = new(@"(?<![\w*])_(.+?)_(?!\w)", RegexOptions.Compiled);
    static readonly Regex Code = new(@"`([^`]+)`", RegexOptions.Compiled);
    static readonly Regex Citation = new(@"\[#(\d+)\]", RegexOptions.Compiled);

    /// <param name="citationTitle">Liefert für eine E-Mail-ID einen Kurztitel (oder null, wenn unbekannt).</param>
    /// <param name="cited">Erhält die belegten E-Mails in der Reihenfolge ihrer Fußnotennummern.</param>
    public static string ToHtml(string markdown, Func<long, string?> citationTitle, List<long>? cited = null)
    {
        var numbers = new Dictionary<long, int>();
        cited ??= new List<long>();
        string? Cite(long id)
        {
            var title = citationTitle(id);
            if (title is null) return null;
            if (!numbers.TryGetValue(id, out var n)) { n = numbers[id] = numbers.Count + 1; cited.Add(id); }
            return $"<sup><a class=\"fn\" href=\"mailvault://msg/{id}\" title=\"{WebUtility.HtmlEncode(title)}\">{n}</a></sup>";
        }
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        string? list = null;
        var table = new List<string[]>();

        void CloseList() { if (list is not null) { sb.Append($"</{list}>"); list = null; } }
        void FlushTable()
        {
            if (table.Count == 0) return;
            sb.Append("<table>");
            for (var i = 0; i < table.Count; i++)
            {
                var tag = i == 0 ? "th" : "td";
                sb.Append("<tr>");
                foreach (var cell in table[i]) sb.Append($"<{tag}>{Inline(cell, Cite)}</{tag}>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            table.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var t = line.TrimStart();
            if (t.StartsWith('|') && t.EndsWith('|') && t.Length > 1)
            {
                CloseList();
                var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) continue; // Trennzeile
                table.Add(cells);
                continue;
            }
            FlushTable();
            if (t.Length == 0) { CloseList(); continue; }
            var h = Regex.Match(t, @"^(#{1,4})\s+(.*)$");
            if (h.Success)
            {
                CloseList();
                var level = Math.Min(4, h.Groups[1].Length + 1);
                sb.Append($"<h{level}>{Inline(h.Groups[2].Value, Cite)}</h{level}>");
                continue;
            }
            var bullet = Regex.Match(t, @"^[-*•]\s+(.*)$");
            var number = Regex.Match(t, @"^\d+[.)]\s+(.*)$");
            if (bullet.Success || number.Success)
            {
                var kind = bullet.Success ? "ul" : "ol";
                if (list != kind) { CloseList(); sb.Append($"<{kind}>"); list = kind; }
                sb.Append($"<li>{Inline((bullet.Success ? bullet : number).Groups[1].Value, Cite)}</li>");
                continue;
            }
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,})$")) { CloseList(); sb.Append("<hr>"); continue; }
            CloseList();
            sb.Append($"<p>{Inline(t, Cite)}</p>");
        }
        FlushTable();
        CloseList();
        return sb.ToString();
    }

    static string Inline(string text, Func<long, string?> cite)
    {
        var s = WebUtility.HtmlEncode(text);
        s = Code.Replace(s, "<code>$1</code>");
        s = Bold.Replace(s, "<strong>$1</strong>");
        s = Italic.Replace(s, "<em>$1</em>");
        s = Citation.Replace(s, m =>
        {
            var id = long.Parse(m.Groups[1].Value);
            return cite(id) ?? $"<sup class=\"missing\" title=\"{WebUtility.HtmlEncode(L.T("Md_MissingCitation"))}\">?</sup>";
        });
        return s;
    }

}
