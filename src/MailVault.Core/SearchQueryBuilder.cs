using System.Text;

namespace MailVault.Core;

/// <summary>
/// Übersetzt die Benutzereingabe in einen FTS5-Ausdruck. Unterstützt werden die Termtypen,
/// einer üblichen Desktop-Suche: Einzelwort,
/// "beginnt mit" (wort*), Phrase ("…") und ODER-Listen (a OR b). Wörter werden implizit mit UND verknüpft,
/// ein vorangestelltes "-" schließt einen Begriff aus.
/// </summary>
public static class SearchQueryBuilder
{
    public static string? BuildFtsQuery(string? input, IReadOnlyCollection<string> columns)
    {
        if (string.IsNullOrWhiteSpace(input) || columns.Count == 0) return null;
        var tokens = Tokenize(input);
        var positive = new List<string>();
        var negative = new List<string>();
        var pendingOr = false;

        foreach (var (text, quoted) in tokens)
        {
            if (!quoted && (text == "OR" || text == "ODER" || text == "|"))
            {
                pendingOr = positive.Count > 0;
                continue;
            }
            if (!quoted && (text == "AND" || text == "UND")) continue;

            var negate = !quoted && text.StartsWith('-') && text.Length > 1;
            var raw = negate ? text[1..] : text;
            var term = ToFtsTerm(raw, quoted, prefixByDefault: !negate);
            if (term is null) continue;

            if (negate) { negative.Add(term); pendingOr = false; continue; }
            if (pendingOr && positive.Count > 0)
            {
                positive[^1] = $"({StripParens(positive[^1])} OR {term})";
                pendingOr = false;
            }
            else positive.Add(term);
        }

        if (positive.Count == 0) return null; // FTS5 kennt kein rein negatives Suchen
        var colFilter = "{" + string.Join(" ", columns) + "}";
        var sb = new StringBuilder();
        sb.Append(colFilter).Append(" : (").Append(string.Join(" AND ", positive)).Append(')');
        foreach (var n in negative) sb.Append(" NOT ").Append(colFilter).Append(" : ").Append(n);
        return sb.ToString();
    }

    static string StripParens(string s) => s.StartsWith('(') && s.EndsWith(')') ? s[1..^1] : s;

    static string? ToFtsTerm(string raw, bool quoted, bool prefixByDefault)
    {
        if (quoted)
        {
            var phrase = raw.Trim();
            return phrase.Length == 0 ? null : Quote(phrase);
        }
        // Wie "beginnt mit": "winter" findet auch "Wintergarten" (deutsche Komposita). Exakt nur in Anführungszeichen.
        var prefix = prefixByDefault || raw.EndsWith('*');
        var word = raw.Trim('*');
        // FTS5-Tokenizer zerlegt ohnehin an Satzzeichen; ein Begriff wie "max@mail.de" wird so zur Phrase.
        if (word.Length == 0) return null;
        return prefix ? Quote(word) + "*" : Quote(word);
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    static List<(string Text, bool Quoted)> Tokenize(string input)
    {
        var result = new List<(string, bool)>();
        var i = 0;
        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            if (input[i] == '"')
            {
                var end = input.IndexOf('"', i + 1);
                if (end < 0) end = input.Length;
                result.Add((input[(i + 1)..end], true));
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] != '"') i++;
            result.Add((input[start..i], false));
        }
        return result;
    }
}
