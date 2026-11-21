using System.Security.Cryptography;
using MailVault.Core;
using MailVault.Core.Sources;
using MimeKit;
using Xunit;

namespace MailVault.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailvault-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class ArchiveTests
{
    internal static byte[] MakeMessage(string subject, string body, string from = "Alice <alice@example.org>",
        string to = "bob@example.org", DateTimeOffset? date = null, (string Name, string Text)? attachment = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Date = date ?? new DateTimeOffset(2024, 5, 17, 10, 30, 0, TimeSpan.FromHours(2));
        msg.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("example.org");
        var builder = new BodyBuilder { TextBody = body };
        if (attachment is { } a) builder.Attachments.Add(a.Name, System.Text.Encoding.UTF8.GetBytes(a.Text), new ContentType("text", "plain"));
        msg.Body = builder.ToMessageBody();
        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Store_deduplicates_and_builds_folder_tree()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var inbox = archive.EnsureFolderPath(new[] { "alice@example.org", "INBOX" });
        var sub = archive.EnsureFolderPath(new[] { "alice@example.org", "INBOX", "Projekte" });
        Assert.Equal(inbox, archive.EnsureFolderPath(new[] { "alice@example.org", "inbox" }));

        var m1 = MakeMessage("Angebot Fenster", "Hallo Bob, anbei das Angebot.");
        Assert.NotNull(archive.StoreMessage(inbox, m1));
        Assert.Null(archive.StoreMessage(sub, m1)); // Duplikat
        Assert.NotNull(archive.StoreMessage(sub, MakeMessage("Projektplan", "Meilensteine Q3")));

        var tree = archive.GetFolderTree();
        var root = Assert.Single(tree);
        Assert.Equal(2, root.TotalCount);
        Assert.Equal("alice@example.org/INBOX/Projekte", archive.GetFolderPath(sub));
        Assert.Equal(2, archive.GetMessages(inbox, recursive: true).Count);
        Assert.Single(archive.GetMessages(inbox));
    }

    [Fact]
    public void Search_finds_subject_body_attachment_and_umlauts()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var f = archive.EnsureFolderPath(new[] { "Test" });
        archive.StoreMessage(f, MakeMessage("Rechnung März", "Bitte überweisen Sie den Betrag."));
        archive.StoreMessage(f, MakeMessage("Urlaub", "Fotos folgen", attachment: ("liste.txt", "Sonnencreme Badehose Reiseführer")));
        archive.StoreMessage(f, MakeMessage("Newsletter", "Neuigkeiten", from: "news@shop.example"));

