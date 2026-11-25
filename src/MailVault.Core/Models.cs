using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Knoten im Archiv-Ordnerbaum (Quelle → Postfachordner → Unterordner).</summary>
public sealed class ArchiveFolder
{
    public long Id { get; init; }
    public long? ParentId { get; init; }
    public string Name { get; set; } = "";
    /// <summary>Nachrichten direkt in diesem Ordner.</summary>
    public long MessageCount { get; set; }
    /// <summary>Nachrichten in diesem Ordner inklusive aller Unterordner.</summary>
    public long TotalCount { get; set; }
    public List<ArchiveFolder> Children { get; } = new();
}

/// <summary>Eine Zeile der Nachrichtenliste (Ordneransicht oder Suchergebnis).</summary>
public sealed record MessageSummary(
    long Id,
    long FolderId,
    string FolderPath,
    DateTime DateUtc,
    DateTime ArchivedUtc,
    string From,
    string To,
    string Subject,
    long Size,
    bool HasAttachments,
    int Priority)
{
    /// <summary>Absender als Name ("Anna Berg" statt "Anna Berg &lt;anna@…&gt;"); ohne Namen die Adresse.</summary>
    public string FromName => DisplayName(From);

    public static string DisplayName(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        var lt = address.IndexOf('<');
        var name = lt > 0 ? address[..lt].Trim().Trim('"', '\'').Trim() : "";
        return name.Length > 0 ? name : address.Trim().Trim('<', '>');
    }
}

public sealed record ArchiveStats(
    long MessageCount,
    long TotalMessageBytes,
    long DatabaseFileBytes,
    DateTime? OldestMessageUtc,
    DateTime? NewestMessageUtc,
    DateTime? LastArchivedUtc,
    bool IsProtected)
{
    /// <summary>Eingesparter Anteil gegenüber den Originalnachrichten (0–1); null, solange das Archiv (Suchindex, Verwaltung) größer ist.</summary>
    public double? SavedFraction => TotalMessageBytes > 0 && DatabaseFileBytes < TotalMessageBytes
        ? 1 - (double)DatabaseFileBytes / TotalMessageBytes : null;
}

/// <summary>Ergebnis einer vollständigen Archivprüfung.</summary>
public sealed record ArchiveCheckResult(DateTime CheckedUtc, long MessagesChecked, long PartsChecked, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Datumsbereiche der Suchoptionen.</summary>
public enum DateRangeType
{
    NotSpecified,
    Custom,
    Today,
    Yesterday,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    ThisYear,
    LastYear,
    OlderThanDays,
    NotOlderThanDays,
}

public sealed class SearchCriteria
{
    public string? Query { get; set; }
    public bool InSubject { get; set; } = true;
    public bool InFromToCcBcc { get; set; } = true;
    public bool InBody { get; set; } = true;
    public bool InAttachmentNames { get; set; } = true;
    public bool InAttachmentContents { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>Betreff enthält (Teilwort, ohne Volltextindex).</summary>
    public string? Subject { get; set; }
    /// <summary>Beste Treffer zuerst statt neueste zuerst (nur mit Suchbegriff).</summary>
    public bool SortByRelevance { get; set; }
    public DateRangeType DateRange { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? Days { get; set; }
    public bool? HasAttachments { get; set; }
    public long? SizeMinBytes { get; set; }
    public long? SizeMaxBytes { get; set; }
    public long? FolderId { get; set; }
    public bool FolderRecurse { get; set; } = true;
    public int? Priority { get; set; }
    /// <summary>Nur in diesen Ordnern suchen (inkl. Unterordner) – für eingeschränkte Benutzer und die KI-Suche. null = alle.</summary>
    public IReadOnlyCollection<long>? AllowedFolderIds { get; set; }
    /// <summary>Nur gesendete (true) bzw. nur empfangene (false) E-Mails: Gesendet-Ordner oder eigene Absenderadresse.</summary>
    public bool? Sent { get; set; }
    /// <summary>Nur in Ordnern, deren Name oder Pfad diesen Text enthält (samt Unterordnern), z. B. aus ordner:rechnungen.</summary>
    public string? FolderName { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Query) && string.IsNullOrWhiteSpace(From) && string.IsNullOrWhiteSpace(To) && string.IsNullOrWhiteSpace(Subject) &&
        DateRange == DateRangeType.NotSpecified && HasAttachments is null && SizeMinBytes is null &&
        SizeMaxBytes is null && FolderId is null && Priority is null &&
        Sent is null && string.IsNullOrWhiteSpace(FolderName);
}

public sealed class ImportResult
{
    public int Archived { get; set; }
    public int AlreadyArchived { get; set; }
    public int Filtered { get; set; }
    public int Errors { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan Duration { get; set; }

    public override string ToString() =>
        CoreText.F("Result_Summary", Archived, AlreadyArchived, Filtered, Errors) + (Cancelled ? CoreText.T("Result_Cancelled") : "");
}

/// <summary>Kopfdaten einer Nachricht für Auswertungen (Kontaktnetz, Statistik).</summary>
public sealed record HeaderRow(long Id, long FolderId, DateTime DateUtc, string From, string To, string Cc, string Subject, string? MessageId, bool HasAttachments);

public sealed record ProgressInfo(string? Status, int Processed, int? Total, string? LogLine = null);
