using System.Diagnostics;
using MailVault.Core.Sources;
using MimeKit;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Führt ein Archivierungsprofil aus: Quelle lesen, filtern, Duplikate erkennen, speichern. Die Quelle wird nie verändert.</summary>
public static class ImportJob
{
    public static async Task<ImportResult> RunAsync(MailArchive archive, ArchiveProfile profile,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var result = new ImportResult();
        var sw = Stopwatch.StartNew();
        void Log(string line) => progress?.Report(new ProgressInfo(null, result.Archived + result.AlreadyArchived, null, line));

        await using var source = SourceFactory.Create(profile);
        Log(CoreText.F("Import_Connecting", source.DisplayName));
        await source.ConnectAsync(ct);
        Log(CoreText.T("Import_Connected"));

        var cutoff = profile.OnlyNewerThanDays is { } days ? DateTimeOffset.Now.AddDays(-days) : (DateTimeOffset?)null;
        var folderIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var target = profile.EffectiveTargetFolder;
        var processed = 0;

        try
        {
            await foreach (var sm in source.EnumerateAsync(p => FolderFilter(profile, p), progress, ct))
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                try
                {
                    var date = sm.Date;
                    if (date is null && sm.Headers?[HeaderId.Date] is { } hd && MimeKit.Utils.DateUtils.TryParse(hd, out var parsedDate))
                        date = parsedDate;
                    if (cutoff is { } c && date is { } d && d < c)
                    {
                        result.Filtered++;
                        continue;
                    }

                    string? hash = sm.Headers is not null ? MessageIndexer.ComputeHeaderHash(sm.Headers, sm.Size) : null;
                    bool archivedNow = false, alreadyArchived = hash is not null && archive.ContainsHash(hash);

                    if (!alreadyArchived)
                    {
                        var raw = await sm.LoadRawAsync(ct);
                        var msg = MimeMessage.Load(new MemoryStream(raw, writable: false));
                        if (date is null && msg.Date != DateTimeOffset.MinValue)
                        {
                            date = msg.Date;
                            if (cutoff is { } c2 && date < c2)
                            {
                                result.Filtered++;
                                continue;
                            }
                        }
                        hash ??= MessageIndexer.ComputeHeaderHash(msg.Headers, raw.LongLength);
                        if (archive.ContainsHash(hash)) alreadyArchived = true;
                        else
                        {
                            var key = string.Join("/", sm.FolderPath);
                            if (!folderIds.TryGetValue(key, out var folderId))
                            {
                                var path = new List<string> { target };
                                path.AddRange(sm.FolderPath);
                                folderIds[key] = folderId = archive.EnsureFolderPath(path);
                            }
                            var id = archive.StoreMessage(folderId, raw, msg, hash);
                            if (id is null) alreadyArchived = true; else archivedNow = true;
                        }
                    }

                    if (archivedNow) result.Archived++;
                    else result.AlreadyArchived++;

                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Errors++;
                    Log(CoreText.F("Import_MessageError", string.Join("/", sm.FolderPath), ex.Message));
                }

                if (processed % 10 == 0)
                    progress?.Report(new ProgressInfo(
                        CoreText.F("Import_Progress", result.Archived, result.AlreadyArchived, string.Join("/", sm.FolderPath)),
                        processed, null));
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        result.Duration = sw.Elapsed;
        progress?.Report(new ProgressInfo(CoreText.T("Import_Done"), processed, processed));
        return result;
    }

    public static bool FolderFilter(ArchiveProfile p, IReadOnlyList<string> path)
    {
        var full = string.Join("/", path);
        static bool Under(string full, string prefix)
        {
            prefix = prefix.Trim().Trim('/');
            return full.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
        if (p.IncludeFolders.Count > 0 && !p.IncludeFolders.Any(i => Under(full, i))) return false;
        if (p.ExcludeFolders.Any(e => Under(full, e))) return false;
        if (p.ExcludeSpecialFolders && SpecialFolders.IsSpecial(path)) return false;
        return true;
    }
}
