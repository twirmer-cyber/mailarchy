using System.Security.Cryptography;
using MailVault.Core;
using Xunit;

namespace MailVault.Tests;

/// <summary>
/// Gesichertes Archiv einbinden: übernehmen mit Ordnern und Duplikaterkennung, mit Kennwort, Wiederherstellungsschlüssel
/// oder Notfallblatt – und die Sicherung bleibt dabei Byte für Byte unverändert.
/// </summary>
public class ArchiveImportTests
{
    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    static byte[] Msg(string subject, int day) => ArchiveTests.MakeMessage(subject, "Text zu " + subject,
        date: new DateTimeOffset(2024, 3, day, 10, 0, 0, TimeSpan.Zero));

    /// <summary>Dieselbe Nachricht (gleiche Bytes) in Sicherung und aktuellem Archiv.</summary>
    static readonly byte[] Shared = Msg("Gemeinsam", 3);

    /// <summary>Sicherung: anna@x/INBOX (3), Alt-PC/Outlook/Projekte (2); liefert ggf. den Wiederherstellungsschlüssel.</summary>
    static string? CreateBackup(string dir, string? password = null)
    {
        using var a = MailArchive.Open(dir);
        var inbox = a.EnsureFolderPath(new[] { "anna@example.org", "INBOX" });
        var proj = a.EnsureFolderPath(new[] { "Alt-PC", "Outlook", "Projekte" });
        a.StoreMessage(inbox, Msg("Rechnung März", 1));
        a.StoreMessage(inbox, Msg("Elternabend", 2));
        a.StoreMessage(inbox, Shared);
        a.StoreMessage(proj, Msg("Projektplan", 4));
        a.StoreMessage(proj, Msg("Statusbericht", 5));
        return password is null ? null : a.EnableProtection(password);
    }

