using System.Diagnostics;
using MailVault.Core;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

/// <summary>
/// Misst den Speicherbedarf des Archivs. Läuft nur, wenn MAILVAULT_BENCH_CORPUS auf einen Ordner mit .mbox-Dateien zeigt.
/// Zusätzlich wird ein reproduzierbarer "Büro-Mix" erzeugt (HTML mit Signaturlogos, PDF-Antwortketten, weitergeleitete Excel-Datei).
/// </summary>
public class StorageBenchmark
{
    readonly ITestOutputHelper _out;
    public StorageBenchmark(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Measure_storage()
    {
        var corpus = Environment.GetEnvironmentVariable("MAILVAULT_BENCH_CORPUS");
        if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus)) return; // nur auf Anforderung

        if (int.TryParse(Environment.GetEnvironmentVariable("MAILVAULT_BENCH_QUALITY"), out var q)) MessageStorage.SkeletonLevel = q;
        _out.WriteLine($"zstd-Stufe {MessageStorage.SkeletonLevel}");
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var sw = Stopwatch.StartNew();
        long rawMbox = 0, rawOffice = 0;

        foreach (var mbox in Directory.EnumerateFiles(corpus, "*.mbox"))
        {
            rawMbox += new FileInfo(mbox).Length;
            var r = await ImportJob.RunAsync(archive, new ArchiveProfile { Type = ProfileType.Mbox, Path = mbox, ExcludeSpecialFolders = false }, null, default);
            Assert.Equal(0, r.Errors);
        }
        var t1 = sw.Elapsed;
        archive.Compact();
        var afterMbox = archive.GetStats().DatabaseFileBytes;
        _out.WriteLine($"Nur Mailinglisten: {afterMbox / 1024.0:N0} KB = {100.0 * afterMbox / rawMbox:N1} %, {t1.TotalSeconds:N1} s");
        foreach (var (t, b) in archive.StorageBreakdown().OrderByDescending(kv => kv.Value).Take(5)) _out.WriteLine($"    {t,-20} {b / 1024.0,8:N0} KB");
        _out.WriteLine($"    page_count*page_size: {archive.PageStats()}");
        sw.Restart();
        var folder = archive.EnsureFolderPath(new[] { "Büro" });
        var originals = new List<(byte[] Raw, long Id)>();
        foreach (var msg in OfficeMix())
        {
            rawOffice += msg.Length;
            var id = archive.StoreMessage(folder, msg);
            if (id is { } i) originals.Add((msg, i));
        }
        var importTime = sw.Elapsed;
        archive.Compact();
        var stats = archive.GetStats();
        var raw = rawMbox + rawOffice;
        _out.WriteLine($"Nachrichten: {stats.MessageCount}, Rohdaten: {raw / 1024.0:N0} KB (Mailinglisten {rawMbox / 1024.0:N0} KB, Büro-Mix {rawOffice / 1024.0:N0} KB)");
        _out.WriteLine($"Archivdatei: {stats.DatabaseFileBytes / 1024.0:N0} KB = {100.0 * stats.DatabaseFileBytes / raw:N1} % der Rohdaten, Import {importTime.TotalSeconds:N1} s");
        foreach (var (t, b) in archive.StorageBreakdown().OrderByDescending(kv => kv.Value).Take(8))
            _out.WriteLine($"  {t,-22} {b / 1024.0,10:N0} KB");

        // Jede Nachricht muss byte-identisch zurückkommen.
        foreach (var (rawMsg, id) in originals)
            Assert.Equal(rawMsg, archive.GetRawMessage(id));
    }

    /// <summary>Reproduzierbarer Büro-Mix mit typischen Wiederholungen.</summary>
    internal static IEnumerable<byte[]> OfficeMix()
    {
        var rnd = new Random(42);
        byte[] Rand(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        var logos = Enumerable.Range(0, 3).Select(_ => Rand(14_000)).ToArray(); // PNG/JPEG: nicht weiter komprimierbar
        var pdfText = string.Concat(Enumerable.Range(0, 1800).Select(i => $"BT /F1 12 Tf 72 {700 - i % 50} Td (Zeile {i}) Tj ET\n"));
        var pdf = Rand(90_000).Concat(System.Text.Encoding.ASCII.GetBytes(pdfText)).ToArray();
        var xlsx = Rand(45_000);
        var senders = new[] { "Müller GmbH <info@mueller.example>", "Steuerbüro Klein <kanzlei@klein.example>", "Shop <news@shop.example>" };
        var words = "Angebot Rechnung Termin Projekt Lieferung Besprechung Freigabe Anlage Rückfrage Bestellung Vertrag Woche Montag Kunde".Split(' ');
        string Text(int n) => string.Join(" ", Enumerable.Range(0, n).Select(_ => words[rnd.Next(words.Length)]));
        var date = new DateTimeOffset(2025, 1, 6, 9, 0, 0, TimeSpan.FromHours(1));

        static byte[] Build(MimeMessage m) { using var ms = new MemoryStream(); m.WriteTo(ms); return ms.ToArray(); }
        MimeMessage New(string from, string subject)
        {
            date = date.AddMinutes(rnd.Next(30, 600));
            var m = new MimeMessage { Subject = subject, Date = date };
            m.From.Add(MailboxAddress.Parse(from));
            m.To.Add(MailboxAddress.Parse("Max Mustermann <max@example.de>"));
            m.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("bench.example");
            return m;
        }

        for (var i = 0; i < 150; i++) // HTML-Mails mit Signaturlogo
        {
            var s = i % 3;
            var m = New(senders[s], "Info " + Text(4));
            var bb = new BodyBuilder();
            var logo = bb.LinkedResources.Add($"logo{s}.png", logos[s], new ContentType("image", "png"));
            logo.ContentId = $"logo{s}@x";
            var paras = string.Concat(Enumerable.Range(0, 8).Select(_ => $"<p style=\"font-family:Arial;font-size:14px;color:#333\">{Text(40)}</p>"));
            bb.HtmlBody = $"<html><body><table width=\"600\"><tr><td>{paras}</td></tr></table><img src=\"cid:{logo.ContentId}\"><p>Mit freundlichen Grüßen<br>{senders[s]}</p></body></html>";
            bb.TextBody = Text(320);
            m.Body = bb.ToMessageBody();
            yield return Build(m);
        }
        for (var t = 0; t < 20; t++) // Antwortketten, jede Antwort trägt dieselbe PDF erneut mit
        {
            var quoted = "";
            for (var k = 0; k < 4; k++)
            {
                var m = New(k % 2 == 0 ? senders[1] : "Max Mustermann <max@example.de>", (k == 0 ? "" : "AW: ") + $"Jahresabschluss {t}");
                var text = Text(60) + "\n\n" + quoted;
                quoted = string.Join("\n", text.Split('\n').Select(l => "> " + l));
                var bb = new BodyBuilder { TextBody = text };
                bb.Attachments.Add($"Abschluss-{t}.pdf", pdf, new ContentType("application", "pdf"));
                m.Body = bb.ToMessageBody();
                yield return Build(m);
            }
        }
        for (var i = 0; i < 30; i++) // dieselbe Excel-Datei mehrfach weitergeleitet
        {
            var m = New(senders[0], "WG: Preisliste " + i);
            var bb = new BodyBuilder { TextBody = Text(50) };
            bb.Attachments.Add("Preisliste.xlsx", xlsx);
            m.Body = bb.ToMessageBody();
            yield return Build(m);
        }
    }
}
