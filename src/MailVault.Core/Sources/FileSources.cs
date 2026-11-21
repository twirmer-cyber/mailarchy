using System.Runtime.CompilerServices;
using MimeKit;

using MailVault.Core.Localization;

namespace MailVault.Core.Sources;

/// <summary>Importiert .eml-Dateien aus einem Ordner (optional rekursiv; Unterordner werden zu Archivordnern).</summary>
public sealed class EmlFolderSource : IMailSource
{
    readonly string _root;
    readonly bool _recursive;

    public EmlFolderSource(string root, bool recursive)
    {
        _root = root;
        _recursive = recursive;
    }

    public string DisplayName => CoreText.F("Source_EmlFolder", _root);

    public Task ConnectAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(CoreText.F("Source_FolderMissing", _root));
        return Task.CompletedTask;
    }

    IEnumerable<string> Directories() =>
        new[] { _root }.Concat(_recursive ? Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories) : Array.Empty<string>());

    IReadOnlyList<string> RelPath(string dir)
    {
        var rel = Path.GetRelativePath(_root, dir);
        return rel == "." ? Array.Empty<string>() : rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(Directories().Select(RelPath).ToList());

    public async IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter,
        IProgress<ProgressInfo>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var dir in Directories())
        {
            var path = RelPath(dir);
            if (!folderFilter(path)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.eml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var f = file;
                yield return new SourceMessage
                {
                    FolderPath = path,
                    Size = new FileInfo(f).Length,
                    LoadRawAsync = token => File.ReadAllBytesAsync(f, token),
                };
            }
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Importiert eine MBOX-Datei (mboxrd/mboxo, wie von Thunderbird, Apple Mail, Google Takeout erzeugt).</summary>
public sealed class MboxSource : IMailSource
{
    readonly string _file;
    readonly IReadOnlyList<string> _folderPath;
    /// <summary>Thunderbird markiert gelöschte, aber noch nicht komprimierte Nachrichten mit X-Mozilla-Status 0x0008.</summary>
    readonly bool _skipMozillaExpunged;

    public MboxSource(string file, IReadOnlyList<string>? folderPath = null, bool skipMozillaExpunged = true)
    {
        _file = file;
        _folderPath = folderPath ?? Array.Empty<string>();
        _skipMozillaExpunged = skipMozillaExpunged;
    }

    public string DisplayName => $"MBOX {_file}";

    public Task ConnectAsync(CancellationToken ct)
    {
        if (!File.Exists(_file)) throw new FileNotFoundException(CoreText.F("Source_FileMissing", _file), _file);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(new[] { _folderPath });

    public async IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter,
        IProgress<ProgressInfo>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!folderFilter(_folderPath)) yield break;
        await using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        if (fs.Length == 0) yield break;
        var parser = new MimeParser(fs, MimeFormat.Mbox);
        var n = 0;
        while (!parser.IsEndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            MimeMessage msg;
            try { msg = await parser.ParseMessageAsync(ct); }
            catch (FormatException ex)
            {
                if (n == 0) throw new InvalidDataException(CoreText.F("Source_InvalidMbox", _file), ex);
                progress?.Report(new ProgressInfo(null, n, null, CoreText.F("Source_MboxReadError", n, ex.Message)));
                yield break;
            }
            n++;
            if (_skipMozillaExpunged && IsMozillaExpunged(msg)) continue;

            using var ms = new MemoryStream();
            await msg.WriteToAsync(ms, ct);
            var raw = ms.ToArray();
            yield return new SourceMessage
            {
                FolderPath = _folderPath,
                Headers = msg.Headers,
                Size = raw.LongLength,
                Date = msg.Date == DateTimeOffset.MinValue ? null : msg.Date,
                LoadRawAsync = _ => Task.FromResult(raw),
            };
        }
    }

    static bool IsMozillaExpunged(MimeMessage msg)
    {
        var status = msg.Headers["X-Mozilla-Status"];
        return status is not null && int.TryParse(status.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v) && (v & 0x0008) != 0;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Liest die lokalen Postfächer von Mozilla Thunderbird (Mail/ und ImapMail/ im Profil).
/// Ordner sind MBOX-Dateien ohne Endung; Unterordner liegen in "Name.sbd"-Verzeichnissen.
/// Profile mit Maildir-Speicherung (cur/new) werden ebenfalls erkannt.
/// </summary>
public sealed class ThunderbirdSource : IMailSource
{
    readonly string? _profileDir;
    static readonly string[] IgnoredExtensions = { ".msf", ".dat", ".json", ".sqlite", ".html", ".js", ".txt", ".bak", ".rdf", ".mab", ".lock" };

    public ThunderbirdSource(string? profileDir) => _profileDir = profileDir;
    public string DisplayName => "Mozilla Thunderbird";

    public static IReadOnlyList<string> FindProfiles()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thunderbird", "Profiles");
        return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
    }

    IEnumerable<string> Profiles() => _profileDir is not null ? new[] { _profileDir } : FindProfiles();

    public Task ConnectAsync(CancellationToken ct)
    {
        if (!Profiles().Any(Directory.Exists))
            throw new DirectoryNotFoundException(CoreText.T("Source_NoThunderbird"));
        return Task.CompletedTask;
    }

    sealed record Box(IReadOnlyList<string> Path, string Location, bool IsMaildir);

    List<Box> FindMailboxes()
    {
        var boxes = new List<Box>();
        foreach (var profile in Profiles().Where(Directory.Exists))
        {
            foreach (var kind in new[] { "Mail", "ImapMail" })
            {
                var dir = Path.Combine(profile, kind);
                if (!Directory.Exists(dir)) continue;
                foreach (var account in Directory.GetDirectories(dir))
                    Scan(account, new[] { Path.GetFileName(account) }, boxes);
            }
        }
        return boxes;
    }

    static void Scan(string dir, IReadOnlyList<string> prefix, List<Box> boxes)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (IgnoredExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()) || name.StartsWith('.')) continue;
            if (Path.HasExtension(name) && !File.Exists(file + ".msf")) continue; // z. B. filterlog.html
            if (new FileInfo(file).Length == 0) continue;
            boxes.Add(new Box(prefix.Append(name).ToList(), file, false));
        }
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.EndsWith(".sbd", StringComparison.OrdinalIgnoreCase))
                Scan(sub, prefix.Append(name[..^4]).ToList(), boxes);
            else if (Directory.Exists(Path.Combine(sub, "cur")))
                boxes.Add(new Box(prefix.Append(name).ToList(), sub, true));
        }
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(FindMailboxes().Select(b => b.Path).ToList());

    public async IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter,
        IProgress<ProgressInfo>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var box in FindMailboxes())
        {
            if (!folderFilter(box.Path)) continue;
            progress?.Report(new ProgressInfo(CoreText.F("Source_ReadingFolder", string.Join("/", box.Path)), 0, null, CoreText.F("Source_FolderLog", string.Join("/", box.Path))));
            if (box.IsMaildir)
            {
                foreach (var sub in new[] { "cur", "new" })
                {
                    var d = Path.Combine(box.Location, sub);
                    if (!Directory.Exists(d)) continue;
                    foreach (var file in Directory.EnumerateFiles(d))
                    {
                        var f = file;
                        yield return new SourceMessage { FolderPath = box.Path, Size = new FileInfo(f).Length, LoadRawAsync = t => File.ReadAllBytesAsync(f, t) };
                    }
                }
                continue;
            }
            var mbox = new MboxSource(box.Location, box.Path);
            IAsyncEnumerator<SourceMessage>? e = null;
            try
            {
                e = mbox.EnumerateAsync(_ => true, progress, ct).GetAsyncEnumerator(ct);
                while (true)
                {
                    SourceMessage current;
                    try
                    {
                        if (!await e.MoveNextAsync()) break;
                        current = e.Current;
                    }
                    catch (InvalidDataException ex)
                    {
                        progress?.Report(new ProgressInfo(null, 0, null, ex.Message));
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                if (e is not null) await e.DisposeAsync();
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