        Assert.Single(archive.Search(new SearchCriteria { Query = "rechnung" }));
        Assert.Single(archive.Search(new SearchCriteria { Query = "marz" }));          // Diakritika werden ignoriert
        Assert.Single(archive.Search(new SearchCriteria { Query = "überw*" }));        // Präfix
        Assert.Single(archive.Search(new SearchCriteria { Query = "\"den Betrag\"" })); // Phrase
        Assert.Empty(archive.Search(new SearchCriteria { Query = "badehose" }));       // Anhanginhalte standardmäßig aus
        Assert.Single(archive.Search(new SearchCriteria { Query = "badehose", InAttachmentContents = true }));
        Assert.Single(archive.Search(new SearchCriteria { Query = "liste" }));         // Anhangname
        Assert.Equal(2, archive.Search(new SearchCriteria { Query = "rechnung OR urlaub" }).Count);
        Assert.Single(archive.Search(new SearchCriteria { From = "shop.example" }));
        Assert.Equal(2, archive.Search(new SearchCriteria { HasAttachments = false }).Count);
        Assert.Empty(archive.Search(new SearchCriteria { Query = "rechnung", InSubject = false, InBody = false, InFromToCcBcc = false, InAttachmentNames = false }));
    }

    [Fact]
    public void Search_html_body_with_prefix_and_without_umlauts()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var f = archive.EnsureFolderPath(new[] { "Test" });
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse("anna@example.org"));
        msg.Subject = "Angebot Wintergarten";
        msg.Body = new TextPart("html") { Text = "<p>Grüße aus <b>Köln</b>!</p>" };
        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        archive.StoreMessage(f, ms.ToArray());

        Assert.Single(archive.Search(new SearchCriteria { Query = "koln winter" }));
        Assert.Single(archive.Search(new SearchCriteria { Query = "GRÜßE" }));
        Assert.Empty(archive.Search(new SearchCriteria { Query = "\"winter\"" })); // Phrase bleibt exakt
    }

    [Fact]
    public void Date_ranges_resolve_relative_to_now()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0); // Sonntag
        var (from, to) = MailArchive.ResolveDateRange(new SearchCriteria { DateRange = DateRangeType.ThisWeek }, now);
        Assert.Equal(new DateTime(2026, 11, 9), from);
        Assert.Equal(new DateTime(2026, 11, 16), to);
        (from, to) = MailArchive.ResolveDateRange(new SearchCriteria { DateRange = DateRangeType.LastMonth }, now);
        Assert.Equal(new DateTime(2026, 10, 1), from);
        Assert.Equal(new DateTime(2026, 11, 1), to);
    }

    [Fact]
    public async Task Export_mbox_and_reimport_detects_duplicates()
    {
        using var tmp = new TempDir();
        var dataA = Path.Combine(tmp.Path, "a");
        var dataB = Path.Combine(tmp.Path, "b");
        var mbox = Path.Combine(tmp.Path, "export", "all.mbox");
        using (var a = MailArchive.Open(dataA))
        {
            var f = a.EnsureFolderPath(new[] { "Quelle" });
            a.StoreMessage(f, MakeMessage("Eins", "From the start\n>From quoted\nEnde"));
            a.StoreMessage(f, MakeMessage("Zwei", "Text"));
            var n = await Exporter.ExportAsync(a, a.GetMessageIds(f, true), mbox, ExportFormat.Mbox, false, null, default);
            Assert.Equal(2, n);
        }

        using var b = MailArchive.Open(dataB);
        var profile = new ArchiveProfile { Type = ProfileType.Mbox, Path = mbox, Name = "Test" };
        var r1 = await ImportJob.RunAsync(b, profile, null, default);
        Assert.Equal(2, r1.Archived);
        var r2 = await ImportJob.RunAsync(b, profile, null, default);
        Assert.Equal(0, r2.Archived);
        Assert.Equal(2, r2.AlreadyArchived);

        var eins = Assert.Single(b.Search(new SearchCriteria { Query = "Eins" }));
        var body = b.LoadMessage(eins.Id).TextBody;
        Assert.Contains("From the start", body);   // mboxrd-Maskierung wurde beim Lesen zurückgenommen
        Assert.Contains(">From quoted", body);
    }

    [Fact]
    public async Task Eml_folder_import_keeps_structure_and_filters()
    {
        using var tmp = new TempDir();
        var src = Path.Combine(tmp.Path, "src");
        Directory.CreateDirectory(Path.Combine(src, "Kunden"));
        Directory.CreateDirectory(Path.Combine(src, "Papierkorb"));
        File.WriteAllBytes(Path.Combine(src, "a.eml"), MakeMessage("A", "a", date: DateTimeOffset.Now.AddDays(-1)));
        File.WriteAllBytes(Path.Combine(src, "Kunden", "b.eml"), MakeMessage("B", "b", date: DateTimeOffset.Now.AddDays(-2)));
        File.WriteAllBytes(Path.Combine(src, "Kunden", "alt.eml"), MakeMessage("Alt", "c", date: DateTimeOffset.Now.AddDays(-400)));
        File.WriteAllBytes(Path.Combine(src, "Papierkorb", "x.eml"), MakeMessage("X", "x"));

        using var archive = MailArchive.Open(Path.Combine(tmp.Path, "data"));
        var profile = new ArchiveProfile { Type = ProfileType.EmlFolder, Path = src, TargetFolder = "Import" };
        var r = await ImportJob.RunAsync(archive, profile, null, default);
        Assert.Equal(3, r.Archived); // Papierkorb wird übersprungen
        Assert.Single(archive.Search(new SearchCriteria { Query = "\"B\"" }), m => m.FolderPath == "Import/Kunden");

        using var archive2 = MailArchive.Open(Path.Combine(tmp.Path, "data2"));
        profile.OnlyNewerThanDays = 30;
        var r2 = await ImportJob.RunAsync(archive2, profile, null, default);
        Assert.Equal(2, r2.Archived);
        Assert.Equal(1, r2.Filtered); // Datumsfilter greift auch ohne vorab bekanntes Datum
    }

    [Fact]
    public void Protection_encrypts_and_supports_password_change_and_recovery()
    {
        using var tmp = new TempDir();
        string recovery;
        using (var archive = MailArchive.Open(tmp.Path))
        {
            var f = archive.EnsureFolderPath(new[] { "Geheim" });
            archive.StoreMessage(f, MakeMessage("Vertraulich", "Kontonummer 1234"));
            recovery = archive.EnableProtection("erstes-Kennwort");
            Assert.True(archive.IsProtected);
            Assert.Single(archive.Search(new SearchCriteria { Query = "kontonummer" }));
        }

        Assert.True(MailArchive.IsProtectedArchive(tmp.Path));
        var dbBytes = File.ReadAllBytes(Path.Combine(tmp.Path, MailArchive.DbFileName));
        Assert.DoesNotContain("SQLite format 3", System.Text.Encoding.ASCII.GetString(dbBytes, 0, 16));
        Assert.Throws<CryptographicException>(() => MailArchive.Open(tmp.Path, "falsch"));

        using (var archive = MailArchive.Open(tmp.Path, "erstes-Kennwort"))
        {
            Assert.Single(archive.Search(new SearchCriteria { Query = "Vertraulich" }));
            archive.ChangePassword("erstes-Kennwort", "zweites");
        }
        using (var archive = MailArchive.OpenWithRecoveryKey(tmp.Path, recovery.ToLowerInvariant(), "drittes"))
            Assert.Equal(1, archive.GetStats().MessageCount);
        using (var archive = MailArchive.Open(tmp.Path, "drittes"))
            archive.DisableProtection("drittes");

        Assert.False(MailArchive.IsProtectedArchive(tmp.Path));
        using (var archive = MailArchive.Open(tmp.Path))
            Assert.Single(archive.Search(new SearchCriteria { Query = "Kontonummer" }));
    }

    [Fact]
    public void Delete_folder_removes_messages_and_index()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var f = archive.EnsureFolderPath(new[] { "X", "Y" });
        archive.StoreMessage(f, MakeMessage("Weg damit", "loeschen"));
        Assert.Equal(1, archive.DeleteFolder(archive.EnsureFolderPath(new[] { "X" })));
        Assert.Empty(archive.Search(new SearchCriteria { Query = "loeschen" }));
        Assert.Empty(archive.GetFolderTree());
    }

    [Fact]
    public void Folder_filter_respects_include_exclude_and_special_folders()
    {
        var p = new ArchiveProfile { IncludeFolders = { "INBOX" }, ExcludeFolders = { "INBOX/Privat" } };
        Assert.True(ImportJob.FolderFilter(p, new[] { "INBOX" }));
        Assert.True(ImportJob.FolderFilter(p, new[] { "INBOX", "Arbeit" }));
        Assert.False(ImportJob.FolderFilter(p, new[] { "INBOX", "Privat", "Fotos" }));
        Assert.False(ImportJob.FolderFilter(p, new[] { "Sent" }));
        Assert.False(ImportJob.FolderFilter(new ArchiveProfile(), new[] { "Trash" }));
        Assert.True(ImportJob.FolderFilter(new ArchiveProfile { ExcludeSpecialFolders = false }, new[] { "Trash" }));
    }

    [Fact]
    public void Query_builder_handles_terms_phrases_or_and_negation()
    {
        var cols = new[] { "subject", "body" };
        Assert.Equal("{subject body} : (\"a\"* AND \"b\"*)", SearchQueryBuilder.BuildFtsQuery("a b*", cols));
        Assert.Equal("{subject body} : ((\"a\"* OR \"b\"*))", SearchQueryBuilder.BuildFtsQuery("a OR b", cols));
        Assert.Equal("{subject body} : (\"x y\") NOT {subject body} : \"z\"", SearchQueryBuilder.BuildFtsQuery("\"x y\" -z", cols));
        Assert.Null(SearchQueryBuilder.BuildFtsQuery("-nur", cols));
    }
}