    static Dictionary<string, string> Snapshot(string dir) =>
        Directory.GetFiles(dir).ToDictionary(Path.GetFileName, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))!;

    static List<string> Paths(MailArchive a)
    {
        var result = new List<string>();
        void Walk(ArchiveFolder f, string prefix)
        {
            var path = prefix.Length == 0 ? f.Name : prefix + "/" + f.Name;
            if (f.MessageCount > 0) result.Add($"{path}:{f.MessageCount}");
            foreach (var c in f.Children) Walk(c, path);
        }
        foreach (var f in a.GetFolderTree()) Walk(f, "");
        return result.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Merges_plain_backup_with_folders_and_skips_duplicates_without_touching_backup()
    {
        using var tmp = new TempDir();
        var backup = Path.Combine(tmp.Path, "sicherung");
        CreateBackup(backup);
        var before = Snapshot(backup);
        Assert.Equal(ArchiveFolderKind.Plain, ArchiveImport.Inspect(backup));

        using var current = MailArchive.Open(Path.Combine(tmp.Path, "aktuell"));
        var inbox = current.EnsureFolderPath(new[] { "anna@example.org", "INBOX" });
        current.StoreMessage(inbox, Shared);                       // schon vorhanden
        current.StoreMessage(inbox, Msg("Nur im neuen Archiv", 9));

        var result = await ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(), null, Timeout());
        Assert.Equal(4, result.Archived);
        Assert.Equal(1, result.AlreadyArchived);
        Assert.Equal(0, result.Errors);
        Assert.Equal(new[] { "Alt-PC/Outlook/Projekte:2", "anna@example.org/INBOX:4" }, Paths(current));

        // Byte für Byte gleich: übernommene Nachricht wie in der Quelle
        var hit = current.Search(new SearchCriteria { Query = "Projektplan" }).Single();
        Assert.Contains("Text zu Projektplan", System.Text.Encoding.UTF8.GetString(current.GetRawMessage(hit.Id)));

        // Sicherung unverändert, keine Arbeitskopie übrig
        Assert.Equal(before, Snapshot(backup));
        Assert.Empty(Directory.GetDirectories(current.DataDirectory, ".einbinden-*"));

        // Ein zweiter Lauf übernimmt nichts doppelt
        var again = await ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(), null, Timeout());
        Assert.Equal(0, again.Archived);
        Assert.Equal(5, again.AlreadyArchived);
    }

    [Fact]
    public async Task Protected_backup_needs_password_or_recovery_key()
    {
        using var tmp = new TempDir();
        var backup = Path.Combine(tmp.Path, "sicherung");
        var recovery = CreateBackup(backup, "Sicher#2024!")!;
        var before = Snapshot(backup);
        Assert.Equal(ArchiveFolderKind.Protected, ArchiveImport.Inspect(backup));
        using var current = MailArchive.Open(Path.Combine(tmp.Path, "aktuell"));

        Assert.ThrowsAny<CryptographicException>(() => ArchiveImport.CheckCredentials(backup, new ArchiveCredentials(Password: "falsch")));
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(Password: "falsch"), null, Timeout()));
        Assert.Empty(Directory.GetDirectories(current.DataDirectory, ".einbinden-*"));

        ArchiveImport.CheckCredentials(backup, new ArchiveCredentials(Password: "Sicher#2024!"));
        var r1 = await ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(Password: "Sicher#2024!"), null, Timeout());
        Assert.Equal(5, r1.Archived);

        // Mit dem Wiederherstellungsschlüssel geht es auch – ohne dass in der Sicherung ein neues Kennwort gesetzt wird
        using var other = MailArchive.Open(Path.Combine(tmp.Path, "anderer-pc"));
        var r2 = await ArchiveImport.MergeAsync(other, backup, new ArchiveCredentials(RecoveryKey: recovery), null, Timeout());
        Assert.Equal(5, r2.Archived);
        Assert.Equal(before, Snapshot(backup));
        using (var reopen = MailArchive.Open(CopyDir(backup, Path.Combine(tmp.Path, "probe")), "Sicher#2024!"))
            Assert.Equal(5, reopen.GetStats().MessageCount); // altes Kennwort gilt weiter
    }

    [Fact]
    public async Task Backup_without_key_file_can_be_merged_with_emergency_sheet()
    {
        using var tmp = new TempDir();
        var backup = Path.Combine(tmp.Path, "sicherung");
        var recovery = CreateBackup(backup, "Sicher#2024!")!;
        string sheet;
        using (var a = MailArchive.Open(backup, "Sicher#2024!")) sheet = EmergencySheet.Build("Notfallblatt", a.CurrentKeyFile!);
        foreach (var f in Directory.GetFiles(backup, MailArchive.KeyFileName + "*")) File.Delete(f);
        Assert.Equal(ArchiveFolderKind.KeyFileMissing, ArchiveImport.Inspect(backup));

        using var current = MailArchive.Open(Path.Combine(tmp.Path, "aktuell"));
        await Assert.ThrowsAsync<KeyFileMissingException>(() =>
            ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(RecoveryKey: recovery), null, Timeout()));

        var kf = EmergencySheet.ReadKeyFile(sheet);
        var r = await ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(RecoveryKey: recovery, SheetKeyFile: kf), null, Timeout());
        Assert.Equal(5, r.Archived);

        // Notfallblatt eines anderen Archivs: klare Meldung statt Datenmüll
        var foreign = Path.Combine(tmp.Path, "fremd");
        var foreignRecovery = CreateBackup(foreign, "Anderes#Kennwort1")!;
        KeyVault.KeyFile foreignKf;
        using (var f = MailArchive.Open(foreign, "Anderes#Kennwort1")) foreignKf = f.CurrentKeyFile!;
        var ex = await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(RecoveryKey: foreignRecovery, SheetKeyFile: foreignKf), null, Timeout()));
        Assert.Equal(MailVault.Core.Localization.CoreText.T("Key_OtherArchive"), ex.Message);
    }

    [Fact]
    public async Task Refuses_current_archive_and_folders_without_archive()
    {
        using var tmp = new TempDir();
        using var current = MailArchive.Open(Path.Combine(tmp.Path, "aktuell"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArchiveImport.MergeAsync(current, current.DataDirectory + "\\", new ArchiveCredentials(), null, Timeout()));
        var empty = Directory.CreateDirectory(Path.Combine(tmp.Path, "leer")).FullName;
        Assert.Equal(ArchiveFolderKind.NotAnArchive, ArchiveImport.Inspect(empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveImport.MergeAsync(current, empty, new ArchiveCredentials(), null, Timeout()));
    }

    [Fact]
    public async Task Keeps_header_hash_so_later_server_runs_still_detect_duplicates()
    {
        using var tmp = new TempDir();
        var backup = Path.Combine(tmp.Path, "sicherung");
        CreateBackup(backup);
        List<string> hashes;
        using (var b = MailArchive.Open(backup)) hashes = b.ListMessageHashes().Select(h => h.Hash).ToList();
        using var current = MailArchive.Open(Path.Combine(tmp.Path, "aktuell"));
        await ArchiveImport.MergeAsync(current, backup, new ArchiveCredentials(), null, Timeout());
        Assert.All(hashes, h => Assert.True(current.ContainsHash(h)));
    }

    [Fact]
    public void Leftover_work_copies_are_removed()
    {
        using var tmp = new TempDir();
        var leftover = Directory.CreateDirectory(Path.Combine(tmp.Path, ".einbinden-1234abcd")).FullName;
        File.WriteAllText(Path.Combine(leftover, "archive.db"), "x");
        ArchiveImport.CleanupLeftovers(tmp.Path);
        Assert.False(Directory.Exists(leftover));
    }

    static string CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        return to;
    }
}
