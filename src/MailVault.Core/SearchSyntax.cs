using System.Globalization;
using System.Text;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>
/// Suchfeld-Syntax für Desktop und Web: Freitext plus Filter direkt im Suchfeld, z. B.
/// <c>von:telekom rechnung jahr:2024 anhang:ja</c> oder <c>an:"Anna Berg" betreff:urlaub nach:2023-05</c>.
/// Unbekannte Schlüssel bleiben Teil des Freitexts (z. B. Uhrzeiten wie 10:30).
/// </summary>
public static class SearchSyntax
{
    public sealed record Help(string Example, string Meaning);

    const int ExampleCount = 10;

    /// <summary>Beispiele für die Suchhilfe in der aktuellen Oberflächensprache (deutsche und englische Schlüssel funktionieren immer).</summary>
    public static IReadOnlyList<Help> Examples =>
        Enumerable.Range(1, ExampleCount)
            .Select(i => new Help(CoreText.T($"SearchHelp_{i}_Example"), CoreText.T($"SearchHelp_{i}_Meaning")))
            .ToList();

    static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["von"] = "from", ["from"] = "from", ["absender"] = "from",
        ["an"] = "to", ["to"] = "to", ["empfänger"] = "to", ["empfaenger"] = "to", ["cc"] = "to",
        ["betreff"] = "subject", ["subject"] = "subject",
        ["anhang"] = "att", ["has"] = "att", ["attachment"] = "att",
        ["jahr"] = "year", ["year"] = "year",
        ["nach"] = "after", ["after"] = "after", ["seit"] = "after", ["ab"] = "after",
        ["vor"] = "before", ["before"] = "before", ["bis"] = "before",
    };

    /// <summary>Überträgt Filter aus dem Suchtext in die Suchkriterien; <see cref="SearchCriteria.Query"/> enthält danach nur noch den Freitext.</summary>
    /// <exception cref="ArgumentException">Ungültiger Wert, z. B. <c>jahr:abc</c>.</exception>
    public static SearchCriteria Apply(SearchCriteria c)
    {
        if (string.IsNullOrWhiteSpace(c.Query)) return c;
        var free = new StringBuilder();
        DateTime? after = null, before = null;
        foreach (var token in Tokenize(c.Query))
        {
            var colon = token.IndexOf(':');
            if (colon > 0 && colon < token.Length - 1 && token[0] != '"' && Keys.TryGetValue(token[..colon], out var key))
            {
                var value = token[(colon + 1)..].Trim('"').Trim();
                switch (key)
                {
                    case "from": c.From = value; break;
                    case "to": c.To = value; break;
                    case "subject": c.Subject = value; break;
                    case "att":
                        c.HasAttachments = value.ToLowerInvariant() switch
                        {
                            "ja" or "yes" or "true" or "1" or "attachment" => true,
                            "nein" or "no" or "false" or "0" => false,
                            _ => throw new ArgumentException(CoreText.F("Search_BadAttachment", token[..colon] + ":" + value)),
                        };
                        break;
                    case "year":
                        if (!int.TryParse(value, out var y) || y is < 1970 or > 2100) throw new ArgumentException(CoreText.F("Search_BadYear", token[..colon] + ":" + value));
                        after = new DateTime(y, 1, 1);
                        before = new DateTime(y + 1, 1, 1);
                        break;
                    case "after": after = ParseDate(value, token[..colon]); break;
                    case "before": before = ParseDate(value, token[..colon]); break;
                }
                continue;
            }
            if (free.Length > 0) free.Append(' ');
            free.Append(token);
        }
        c.Query = free.Length == 0 ? null : free.ToString();
        if (after is not null || before is not null)
        {
            c.DateRange = DateRangeType.Custom;
            c.DateFrom = after ?? new DateTime(1970, 1, 1);
            // ResolveDateRange behandelt DateTo als inklusiven Tag
            c.DateTo = (before ?? new DateTime(2100, 1, 1)).AddDays(-1);
        }
        return c;
    }

    /// <summary>nach:2023 = ab 1.1.2023; vor:2023 = vor dem 1.1.2023 (exklusives Ende).</summary>
    static DateTime ParseDate(string value, string typedKey)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        if (DateTime.TryParseExact(value, new[] { "yyyy-MM-dd", "d.M.yyyy", "dd.MM.yyyy" }, de, DateTimeStyles.None, out var day))
            return day;
        if (DateTime.TryParseExact(value, new[] { "yyyy-MM", "M.yyyy", "MM.yyyy" }, de, DateTimeStyles.None, out var month))
            return month;
        if (int.TryParse(value, out var year) && year is >= 1970 and <= 2100)
            return new DateTime(year, 1, 1);
        throw new ArgumentException(CoreText.F("Search_BadDate", typedKey + ":" + value));
    }

    /// <summary>Zerlegt an Leerzeichen, hält "Phrasen" und schluessel:"Wert mit Leerzeichen" zusammen.</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var ch in text)
        {
            if (ch == '"') { inQuote = !inQuote; sb.Append(ch); continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
