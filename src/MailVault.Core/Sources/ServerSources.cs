using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

using MailVault.Core.Localization;

namespace MailVault.Core.Sources;

internal static class ServerHelpers
{
    public static (int Port, SecureSocketOptions Options) Resolve(ArchiveProfile p, int sslPort, int plainPort) => p.Security switch
    {
        ConnectionSecurity.SslOnConnect => (p.Port > 0 ? p.Port : sslPort, SecureSocketOptions.SslOnConnect),
        ConnectionSecurity.StartTls => (p.Port > 0 ? p.Port : plainPort, SecureSocketOptions.StartTls),
        ConnectionSecurity.None => (p.Port > 0 ? p.Port : plainPort, SecureSocketOptions.None),
        _ => (p.Port > 0 ? p.Port : sslPort, p.Port == plainPort ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.Auto),
    };

    /// <summary>
    /// Anmeldung am Server: Kennwort (LOGIN/PLAIN) oder bei OAuth-Profilen XOAUTH2 mit einem frischen Zugriffstoken.
    /// </summary>
    public static async Task AuthenticateAsync(MailService client, ArchiveProfile p, CancellationToken ct)
    {
        if (p.AuthMethod == MailVault.Core.OAuth.MailAuth.Password)
        {
            await client.AuthenticateAsync(p.UserName, p.Password, ct);
            return;
        }
        var token = await MailVault.Core.OAuth.OAuthService.Require().GetAccessTokenAsync(p, ct);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(p.UserName, token), ct);
    }

    public static async Task<byte[]> ToBytesAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

/// <summary>Archiviert ein einzelnes IMAP-Postfach (alle Ordner, gefiltert durch das Profil).</summary>
public sealed class ImapSource : IMailSource
{
    const int BatchSize = 250;
    readonly ArchiveProfile _p;
    readonly ImapClient _client = new();

    public ImapSource(ArchiveProfile profile) => _p = profile;
    public string DisplayName => $"IMAP {_p.UserName}@{_p.Host}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_p.AcceptInvalidCertificates) _client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var (port, opts) = ServerHelpers.Resolve(_p, 993, 143);
        await _client.ConnectAsync(_p.Host, port, opts, ct);
        await ServerHelpers.AuthenticateAsync(_client, _p, ct);
    }

    async Task<List<IMailFolder>> GetAllFoldersAsync(CancellationToken ct)
    {
        var result = new List<IMailFolder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in _client.PersonalNamespaces)
        {
            foreach (var f in await _client.GetFoldersAsync(ns, cancellationToken: ct))
            {
                if (f.Attributes.HasFlag(FolderAttributes.NoSelect) || f.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
                if (seen.Add(f.FullName)) result.Add(f);
            }
        }
        if (seen.Add(_client.Inbox.FullName)) result.Insert(0, _client.Inbox);
        // Posteingang zuerst, damit Duplikate (z. B. Gmail-Labels) möglichst dort landen.
        return result.OrderBy(f => f == _client.Inbox ? 0 : 1).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static IReadOnlyList<string> PathOf(IMailFolder f) =>
        f.DirectorySeparator == '\0' ? new[] { f.FullName } : f.FullName.Split(f.DirectorySeparator, StringSplitOptions.RemoveEmptyEntries);

    public async Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct) =>
        (await GetAllFoldersAsync(ct)).Select(PathOf).ToList();

    public async IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter,
        IProgress<ProgressInfo>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var folder in await GetAllFoldersAsync(ct))
        {
            var path = PathOf(folder);
            if (!folderFilter(path)) continue;
            progress?.Report(new ProgressInfo(CoreText.F("Source_ReadingFolder", string.Join("/", path)), 0, null, CoreText.F("Source_FolderLog", folder.FullName)));
            try { await folder.OpenAsync(FolderAccess.ReadOnly, ct); }
            catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException)
            {
                progress?.Report(new ProgressInfo(null, 0, null, CoreText.F("Source_FolderOpenFailed", folder.FullName, ex.Message)));
                continue;
            }
            if (folder.Count == 0) { await folder.CloseAsync(false, ct); continue; }

            var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All, ct);
            foreach (var batch in uids.Chunk(BatchSize))
            {
                var summaries = await folder.FetchAsync(batch,
                    MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.InternalDate |
                    MessageSummaryItems.Flags | MessageSummaryItems.Headers, ct);
                foreach (var s in summaries)
                {
                    var uid = s.UniqueId;
                    var f = folder;
                    yield return new SourceMessage
                    {
                        FolderPath = path,
                        Headers = s.Headers,
                        Size = s.Size ?? 0,
                        Date = s.Headers?[HeaderId.Date] is { } d && DateUtils.TryParse(d, out var dt) ? dt : s.InternalDate,
                        LoadRawAsync = async token =>
                        {
                            if (!f.IsOpen) await f.OpenAsync(FolderAccess.ReadOnly, token);
                            using var stream = await f.GetStreamAsync(uid, token);
                            return await ServerHelpers.ToBytesAsync(stream, token);
                        },
                    };
                }
            }
            if (folder.IsOpen) await folder.CloseAsync(false, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(true); } catch { /* Verbindungsabbau ist unkritisch */ }
        _client.Dispose();
    }
}

/// <summary>Archiviert ein POP3-Postfach (POP3 kennt nur den Posteingang).</summary>
public sealed class Pop3Source : IMailSource
{
    readonly ArchiveProfile _p;
    readonly Pop3Client _client = new();

    public Pop3Source(ArchiveProfile profile) => _p = profile;
    public string DisplayName => $"POP3 {_p.UserName}@{_p.Host}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_p.AcceptInvalidCertificates) _client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var (port, opts) = ServerHelpers.Resolve(_p, 995, 110);
        await _client.ConnectAsync(_p.Host, port, opts, ct);
        await ServerHelpers.AuthenticateAsync(_client, _p, ct);
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>(new[] { new[] { "INBOX" } });

    public async IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter,
        IProgress<ProgressInfo>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = new[] { "INBOX" };
        if (!folderFilter(path)) yield break;
        var count = _client.Count;
        IList<int> sizes = count > 0 ? await _client.GetMessageSizesAsync(ct) : new List<int>();
        for (var i = 0; i < count; i++)
        {
            var index = i;
            HeaderList headers;
            using (var hs = await _client.GetStreamAsync(index, true, ct))
                headers = await HeaderList.LoadAsync(hs, ct);
            yield return new SourceMessage
            {
                FolderPath = path,
                Headers = headers,
                Size = sizes.Count > index ? sizes[index] : 0,
                Date = headers[HeaderId.Date] is { } d && DateUtils.TryParse(d, out var dt) ? dt : null,
                LoadRawAsync = async token =>
                {
                    using var stream = await _client.GetStreamAsync(index, false, token);
                    return await ServerHelpers.ToBytesAsync(stream, token);
                },
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(true); } catch { }
        _client.Dispose();
    }
}
