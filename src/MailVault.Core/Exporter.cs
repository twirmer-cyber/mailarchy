using System.Text;

using MailVault.Core.Localization;

namespace MailVault.Core;

public enum ExportFormat
{
    /// <summary>Einzelne .eml-Dateien.</summary>
    EmlFiles,
    /// <summary>MBOX-Datei(en) im mboxrd-Format.</summary>
    Mbox,
    /// <summary>Eine PDF-Datei je E-Mail (zum Drucken oder als Nachweis); braucht einen <see cref="IMessagePdfWriter"/>.</summary>
    Pdf,
}

/// <summary>Wandelt eine archivierte Nachricht in eine PDF-Datei um (in der App über die WebView2-Druckfunktion).</summary>
public interface IMessagePdfWriter
{
    Task WriteAsync(long messageId, string file, CancellationToken ct);
}

/// <summary>Exportiert archivierte Nachrichten ("E-Mails exportieren / Sicherung").</summary>
public static class Exporter
{
    /// <param name="keepStructure">"Ordnerstruktur beibehalten": Unterordner bzw. eine MBOX je Archivordner.</param>
    public static async Task<int> ExportAsync(MailArchive archive, IReadOnlyList<long> messageIds, string target,
        ExportFormat format, bool keepStructure, IProgress<ProgressInfo>? progress, CancellationToken ct, IMessagePdfWriter? pdf = null)
    {
        if (format == ExportFormat.Pdf && pdf is null) throw new ArgumentNullException(nameof(pdf));
        Directory.CreateDirectory(format == ExportFormat.Mbox && !keepStructure ? Path.GetDirectoryName(Path.GetFullPath(target))! : target);
        var mboxWriters = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var exported = 0;
        try
        {
            for (var i = 0; i < messageIds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var summary = archive.GetSummary(messageIds[i]);
                if (summary is null) continue;
                var raw = format == ExportFormat.Pdf ? Array.Empty<byte>() : archive.GetRawMessage(summary.Id);
                var folderParts = summary.FolderPath.Split('/').Select(p => SafeName(p)).ToArray();

                if (format is ExportFormat.EmlFiles or ExportFormat.Pdf)
                {
                    var dir = keepStructure ? Path.Combine(new[] { target }.Concat(folderParts).ToArray()) : target;
                    Directory.CreateDirectory(dir);
                    var baseName = $"{summary.DateUtc.ToLocalTime():yyyy-MM-dd HHmm} {SafeName(summary.Subject, 80)}";
                    if (format == ExportFormat.Pdf) await pdf!.WriteAsync(summary.Id, UniquePath(dir, baseName, ".pdf"), ct);
                    else await File.WriteAllBytesAsync(UniquePath(dir, baseName, ".eml"), raw, ct);
                }
                else
                {
                    var file = keepStructure
                        ? Path.Combine(new[] { target }.Concat(folderParts.SkipLast(1)).Append(folderParts[^1] + ".mbox").ToArray())
                        : (target.EndsWith(".mbox", StringComparison.OrdinalIgnoreCase) ? target : target + ".mbox");
                    if (!mboxWriters.TryGetValue(file, out var fs))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
                        mboxWriters[file] = fs;
                    }
                    await WriteMboxEntryAsync(fs, raw, summary.DateUtc, ct);
                }

                exported++;
                if (i % 20 == 0 || i == messageIds.Count - 1)
                    progress?.Report(new ProgressInfo(CoreText.F("Export_Progress", exported, messageIds.Count), i + 1, messageIds.Count));
            }
        }
        finally
        {
            foreach (var fs in mboxWriters.Values) await fs.DisposeAsync();
        }
        return exported;
    }

    /// <summary>Schreibt einen Eintrag im mboxrd-Format: "From "-Trennzeile, Zeilen mit &gt;*From  werden maskiert.</summary>
    public static async Task WriteMboxEntryAsync(Stream s, byte[] raw, DateTime dateUtc, CancellationToken ct)
    {
        var sep = Encoding.ASCII.GetBytes(
            "From MAILER-DAEMON " + dateUtc.ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        await s.WriteAsync(sep, ct);
        var output = new MemoryStream(raw.Length + 256);
        var lineStart = 0;
        while (lineStart < raw.Length)
        {
            var nl = Array.IndexOf(raw, (byte)'\n', lineStart);
            var end = nl < 0 ? raw.Length : nl + 1;
            var j = lineStart;
            while (j < end && raw[j] == (byte)'>') j++;
            if (end - j >= 5 && raw[j] == 'F' && raw[j + 1] == 'r' && raw[j + 2] == 'o' && raw[j + 3] == 'm' && raw[j + 4] == ' ')
                output.WriteByte((byte)'>');
            output.Write(raw, lineStart, end - lineStart);
            lineStart = end;
        }
        if (raw.Length == 0 || raw[^1] != '\n') output.WriteByte((byte)'\n');
        output.WriteByte((byte)'\n');
        output.Position = 0;
        await output.CopyToAsync(s, ct);
    }

    public static string SafeName(string name) => SafeName(name, 120);

    public static string SafeName(string name, int max)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length == 0) s = CoreText.T("Export_NoSubject");
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    static string UniquePath(string dir, string baseName, string ext)
    {
        var path = Path.Combine(dir, baseName + ext);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{baseName} ({n}){ext}");
        return path;
    }
}
