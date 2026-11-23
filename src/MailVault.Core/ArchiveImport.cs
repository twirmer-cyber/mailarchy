using System.Diagnostics;
using System.Security.Cryptography;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Was in einem gewählten Ordner liegt.</summary>
public enum ArchiveFolderKind
{
    /// <summary>Keine archive.db – kein MailVault-Archiv.</summary>
    NotAnArchive,
    Plain,
    Protected,
    /// <summary>Verschlüsselt, aber die Schlüsseldatei fehlt: Notfallblatt + Wiederherstellungsschlüssel nötig.</summary>
    KeyFileMissing,
}

/// <summary>Zugangsdaten für ein gesichertes Archiv (je nach Lage genügt eines davon).</summary>
public sealed record ArchiveCredentials(string? Password = null, string? RecoveryKey = null, KeyVault.KeyFile? SheetKeyFile = null);

/// <summary>
/// Bindet ein gesichertes MailVault-Archiv (z. B. vom alten PC oder von einer externen Festplatte) in die vorhandene
/// Installation ein. Die Sicherung selbst wird nie verändert: Gelesen wird aus einer Arbeitskopie im Datenordner, die
/// danach gelöscht wird. Übernommen werden alle E-Mails mit ihrer Ordnerstruktur; was schon im Archiv liegt (gleiche
/// Kopfzeilen-Prüfsumme), wird übersprungen.
/// </summary>
public static class ArchiveImport
{
    static readonly string[] CopiedFiles =
    {
        MailArchive.DbFileName, MailArchive.DbFileName + "-wal",
        MailArchive.KeyFileName, MailArchive.KeyFileName + KeyVault.BackupSuffix, MailArchive.KeyFileName + ".pending",
    };

