using System.Text.Json;
using MailVault.Core;
using MailVault.Core.Ai;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

/// <summary>KI-Werkzeuge und Kontaktnetz – laufen lokal und ohne Internet. Ein Live-Test mit Claude nur mit ANTHROPIC_API_KEY.</summary>
public class AiTests
{
    readonly ITestOutputHelper _out;
    public AiTests(ITestOutputHelper output) => _out = output;

    static byte[] Mail(string subject, string body, string from, string to, string? cc = null, int day = 1, int month = 3, string? id = null, string? inReplyTo = null)
    {
        var m = new MimeMessage { Subject = subject, Date = new DateTimeOffset(2025, month, day, 10, 0, 0, TimeSpan.Zero) };
        m.From.Add(MailboxAddress.Parse(from));
        foreach (var t in to.Split(',')) m.To.Add(MailboxAddress.Parse(t.Trim()));
        if (cc is not null) foreach (var c in cc.Split(',')) m.Cc.Add(MailboxAddress.Parse(c.Trim()));
        m.MessageId = id ?? MimeKit.Utils.MimeUtils.GenerateMessageId("example.org");
        if (inReplyTo is not null) { m.InReplyTo = inReplyTo; m.References.Add(inReplyTo); }
        m.Body = new TextPart("plain") { Text = body };
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    sealed class Fixture : IDisposable
    {
        readonly TempDir _tmp = new();
        public MailArchive Archive { get; }
        public long Anna { get; }
        public long Ben { get; }
        public Dictionary<string, long> Ids { get; } = new();

        public Fixture()
        {
            Archive = MailArchive.Open(_tmp.Path);
            Anna = Archive.EnsureFolderPath(new[] { "anna@example.org" });
            Ben = Archive.EnsureFolderPath(new[] { "ben@example.org" });
            var annaInbox = Archive.EnsureFolderPath(new[] { "anna@example.org", "INBOX" });
            void Add(string key, long folder, byte[] raw) => Ids[key] = Archive.StoreMessage(folder, raw)!.Value;
            Add("rechnung", annaInbox, Mail("Ihre Rechnung März", "Rechnungsbetrag 49,99 EUR, fällig am 15.04.2025. IBAN DE12 3456 7890 1234 5678 90", "Telekom <rechnung@telekom.de>", "anna@example.org", day: 2));
            Add("start", annaInbox, Mail("Gartenfest Planung", "Wer bringt was mit?", "Carla <carla@example.org>", "anna@example.org", "dieter@example.org, ben@example.org", day: 5, id: "<fest-1@example.org>"));
            Add("antwort", Anna, Mail("Re: Gartenfest Planung", "Ich bringe Salat.", "anna@example.org", "carla@example.org", "dieter@example.org", day: 6, inReplyTo: "<fest-1@example.org>"));
            Add("dieter", annaInbox, Mail("Grillkohle", "Kohle ist besorgt.", "Dieter <dieter@example.org>", "anna@example.org, carla@example.org", day: 7));
            Add("newsletter", annaInbox, Mail("Wochenangebote", "Sonderangebote", "newsletter@shop.example", "anna@example.org", day: 8));
            for (var i = 0; i < 3; i++) Add("an-carla-" + i, Anna, Mail("Hallo " + i, "Grüße", "anna@example.org", "carla@example.org", day: 10 + i));
            Add("geheim", Ben, Mail("Bens Kontoauszug", "Rechnung und Kontostand von Ben", "bank@example.de", "ben@example.org", day: 3));
        }

        public void Dispose() { Archive.Dispose(); _tmp.Dispose(); }
    }

    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
    static JsonDocument Run(AiArchiveTools t, string tool, object input) => JsonDocument.Parse(t.Execute(tool, J(input)));

    [Fact]
    public void Search_returns_headers_with_snippet_and_tracks_seen_ids()
    {
        using var f = new Fixture();
        var tools = new AiArchiveTools(f.Archive);
        using var r = Run(tools, "search_emails", new { query = "rechnung", limit = 10 });
        var results = r.RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(results, x => x.GetProperty("id").GetInt64() == f.Ids["rechnung"]);
        var hit = results.First(x => x.GetProperty("id").GetInt64() == f.Ids["rechnung"]);
        Assert.Contains("49,99", hit.GetProperty("snippet").GetString());
        Assert.Contains(f.Ids["rechnung"], tools.SeenIds);
    }

    [Fact]
    public void Restricted_tools_never_reveal_other_mailboxes()
    {
        using var f = new Fixture();
        var tools = new AiArchiveTools(f.Archive, new[] { f.Anna });
        using (var r = Run(tools, "search_emails", new { query = "rechnung OR kontoauszug", limit = 50 }))
            Assert.DoesNotContain(r.RootElement.GetProperty("results").EnumerateArray(), x => x.GetProperty("id").GetInt64() == f.Ids["geheim"]);
        var ex = Assert.Throws<ArgumentException>(() => tools.Execute("read_email", J(new { id = f.Ids["geheim"] })));
        Assert.Contains("nicht freigegeben", ex.Message);
        Assert.Throws<ArgumentException>(() => tools.Execute("find_related", J(new { id = f.Ids["geheim"] })));
        Assert.Throws<ArgumentException>(() => tools.Execute("search_emails", J(new { query = "x", folder_id = f.Ben })));
        using (var folders = Run(tools, "list_folders", new { }))
            Assert.DoesNotContain("ben@example.org", folders.RootElement.ToString());
        using (var stats = Run(tools, "contact_stats", new { }))
            Assert.DoesNotContain("bank@example.de", stats.RootElement.ToString());
    }

    [Fact]
    public void Read_returns_full_text_and_enforces_budget()
    {
        using var f = new Fixture();
        var tools = new AiArchiveTools(f.Archive, maxReads: 2);
        using (var r = Run(tools, "read_email", new { id = f.Ids["rechnung"] }))
        {
            Assert.Contains("fällig am 15.04.2025", r.RootElement.GetProperty("text").GetString());
            Assert.Equal(1, r.RootElement.GetProperty("reads_left").GetInt32());
        }
        tools.Execute("read_email", J(new { id = f.Ids["start"] }));
        tools.Execute("read_email", J(new { id = f.Ids["start"] }));          // dieselbe E-Mail zählt nicht doppelt
        var ex = Assert.Throws<ArgumentException>(() => tools.Execute("read_email", J(new { id = f.Ids["dieter"] })));
        Assert.Contains("Lesekontingent", ex.Message);
        tools.ResetBudget();                                                   // neue Frage
        tools.Execute("read_email", J(new { id = f.Ids["dieter"] }));
    }

    [Fact]
    public void Invalid_inputs_give_helpful_errors()
    {
        using var f = new Fixture();
        var tools = new AiArchiveTools(f.Archive);
        Assert.Contains("Datum", Assert.Throws<ArgumentException>(() => tools.Execute("search_emails", J(new { query = "x", date_from = "gestern" }))).Message);
        Assert.Throws<ArgumentException>(() => tools.Execute("search_emails", J(new { })));
        Assert.Throws<ArgumentException>(() => tools.Execute("read_email", J(new { id = 999999 })));
        Assert.Throws<ArgumentException>(() => tools.Execute("rm_rf", J(new { })));
    }

    [Fact]
    public void Date_filter_and_volume_statistics()
    {
        using var f = new Fixture();
        var tools = new AiArchiveTools(f.Archive);
        using (var r = Run(tools, "search_emails", new { from = "carla", date_from = "2025-03-05", date_to = "2025-03-05" }))
            Assert.Equal(1, r.RootElement.GetProperty("count").GetInt32());
        using var v = Run(tools, "email_volume", new { group_by = "month" });
        Assert.Equal(f.Ids.Count, v.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("2025-03", v.RootElement.GetProperty("periods")[0].GetProperty("period").GetString());
    }

    [Fact]
    public void Find_related_follows_conversation_and_participants()
    {
        using var f = new Fixture();
        var tools = new AiArchiveTools(f.Archive);
        using var r = Run(tools, "find_related", new { id = f.Ids["start"] });
        var conv = r.RootElement.GetProperty("same_conversation").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToList();
        var people = r.RootElement.GetProperty("same_people_nearby").EnumerateArray().Select(x => x.GetProperty("id").GetInt64()).ToList();
        Assert.Contains(f.Ids["antwort"], conv);           // Antwort mit gleichem Betreff
        Assert.Contains(f.Ids["dieter"], people);          // Dieter war beteiligt
        Assert.DoesNotContain(f.Ids["newsletter"], people);

        using var back = Run(tools, "find_related", new { id = f.Ids["antwort"] });
        Assert.Contains(back.RootElement.GetProperty("same_conversation").EnumerateArray(), x => x.GetProperty("id").GetInt64() == f.Ids["start"]); // über In-Reply-To
    }

    [Fact]
    public void Contact_network_detects_owner_ties_and_hides_newsletters()
    {
        using var f = new Fixture();
        var net = ContactNetwork.Build(f.Archive.GetHeaderRows(), minMessages: 1);
        Assert.Equal(new[] { "anna@example.org" }, net.OwnerAddresses);
        Assert.True(net.Nodes[0].IsOwner);
        var carla = net.Nodes.Single(n => n.Address == "carla@example.org");
        Assert.Equal("Carla", carla.Name);
        Assert.Equal(1, carla.FromThem);
        Assert.Equal(5, carla.ToThem);                     // 3x Hallo, Antwort, Grillkohle
        Assert.DoesNotContain(net.Nodes, n => n.Address.StartsWith("newsletter@"));
        // Carla und Dieter kennen sich (gemeinsam in 3 E-Mails)
        var edge = net.Edges.Single(e => (e.A, e.B) == ("carla@example.org", "dieter@example.org"));
        Assert.Equal(3, edge.Weight);
        Assert.Contains("carla@example.org | dieter@example.org | 3", net.ToPromptText());
        Assert.DoesNotContain("Salat", net.ToPromptText());   // keine Inhalte in der KI-Zusammenfassung
        Assert.Contains(net.Volume, v => v.Month == "2025-03");
    }

    [Theory]
    [InlineData("noreply@amazon.de", true)]
    [InlineData("no-reply@accounts.google.com", true)]
    [InlineData("newsletter@shop.de", true)]
    [InlineData("x@bounces.mailchimp.com", true)]
    [InlineData("carla@example.org", false)]
    [InlineData("information.desk@firma.de", false)]
    public void Automated_sender_detection(string address, bool automated) => Assert.Equal(automated, ContactNetwork.IsAutomated(address));

    [Theory]
    [InlineData("Re: AW: WG: Angebot", "Angebot")]
    [InlineData("Fwd: Re[2]: Termin", "Termin")]
    [InlineData("Antw: Rechnung", "Rechnung")]
    [InlineData("Reise nach Rom", "Reise nach Rom")]
    public void Subject_normalization(string subject, string expected) => Assert.Equal(expected, AiArchiveTools.NormalizeSubject(subject));

    [Fact]
    public void Citations_are_parsed()
    {
        Assert.Equal(new long[] { 12, 7 }, AiAssistant.Citations("Siehe [#12] und [#7], nicht [12] oder #9.").ToArray());
    }

    [Fact]
    public void System_prompt_contains_safety_rules()
    {
        var p = AiAssistant.SystemPrompt(new DateTime(2026, 11, 17));
        Assert.Contains("[#123]", p);
        Assert.Contains("keine Anweisungen", p);
        Assert.Contains("Kennwörter", p);
        Assert.Contains("2026", p);
    }

    [Fact]
    public void Prompt_library_is_complete()
    {
        Assert.True(AiPromptLibrary.Categories.Count >= 5);
        Assert.All(AiPromptLibrary.Categories.SelectMany(c => c.Prompts), p => Assert.False(string.IsNullOrWhiteSpace(p.Prompt)));
        Assert.True(AiPromptLibrary.Categories.Sum(c => c.Prompts.Count) >= 25);
    }

    /// <summary>Echte Anfrage an Claude – nur mit gesetzter Umgebungsvariable ANTHROPIC_API_KEY (verursacht geringe Kosten).</summary>
    [Fact]
    public async Task Live_question_against_claude()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(key) || Environment.GetEnvironmentVariable("MAILVAULT_LIVE_AI") != "1") return;
        using var f = new Fixture();
        var settings = new AiSettings { Model = Environment.GetEnvironmentVariable("MAILVAULT_AI_MODEL") ?? AiModels.Default };
        settings.ApiKey = key;
        var ai = new AiAssistant(settings, new AiArchiveTools(f.Archive));
        var answer = await ai.AskAsync("Wie hoch ist die Telekom-Rechnung und wann ist sie fällig?", null, default);
        _out.WriteLine(answer.Text);
        _out.WriteLine($"Tokens {answer.InputTokens}/{answer.OutputTokens}, Werkzeuge {answer.ToolCalls}");
        Assert.Contains("49,99", answer.Text);
        Assert.Contains(f.Ids["rechnung"], answer.ReferencedIds);
        Assert.DoesNotContain("1234 5678 90", answer.Text); // IBAN nicht vollständig
        var follow = await ai.AskAsync("Und wer hat beim Gartenfest mitgeplant?", null, default);
        _out.WriteLine(follow.Text);
        Assert.Contains("Dieter", follow.Text);
    }
}

