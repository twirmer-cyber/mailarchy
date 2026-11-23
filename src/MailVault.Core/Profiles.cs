using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MailVault.Core.Localization;

namespace MailVault.Core;

public enum ProfileType
{
    Imap,
    Pop3,
    Thunderbird,
    EmlFolder,
    Mbox,
    OutlookPst,
}

/// <summary>Wann ein Profil automatisch ausgeführt wird.</summary>
public enum ScheduleKind
{
    /// <summary>Nur auf Klick.</summary>
    Manual,
    /// <summary>Alle n Minuten.</summary>
    Interval,
    /// <summary>Täglich zu einer festen Uhrzeit.</summary>
    Daily,
}

public enum ConnectionSecurity
{
    Auto,
    SslOnConnect,
    StartTls,
    None,
}

/// <summary>
/// Archivierungsprofil – im Original werden "Archivierungsaufgaben in Form von Profilen gespeichert".
/// Ein Profil beschreibt Quelle, Filter und Löschverhalten; das Löschen eines Profils ändert nichts am Archiv.
/// </summary>
public sealed class ArchiveProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ProfileType Type { get; set; }

    // Server (IMAP/POP3)
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public ConnectionSecurity Security { get; set; } = ConnectionSecurity.Auto;
    public string UserName { get; set; } = "";
    /// <summary>Per Windows-DPAPI (Benutzerkontext) verschlüsseltes Kennwort.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>Anmeldung mit Kennwort oder per OAuth (Microsoft/Google). Fehlt in alten profiles.json = Kennwort.</summary>
    public MailVault.Core.OAuth.MailAuth AuthMethod { get; set; } = MailVault.Core.OAuth.MailAuth.Password;
    public bool AcceptInvalidCertificates { get; set; }

    // Dateien (EML-Ordner, MBOX, Thunderbird-Profilordner)
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = true;

    // Filter
    public List<string> IncludeFolders { get; set; } = new();
    public List<string> ExcludeFolders { get; set; } = new();
    /// <summary>Papierkorb, Entwürfe, Junk-E-Mail und Postausgang automatisch überspringen.</summary>
    public bool ExcludeSpecialFolders { get; set; } = true;
    public int? OnlyNewerThanDays { get; set; }

    // Automatischer Abruf
    public ScheduleKind Schedule { get; set; } = ScheduleKind.Manual;
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Uhrzeit (Ortszeit) für den täglichen Abruf, Format HH:mm.</summary>
    public string DailyTime { get; set; } = "08:00";

    /// <summary>Oberster Ordnername im Archiv; leer = automatisch aus der Quelle.</summary>
    public string TargetFolder { get; set; } = "";

    public DateTime? LastRunUtc { get; set; }
    public string? LastResult { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    /// <summary>Letzte Fehlermeldung (verständlich formuliert), null nach erfolgreichem Lauf.</summary>
    public string? LastError { get; set; }
    /// <summary>Fehlgeschlagene automatische Abrufe in Folge.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Zeitpunkt der letzten Fehler-Benachrichtigung in der aktuellen Fehlerserie (null = noch keine).</summary>
    public DateTime? FailureNotifiedUtc { get; set; }

    [JsonIgnore]
    public bool IsServer => Type is ProfileType.Imap or ProfileType.Pop3;

    [JsonIgnore]
    public string ScheduleDisplay => Schedule switch
    {
        ScheduleKind.Interval => IntervalMinutes % 60 == 0 && IntervalMinutes >= 60
            ? (IntervalMinutes == 60 ? CoreText.T("Schedule_Hourly") : CoreText.F("Schedule_EveryHours", IntervalMinutes / 60))
            : CoreText.F("Schedule_EveryMinutes", IntervalMinutes),
        ScheduleKind.Daily => CoreText.F("Schedule_Daily", DailyTime),
        _ => CoreText.T("Schedule_Manual"),
    };

    [JsonIgnore]
    public string StatusDisplay => LastError is not null
        ? CoreText.F("Status_Error", LastError)
        : LastResult ?? CoreText.T("Status_NeverRun");

    [JsonIgnore]
    public DateTime? LastRunLocal => LastRunUtc?.ToLocalTime();

    [JsonIgnore]
    public string Password
    {
        get => ProtectedPassword is null ? "" : Unprotect(ProtectedPassword);
        set => ProtectedPassword = string.IsNullOrEmpty(value) ? null : Protect(value);
    }

    /// <summary>Uhrzeit im Format HH:mm lesen (für den täglichen Abruf).</summary>
    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var h) || h is < 0 or > 23) return false;
        var m = 0;
        if (parts.Length == 2 && (parts[1].Length != 2 || !int.TryParse(parts[1], out m) || m is < 0 or > 59)) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    public string TypeDisplayName => Type switch
    {
        ProfileType.Imap => CoreText.T("ProfileType_Imap"),
        ProfileType.Pop3 => CoreText.T("ProfileType_Pop3"),
        ProfileType.Thunderbird => "Mozilla Thunderbird",
        ProfileType.EmlFolder => CoreText.T("ProfileType_EmlFolder"),
        ProfileType.Mbox => CoreText.T("ProfileType_Mbox"),
        ProfileType.OutlookPst => "Microsoft Outlook (PST/OST)",
        _ => Type.ToString(),
    };

    public string EffectiveTargetFolder => !string.IsNullOrWhiteSpace(TargetFolder) ? TargetFolder.Trim() : Type switch
    {
        ProfileType.Imap or ProfileType.Pop3 => string.IsNullOrWhiteSpace(UserName) ? Host : UserName,
        ProfileType.Thunderbird => "Thunderbird",
        ProfileType.EmlFolder => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "E-Mail-Dateien",
        ProfileType.Mbox => System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } n ? n : "MBOX",
        ProfileType.OutlookPst => System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } n ? "Outlook " + n : "Outlook",
        _ => Name,
    };

    public ArchiveProfile Clone() =>
        JsonSerializer.Deserialize<ArchiveProfile>(JsonSerializer.Serialize(this, ProfileStore.JsonOptions), ProfileStore.JsonOptions)!;

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MailVault.Profile.v1");
    static string Protect(string s) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), Entropy, DataProtectionScope.CurrentUser));
    static string Unprotect(string s)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return ""; } // anderes Benutzerkonto/Rechner (z. B. portable Nutzung)
    }
}