    /// <summary>Prüft einen Ordner, ohne etwas darin zu verändern.</summary>
    public static ArchiveFolderKind Inspect(string dir)
    {
        var db = Path.Combine(dir, MailArchive.DbFileName);
        if (!File.Exists(db)) return ArchiveFolderKind.NotAnArchive;
        var keys = Path.Combine(dir, MailArchive.KeyFileName);
        if (File.Exists(keys) || File.Exists(keys + KeyVault.BackupSuffix) || File.Exists(keys + ".pending")) return ArchiveFolderKind.Protected;
        var header = new byte[16];
        using (var fs = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            if (fs.Read(header) < 16) return ArchiveFolderKind.Plain; // leere/neue Datei
        return header.AsSpan().SequenceEqual("SQLite format 3\0"u8) ? ArchiveFolderKind.Plain : ArchiveFolderKind.KeyFileMissing;
    }

    /// <summary>Gleicher Ordner wie das laufende Archiv (Groß-/Kleinschreibung und abschließende Trenner egal)?</summary>
    public static bool IsSameDirectory(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Prüft die Zugangsdaten nur an der Schlüsseldatei (schnell, ohne die Archivdatei zu kopieren). Ob ein Notfallblatt
    /// wirklich zu dieser Archivdatei gehört, zeigt sich erst beim Öffnen in <see cref="MergeAsync"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Kennwort bzw. Wiederherstellungsschlüssel falsch.</exception>
    public static void CheckCredentials(string sourceDir, ArchiveCredentials c)
    {
        switch (Inspect(sourceDir))
        {
            case ArchiveFolderKind.Protected:
                var kf = KeyVault.Load(Path.Combine(sourceDir, MailArchive.KeyFileName));
                if (!string.IsNullOrWhiteSpace(c.RecoveryKey)) KeyVault.UnlockWithRecoveryKey(kf, c.RecoveryKey);
                else KeyVault.UnlockWithPassword(kf, c.Password ?? "");
                break;
            case ArchiveFolderKind.KeyFileMissing:
                if (c.SheetKeyFile is null) throw new KeyFileMissingException(CoreText.T("Key_Missing"));
                KeyVault.UnlockWithRecoveryKey(c.SheetKeyFile, c.RecoveryKey ?? "");
                break;
        }
    }

    public static async Task<ImportResult> MergeAsync(MailArchive target, string sourceDir, ArchiveCredentials credentials,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var result = new ImportResult();
        var sw = Stopwatch.StartNew();
        var work = await CopyAsync(target, sourceDir, progress, ct);
        try
        {
            progress?.Report(new ProgressInfo(CoreText.T("ArchiveImport_Opening"), 0, null));
            using var source = await Task.Run(() =>
                MailArchive.OpenCopyForImport(work, credentials.Password, credentials.RecoveryKey, credentials.SheetKeyFile), ct);
            var messages = source.ListMessageHashes();
            progress?.Report(new ProgressInfo(CoreText.F("ArchiveImport_Found", messages.Count), 0, messages.Count,
                CoreText.F("ArchiveImport_FoundLog", messages.Count, sourceDir)));

            var folderMap = new Dictionary<long, long>();
            var processed = 0;
            await Task.Run(() =>
            {
                foreach (var (id, folderId, hash) in messages)
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                    processed++;
                    try
                    {
                        if (target.ContainsHash(hash)) result.AlreadyArchived++;
                        else
                        {
                            if (!folderMap.TryGetValue(folderId, out var targetFolder))
                                folderMap[folderId] = targetFolder = target.EnsureFolderPath(source.GetFolderPathParts(folderId));
                            var raw = source.GetRawMessage(id);
                            // Die Prüfsumme aus der Sicherung übernehmen: So bleibt die Duplikaterkennung für spätere
                            // Abrufe desselben Postfachs gleich (sie wurde dort ggf. mit der Servergröße berechnet).
                            if (target.StoreMessage(targetFolder, raw, headerHash: hash) is null) result.AlreadyArchived++;
                            else result.Archived++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result.Errors++;
                        progress?.Report(new ProgressInfo(null, processed, messages.Count, CoreText.F("ArchiveImport_MessageError", id, ex.Message)));
                    }
                    if (processed % 25 == 0 || processed == messages.Count)
                        progress?.Report(new ProgressInfo(CoreText.F("ArchiveImport_Progress", result.Archived, result.AlreadyArchived, messages.Count),
                            processed, messages.Count));
                }
            }, CancellationToken.None);
        }
        finally
        {
            DeleteWorkCopy(work);
        }
        result.Duration = sw.Elapsed;
        return result;
    }

    /// <summary>Arbeitskopie im Datenordner des laufenden Archivs (gleiches Laufwerk, kein Umweg über %TEMP%).</summary>
    static async Task<string> CopyAsync(MailArchive target, string sourceDir, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (IsSameDirectory(sourceDir, target.DataDirectory)) throw new InvalidOperationException(CoreText.T("ArchiveImport_SameArchive"));
        var kind = Inspect(sourceDir);
        if (kind == ArchiveFolderKind.NotAnArchive) throw new InvalidOperationException(CoreText.T("ArchiveImport_NotAnArchive"));

        var files = CopiedFiles.Select(f => Path.Combine(sourceDir, f)).Where(File.Exists).ToList();
        var needed = files.Sum(f => new FileInfo(f).Length);
        var root = Path.GetPathRoot(Path.GetFullPath(target.DataDirectory));
        if (root is { Length: > 0 })
        {
            try
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < needed + 200L * 1024 * 1024)
                    throw new IOException(CoreText.F("ArchiveImport_NoSpace", FormatMb(needed), FormatMb(free)));
            }
            catch (ArgumentException) { /* Netzlaufwerk ohne Laufwerksinfo: einfach versuchen */ }
        }

        var work = Path.Combine(target.DataDirectory, ".einbinden-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new ProgressInfo(CoreText.F("ArchiveImport_Copying", FormatMb(needed)), 0, null));
            foreach (var f in files)
            {
                await using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true);
                await using var dst = new FileStream(Path.Combine(work, Path.GetFileName(f)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                await src.CopyToAsync(dst, ct);
            }
            return work;
        }
        catch
        {
            DeleteWorkCopy(work);
            throw;
        }
    }

    static void DeleteWorkCopy(string work)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(200); } // SQLite gibt die Datei manchmal einen Moment später frei
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    /// <summary>Übrig gebliebene Arbeitskopien (z. B. nach einem Absturz) beim Start entfernen.</summary>
    public static void CleanupLeftovers(string dataDirectory)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(dataDirectory, ".einbinden-*")) DeleteWorkCopy(d);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string FormatMb(long bytes) => $"{bytes / (1024.0 * 1024.0):N0} MB";
}
