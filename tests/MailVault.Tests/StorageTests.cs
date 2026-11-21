using System.IO.Compression;
using System.Text;
using MailVault.Core;
using Microsoft.Data.Sqlite;
using MimeKit;
using Xunit;

namespace MailVault.Tests;

public class StorageTests
{
    static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    static byte[] WithAttachment(string subject, byte[] attachment, string name = "anhang.pdf")
    {
        var m = new MimeMessage { Subject = subject, Date = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero) };
        m.From.Add(MailboxAddress.Parse("a@example.org"));
        m.To.Add(MailboxAddress.Parse("b@example.org"));
        m.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("example.org");
        var bb = new BodyBuilder { TextBody = "Siehe Anhang: " + subject };
        bb.Attachments.Add(name, attachment);
        m.Body = bb.ToMessageBody();
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    /// <summary>Handgeschriebene Nachricht mit frei wählbarer base64-Form (Zeilenlänge, Zeilenende, Störungen).</summary>
    static byte[] Handmade(string subject, byte[] attachment, int lineLength, string eol, string trailer = "", Func<string, string>? mangle = null)
    {
        var b64 = Convert.ToBase64String(attachment);
        var lines = Enumerable.Range(0, (b64.Length + lineLength - 1) / lineLength).Select(i => b64.Substring(i * lineLength, Math.Min(lineLength, b64.Length - i * lineLength)));
        var body = string.Join(eol, lines);
        if (mangle is not null) body = mangle(body);
        var text = string.Join(eol,
            "From: x@example.org", "To: y@example.org", $"Subject: {subject}", $"Message-ID: <{subject}@example.org>",
            "Date: Mon, 3 Mar 2025 10:00:00 +0000", "MIME-Version: 1.0", "Content-Type: multipart/mixed; boundary=\"BB\"", "",
            "--BB", "Content-Type: text/plain; charset=utf-8", "", "Hallo", "--BB",
            "Content-Type: application/octet-stream; name=\"d.bin\"", "Content-Transfer-Encoding: base64", "", body + trailer,
            "--BB--", "");
        return Encoding.ASCII.GetBytes(text);
    }