/// <summary>Speichert die Profile als JSON im Datenverzeichnis.</summary>
public sealed class ProfileStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly object _sync = new();
    List<ArchiveProfile> _profiles;

    public ProfileStore(string dataDirectory)
    {
        _path = System.IO.Path.Combine(dataDirectory, "profiles.json");
        _profiles = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<ArchiveProfile>>(File.ReadAllText(_path), JsonOptions) ?? new()
            : new();
    }

    public IReadOnlyList<ArchiveProfile> All
    {
        get { lock (_sync) return _profiles.Select(p => p.Clone()).ToList(); }
    }

    /// <summary>Wird nach jeder Änderung ausgelöst (aus beliebigem Thread).</summary>
    public event Action? Changed;

    public ArchiveProfile? Get(Guid id)
    {
        lock (_sync) return _profiles.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    /// <summary>
    /// Speichert die Einstellungen eines Profils. Der Laufstatus (letzter Lauf, Fehler, Benachrichtigung) bleibt
    /// erhalten, damit ein offener Editor keinen Status überschreibt, den ein Hintergrundlauf inzwischen gesetzt hat.
    /// </summary>
    public void Save(ArchiveProfile profile)
    {
        lock (_sync)
        {
            var copy = profile.Clone();
            var i = _profiles.FindIndex(p => p.Id == profile.Id);
            if (i >= 0)
            {
                var old = _profiles[i];
                copy.LastRunUtc = old.LastRunUtc;
                copy.LastResult = old.LastResult;
                copy.LastSuccessUtc = old.LastSuccessUtc;
                copy.LastError = old.LastError;
                copy.ConsecutiveFailures = old.ConsecutiveFailures;
                copy.FailureNotifiedUtc = old.FailureNotifiedUtc;
                _profiles[i] = copy;
            }
            else _profiles.Add(copy);
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Ändert ein gespeichertes Profil atomar (für den Laufstatus). Gibt die neue Fassung zurück.</summary>
    public ArchiveProfile? Update(Guid id, Action<ArchiveProfile> change)
    {
        ArchiveProfile? result;
        lock (_sync)
        {
            var p = _profiles.FirstOrDefault(x => x.Id == id);
            if (p is null) return null;
            change(p);
            Persist();
            result = p.Clone();
        }
        Changed?.Invoke();
        return result;
    }

    public void Delete(Guid id)
    {
        lock (_sync)
        {
            _profiles.RemoveAll(p => p.Id == id);
            Persist();
        }
        Changed?.Invoke();
    }

    void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
