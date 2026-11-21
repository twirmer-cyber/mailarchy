using System.Text.RegularExpressions;
using MimeKit;

namespace MailVault.Core.Ai;

public sealed record ContactNode(string Address, string Name, int FromThem, int ToThem, int Total, DateTime FirstUtc, DateTime LastUtc, bool IsOwner)
{
    public string Domain => Address.Contains('@') ? Address[(Address.IndexOf('@') + 1)..] : "";
    public string Label => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}

public sealed record ContactEdge(string A, string B, int Weight);

public sealed record MonthCount(string Month, int Count);

/// <summary>"Wer kennt wen": Kontaktnetz aus den Kopfzeilen (Von/An/Cc) – wird lokal berechnet, nichts verlässt den PC.</summary>
public sealed record ContactNetwork(IReadOnlyList<ContactNode> Nodes, IReadOnlyList<ContactEdge> Edges, IReadOnlyList<MonthCount> Volume,
    int MessageCount, IReadOnlyList<string> OwnerAddresses)
{
    /// <summary>Automatisch versendete Adressen (Newsletter, Benachrichtigungen) – standardmäßig ausgeblendet.</summary>
    static readonly Regex Automated = new(@"^(no-?reply|do-?not-?reply|noreply|newsletter|news|info|mailer-daemon|postmaster|notification|notifications|alerts?|bounce|support|service|marketing|hello|team|updates?)[@.+-]|@(.*\.)?(mailchimp|sendgrid|amazonses|mailgun|bounces?)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsAutomated(string address) => Automated.IsMatch(address);

    /// <param name="ownerAddresses">Eigene Adressen; leer = automatisch die häufigste Adresse.</param>
    /// <param name="maxParticipants">Rundmails mit mehr Beteiligten zählen nicht als Bekanntschaft.</param>
    public static ContactNetwork Build(IEnumerable<HeaderRow> rows, IReadOnlyCollection<string>? ownerAddresses = null,
        int maxNodes = 60, int minMessages = 2, bool hideAutomated = true, DateTime? fromUtc = null, DateTime? toUtc = null, int maxParticipants = 12)
    {
        var mails = new List<(DateTime Date, string From, List<string> Others)>();
        var names = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var volume = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            if (fromUtc is { } f && r.DateUtc < f) continue;
            if (toUtc is { } t && r.DateUtc > t) continue;
            var from = Parse(r.From, names).FirstOrDefault();
            var others = Parse(r.To, names).Concat(Parse(r.Cc, names)).Where(a => a != from).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (from is null && others.Count == 0) continue;
            mails.Add((r.DateUtc, from ?? "", others));
            var month = r.DateUtc.ToString("yyyy-MM");
            volume[month] = volume.GetValueOrDefault(month) + 1;
        }

        var owners = new HashSet<string>(ownerAddresses?.Where(a => a.Contains('@')).Select(a => a.Trim().ToLowerInvariant()) ?? Enumerable.Empty<string>());
        if (owners.Count == 0)
        {
            var top = mails.SelectMany(m => m.Others.Append(m.From)).Where(a => a.Length > 0)
                .GroupBy(a => a).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (top is not null) owners.Add(top.Key);
        }

        var stats = new Dictionary<string, (int From, int To, DateTime First, DateTime Last)>();
        void Touch(string a, bool isFrom, DateTime d)
        {
            var s = stats.TryGetValue(a, out var v) ? v : (From: 0, To: 0, First: d, Last: d);
            stats[a] = (s.From + (isFrom ? 1 : 0), s.To + (isFrom ? 0 : 1), d < s.First ? d : s.First, d > s.Last ? d : s.Last);
        }
        foreach (var m in mails)
        {
            if (m.From.Length > 0) Touch(m.From, true, m.Date);
            foreach (var o in m.Others) Touch(o, false, m.Date);
        }

        bool Keep(string a) => owners.Contains(a) || !hideAutomated || !IsAutomated(a);
        var selected = stats
            .Where(kv => Keep(kv.Key) && (owners.Contains(kv.Key) || kv.Value.From + kv.Value.To >= minMessages))
            .OrderByDescending(kv => owners.Contains(kv.Key)).ThenByDescending(kv => kv.Value.From + kv.Value.To)
            .Take(Math.Max(2, maxNodes))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var edges = new Dictionary<(string, string), int>();
        foreach (var m in mails)
        {
            var people = m.Others.Prepend(m.From).Where(a => a.Length > 0 && selected.Contains(a)).Distinct().ToList();
            if (people.Count < 2 || m.Others.Count + 1 > maxParticipants) continue;
            for (var i = 0; i < people.Count; i++)
                for (var j = i + 1; j < people.Count; j++)
                {
                    var key = string.CompareOrdinal(people[i], people[j]) < 0 ? (people[i], people[j]) : (people[j], people[i]);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
        }

        var nodes = selected.Select(a =>
        {
            var s = stats[a];
            var name = names.TryGetValue(a, out var n) && n.Count > 0 ? n.OrderByDescending(x => x.Value).First().Key : "";
            return new ContactNode(a, name, s.From, s.To, s.From + s.To, s.First, s.Last, owners.Contains(a));
        }).OrderByDescending(n => n.IsOwner).ThenByDescending(n => n.Total).ToList();

        return new ContactNetwork(nodes,
            edges.Select(kv => new ContactEdge(kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderByDescending(e => e.Weight).ToList(),
            volume.Select(kv => new MonthCount(kv.Key, kv.Value)).ToList(),
            mails.Count, owners.ToList());
    }

    static List<string> Parse(string text, Dictionary<string, Dictionary<string, int>> names)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || !InternetAddressList.TryParse(text, out var list)) return result;
        foreach (var mb in list.Mailboxes)
        {
            var addr = mb.Address?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(addr) || !addr.Contains('@')) continue;
            result.Add(addr);
            var name = mb.Name?.Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(name) && !name.Contains('@'))
            {
                if (!names.TryGetValue(addr, out var n)) names[addr] = n = new Dictionary<string, int>();
                n[name] = n.GetValueOrDefault(name) + 1;
            }
        }
        return result;
    }

    /// <summary>Kompakte Textfassung für die KI-Deutung (nur Namen, Adressdomains, Zahlen – keine Inhalte).</summary>
    public string ToPromptText(int maxEdges = 150)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Ausgewertete E-Mails: {MessageCount}. Eigene Adresse(n): {string.Join(", ", OwnerAddresses)}");
        sb.AppendLine("Kontakte (Adresse | Name | E-Mails von ihm/ihr | an ihn/sie | erster–letzter Kontakt):");
        foreach (var n in Nodes)
            sb.AppendLine($"- {n.Address} | {n.Name} | {n.FromThem} | {n.ToThem} | {n.FirstUtc:yyyy-MM}–{n.LastUtc:yyyy-MM}{(n.IsOwner ? " | (Archivbesitzer)" : "")}");
        sb.AppendLine("Gemeinsam in denselben E-Mails (Adresse A | Adresse B | Anzahl):");
        foreach (var e in Edges.Where(e => !OwnerAddresses.Contains(e.A) && !OwnerAddresses.Contains(e.B)).Take(maxEdges))
            sb.AppendLine($"- {e.A} | {e.B} | {e.Weight}");
        return sb.ToString();
    }
}
