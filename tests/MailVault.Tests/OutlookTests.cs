using MailVault.Core;
using MailVault.Core.Sources;
using MimeKit;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

public class OutlookTests
{
    readonly ITestOutputHelper _out;
    public OutlookTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Samples()
    {
        var dirs = new[] { Path.Combine(AppContext.BaseDirectory, "TestData"), Environment.GetEnvironmentVariable("MAILVAULT_PST_SAMPLES") };
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d).Where(f => f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)))
                yield return new object[] { f };
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Outlook_data_file_imports_and_deduplicates(string file)
    {
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        await using (var src = new OutlookPstSource(file))
        {
            await src.ConnectAsync(default);
            foreach (var f in await src.ListFoldersAsync(default)) _out.WriteLine("Ordner: " + string.Join("/", f));
        }

        var profile = new ArchiveProfile { Type = ProfileType.OutlookPst, Path = file, ExcludeSpecialFolders = false };
        var r1 = await ImportJob.RunAsync(archive, profile, new Progress<ProgressInfo>(p => { if (p.LogLine is { } l) _out.WriteLine(l); }), default);
        _out.WriteLine("1. Lauf: " + r1);
        Assert.Equal(0, r1.Errors);
        if (Path.GetFileName(file) == "dist-list.pst") { Assert.Equal(0, r1.Archived); return; } // enthält nur eine Verteilerliste
        Assert.True(r1.Archived > 0, "Es sollte mindestens eine E-Mail archiviert werden.");
        Assert.DoesNotContain(archive.GetFolderTree().SelectMany(Flatten), f => f.Name is "IPM_SUBTREE" or "Root - Mailbox" || f.Name.Contains("This computer only"));

        foreach (var m in archive.GetFolderTree().SelectMany(f => archive.GetMessages(f.Id, true)).Take(15))
        {
            var mime = archive.LoadMessage(m.Id);
            _out.WriteLine($"{m.FolderPath} | {m.DateUtc:u} | {m.From} | {m.Subject} | Anh:{m.HasAttachments} | Text:{(mime.TextBody ?? mime.HtmlBody ?? "").Length}");
        }

        var r2 = await ImportJob.RunAsync(archive, profile, null, default);
        Assert.Equal(0, r2.Archived);
        Assert.Equal(r1.Archived, r2.AlreadyArchived);
    }

    static IEnumerable<ArchiveFolder> Flatten(ArchiveFolder f) => new[] { f }.Concat(f.Children.SelectMany(Flatten));

    [Fact]
    public void Rtf_bodies_are_converted_to_text()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0 Calibri;}}{\*\generator Riched20;}\f0 Hallo M\'fcller,\par anbei die \b Rechnung\b0 .\par " + "\\u8364? 12\\par}";
        var text = OutlookMessageConverter.RtfToText(rtf);
        Assert.Equal("Hallo Müller,\nanbei die Rechnung.\n€ 12", text.Replace("\r", ""));
    }
}