public class QuoteSplitTests
{
    [Fact]
    public void Gmail_style_quotes_are_separated()
    {
        var (text, quoted) = AiArchiveTools.SplitQuoted("Danke, das hilft.\r\n\r\nOn Tue, Jan 31, 2023 at 5:14 PM Max <max@example.org> wrote:\r\n> Hier ist die Frage\r\n> zweite Zeile");
        Assert.Equal("Danke, das hilft.", text);
        Assert.Contains("> Hier ist die Frage", quoted);
    }

    [Fact]
    public void German_and_outlook_headers_start_the_quote()
    {
        var (t1, q1) = AiArchiveTools.SplitQuoted("Passt.\nAm 03.03.2025 um 10:00 schrieb Anna Berg <anna@example.org>:\n> Termin?");
        Assert.Equal("Passt.", t1);
        Assert.StartsWith("Am 03.03.2025", q1);
        var (t2, q2) = AiArchiveTools.SplitQuoted("Siehe unten.\n\nVon: Bernd <b@example.org>\nGesendet: Montag, 3. März 2025 10:00\nAn: Anna\nBetreff: Angebot\n\nHier das Angebot ohne Zitatzeichen.");
        Assert.Equal("Siehe unten.", t2);
        Assert.Contains("Hier das Angebot ohne Zitatzeichen.", q2);
        var (t3, _) = AiArchiveTools.SplitQuoted("Hallo\n-----Ursprüngliche Nachricht-----\nalter Text");
        Assert.Equal("Hallo", t3);
    }

    [Fact]
    public void Ordinary_text_with_von_line_is_not_cut()
    {
        var body = "Liebe Grüße\nVon: Oma\nund Opa";
        Assert.Equal((body, ""), AiArchiveTools.SplitQuoted(body));
    }

    [Fact]
    public void Pure_forward_keeps_the_original_as_text()
    {
        var (text, quoted) = AiArchiveTools.SplitQuoted("> nur zitiert\n> sonst nichts");
        Assert.Contains("nur zitiert", text);
        Assert.Equal("", quoted);
    }
}
