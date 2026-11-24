using MimeKit;

namespace MailVault.Core.Sources;

/// <summary>Eine Nachricht, wie sie eine Quelle liefert. Kopfzeilen erlauben die Duplikatprüfung vor dem Download.</summary>
public sealed class SourceMessage
{
    /// <summary>Ordnerpfad innerhalb der Quelle, z. B. ["INBOX", "Projekte"].</summary>
    public required IReadOnlyList<string> FolderPath { get; init; }
    /// <summary>Kopfzeilen, falls ohne vollständigen Download verfügbar.</summary>
    public HeaderList? Headers { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Date { get; init; }
    /// <summary>Lädt die vollständige Originalnachricht (RFC 822).</summary>
    public required Func<CancellationToken, Task<byte[]>> LoadRawAsync { get; init; }
}

/// <summary>
/// Eine E-Mail-Quelle. Quellen werden ausschließlich lesend benutzt: Es gibt bewusst keine Methode zum Löschen,
/// Verschieben oder Markieren. IMAP-Ordner werden schreibgeschützt geöffnet (EXAMINE), Nachrichten mit BODY.PEEK
/// geladen (der Gelesen-Status bleibt unverändert), bei POP3 wird nie DELE gesendet.
/// </summary>
public interface IMailSource : IAsyncDisposable
{
    string DisplayName { get; }

    Task ConnectAsync(CancellationToken ct);

    /// <summary>Alle Ordner der Quelle (für die Ordnerauswahl im Profil).</summary>
    Task<IReadOnlyList<IReadOnlyList<string>>> ListFoldersAsync(CancellationToken ct);

    IAsyncEnumerable<SourceMessage> EnumerateAsync(Func<IReadOnlyList<string>, bool> folderFilter, IProgress<ProgressInfo>? progress, CancellationToken ct);

}

public static class SourceFactory
{
    public static IMailSource Create(ArchiveProfile p) => p.Type switch
    {
        ProfileType.Imap => new ImapSource(p),
        ProfileType.Pop3 => new Pop3Source(p),
        ProfileType.EmlFolder => new EmlFolderSource(p.Path, p.Recursive),
        ProfileType.Mbox => new MboxSource(p.Path),
        ProfileType.OutlookPst => new OutlookPstSource(p.Path),
        ProfileType.Thunderbird => new ThunderbirdSource(string.IsNullOrWhiteSpace(p.Path) ? null : p.Path),
        _ => throw new NotSupportedException(p.Type.ToString()),
    };
}

/// <summary>Erkennung der Sonderordner, die beim Archivieren standardmäßig ausgeschlossen werden
/// ("Gelöschte Objekte, Entwürfe, Junk-E-Mail und Postausgang").</summary>
public static class SpecialFolders
{
    static readonly string[] Names =
    {
        "trash", "deleted", "deleted items", "deleted messages", "papierkorb", "gelöschte objekte", "gelöschte elemente",
        "drafts", "draft", "entwürfe", "junk", "junk e-mail", "junk-e-mail", "spam", "bulk mail",
        "outbox", "postausgang", "[gmail]/papierkorb", "[gmail]/trash", "[gmail]/spam", "[gmail]/entwürfe", "[gmail]/drafts",
        "templates", "vorlagen",
    };

    public static bool IsSpecial(IReadOnlyList<string> path)
    {
        var full = string.Join("/", path).ToLowerInvariant();
        var last = path.Count > 0 ? path[^1].ToLowerInvariant() : "";
        return Names.Contains(full) || Names.Contains(last);
    }
}