    [Fact]
    public void Messages_round_trip_byte_identical_in_all_variants()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var f = archive.EnsureFolderPath(new[] { "T" });
        var data = Bytes(20_000, 1);
        var variants = new List<byte[]>
        {
            WithAttachment("standard", data),
            Handmade("crlf76", data, 76, "\r\n"),
            Handmade("lf76", data, 76, "\n"),
            Handmade("crlf72", data, 72, "\r\n"),
            Handmade("trailing", data, 76, "\r\n", "\r\n\r\n"),
            Handmade("spaces", data, 76, "\r\n", mangle: b => b.Replace("\r\n", " \r\n")),           // nicht kanonisch -> bleibt im Skelett
            Handmade("broken", data, 76, "\r\n", mangle: b => b[..500] + "!!" + b[500..]),          // ungültig -> bleibt im Skelett
            Handmade("short", data, 76, "\r\n", mangle: b => b.Remove(1000, 3)),                    // Zeilenlänge unregelmäßig
            Encoding.UTF8.GetBytes("Subject: kein MIME\r\n\r\nNur Text mit Umlauten äöü\r\n"),
        };
        foreach (var raw in variants)
        {
            var id = archive.StoreMessage(f, raw);
            Assert.NotNull(id);
            Assert.Equal(raw, archive.GetRawMessage(id!.Value));
        }
    }

    [Fact]
    public void Identical_attachments_are_stored_once_and_released_on_delete()
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var f = archive.EnsureFolderPath(new[] { "T" });
        var pdf = Bytes(100_000, 2);
        var a = archive.StoreMessage(f, WithAttachment("eins", pdf))!.Value;
        var b = archive.StoreMessage(f, WithAttachment("zwei", pdf))!.Value;
        var c = archive.StoreMessage(f, WithAttachment("drei", Bytes(50_000, 3)))!.Value;

        var parts = archive.StorageBreakdown()["parts"];
        Assert.InRange(parts, 150_000, 160_000); // 100 KB + 50 KB, nicht 250 KB (zufällige Bytes: unkomprimiert)

        archive.DeleteMessages(new[] { a });
        Assert.Equal(WithAttachmentBody(archive, b), pdf);    // geteilter Teil bleibt erhalten
        archive.DeleteMessages(new[] { b });
        Assert.InRange(archive.StorageBreakdown()["parts"], 50_000, 55_000); // jetzt freigegeben
        Assert.NotEmpty(archive.GetRawMessage(c));
    }

    static byte[] WithAttachmentBody(MailArchive archive, long id)
    {
        var part = (MimePart)archive.LoadMessage(id).Attachments.Single();
        using var ms = new MemoryStream();
        part.Content.DecodeTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Dictionary_is_trained_and_all_messages_stay_readable()
    {
        var old = MailArchive.DictionaryTrainThreshold;
        MailArchive.DictionaryTrainThreshold = 60;
        try
        {
            using var tmp = new TempDir();
            using var archive = MailArchive.Open(tmp.Path);
            var f = archive.EnsureFolderPath(new[] { "T" });
            var raws = new Dictionary<long, byte[]>();
            for (var i = 0; i < 120; i++)
            {
                var raw = ArchiveTests.MakeMessage($"Newsletter Ausgabe {i}", $"Liebe Leserinnen und Leser, hier ist Ausgabe {i} mit vielen Neuigkeiten. " + string.Join(" ", Enumerable.Range(0, 400).Select(k => (k * 7 + i) % 13 == 0 ? "Sonderangebot" : "Woche" + (k % 50))));
                raws[archive.StoreMessage(f, raw)!.Value] = raw;
            }
            Assert.True(archive.StorageBreakdown()["dicts"] > 0, "Wörterbuch sollte gelernt sein");
            foreach (var (id, raw) in raws) Assert.Equal(raw, archive.GetRawMessage(id));
            archive.Compact();
            foreach (var (id, raw) in raws) Assert.Equal(raw, archive.GetRawMessage(id));
            Assert.Single(archive.Search(new SearchCriteria { Query = "\"Ausgabe 77\"" }));
        }
        finally { MailArchive.DictionaryTrainThreshold = old; }
    }

    [Fact]
    public void Archive_from_version_1_is_upgraded()
    {
        using var tmp = new TempDir();
        var raw = WithAttachment("Altbestand Rechnung", Bytes(30_000, 4));
        // Archiv im Format von Version 1 anlegen: FTS mit Textkopie, Nachricht als Brotli-Block
        SQLitePCL.Batteries_V2.Init();
        using (var cn = new SqliteConnection($"Data Source={Path.Combine(tmp.Path, MailArchive.DbFileName)};Pooling=false"))
        {
            cn.Open();
            var cmd = cn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO meta VALUES ('schema_version', '1');
                CREATE TABLE folders(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES folders(id) ON DELETE CASCADE, name TEXT NOT NULL);
                CREATE TABLE messages(id INTEGER PRIMARY KEY, folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                    message_id TEXT, header_hash TEXT NOT NULL, date_utc INTEGER NOT NULL, arc_date_utc INTEGER NOT NULL, size INTEGER NOT NULL,
                    priority INTEGER NOT NULL DEFAULT 3, has_attachments INTEGER NOT NULL DEFAULT 0, from_addr TEXT NOT NULL DEFAULT '',
                    to_addr TEXT NOT NULL DEFAULT '', cc_addr TEXT NOT NULL DEFAULT '', subject TEXT NOT NULL DEFAULT '');
                CREATE UNIQUE INDEX ux_messages_hash ON messages(header_hash);
                CREATE TABLE blobs(message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE, data BLOB NOT NULL);
                CREATE VIRTUAL TABLE fts USING fts5(subject, fromto, body, att_names, att_content, tokenize = 'unicode61 remove_diacritics 2');
                CREATE TRIGGER trg_messages_delete AFTER DELETE ON messages BEGIN DELETE FROM fts WHERE rowid = old.id; END;
                INSERT INTO folders VALUES (1, NULL, 'Alt');
                INSERT INTO messages(id, folder_id, header_hash, date_utc, arc_date_utc, size, subject) VALUES (1, 1, 'h1', 1740823200, 1740823200, $s, 'Altbestand Rechnung');
                INSERT INTO blobs VALUES (1, $b);
                INSERT INTO fts(rowid, subject, fromto, body, att_names, att_content) VALUES (1, 'Altbestand Rechnung', '', '', '', '');
                """;
            cmd.Parameters.AddWithValue("$s", raw.Length);
            using var ms = new MemoryStream();
            using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) br.Write(raw);
            cmd.Parameters.AddWithValue("$b", ms.ToArray());
            cmd.ExecuteNonQuery();
        }

        using var archive = MailArchive.Open(tmp.Path);
        Assert.Equal(raw, archive.GetRawMessage(1));                                    // altes Format lesbar
        Assert.Single(archive.Search(new SearchCriteria { Query = "Siehe Anhang" }));     // Index wurde neu aufgebaut
        Assert.False(archive.StorageBreakdown().ContainsKey("fts_content"));              // keine Textkopie mehr
        archive.Compact();                                                                // wandelt ins neue Format um
        Assert.Equal(raw, archive.GetRawMessage(1));
        Assert.True(archive.StorageBreakdown()["parts"] > 0);
    }

    [Fact]
    public void Protection_keeps_attachments_dictionary_and_index()
    {
        var old = MailArchive.DictionaryTrainThreshold;
        MailArchive.DictionaryTrainThreshold = 60;
        try
        {
            using var tmp = new TempDir();
            var raws = new Dictionary<long, byte[]>();
            using (var archive = MailArchive.Open(tmp.Path))
            {
                var f = archive.EnsureFolderPath(new[] { "T" });
                var shared = Bytes(40_000, 5);
                for (var i = 0; i < 80; i++)
                {
                    var raw = WithAttachment($"Vertrag {i}", i % 2 == 0 ? shared : Bytes(5_000, 100 + i));
                    raws[archive.StoreMessage(f, raw)!.Value] = raw;
                }
                archive.EnableProtection("geheim123");
                foreach (var (id, raw) in raws) Assert.Equal(raw, archive.GetRawMessage(id));
            }
            var header = File.ReadAllBytes(Path.Combine(tmp.Path, MailArchive.DbFileName)).Take(15).ToArray();
            Assert.NotEqual("SQLite format 3", Encoding.ASCII.GetString(header));

            using (var archive = MailArchive.Open(tmp.Path, "geheim123"))
            {
                foreach (var (id, raw) in raws) Assert.Equal(raw, archive.GetRawMessage(id));
                Assert.Single(archive.Search(new SearchCriteria { Query = "\"Vertrag 42\"" }));
                archive.DisableProtection("geheim123");
                foreach (var (id, raw) in raws) Assert.Equal(raw, archive.GetRawMessage(id));
                Assert.Single(archive.Search(new SearchCriteria { Query = "\"Vertrag 42\"" }));
            }
        }
        finally { MailArchive.DictionaryTrainThreshold = old; }
    }
}
