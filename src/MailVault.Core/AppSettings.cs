using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailVault.Core;

/// <summary>
/// Programmeinstellungen. Es gibt eine installierte Variante (Einstellungen unter %APPDATA%)
/// und eine portable Variante: Liegt "portable.txt" neben der EXE, wird alles neben der EXE gespeichert.
/// </summary>
public sealed class AppSettings
{
    public string? DataDirectory { get; set; }
    public bool BlockExternalContent { get; set; } = true;
    public string? LastExportDirectory { get; set; }

    // Hintergrundbetrieb
    /// <summary>MailVault mit Windows starten (im Infobereich), damit geplante Abrufe laufen.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>Beim Schließen des Fensters im Infobereich weiterlaufen, solange geplante Abrufe aktiv sind.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Darstellung: "system" (folgt Windows), "light" oder "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Oberflächensprache: "system" (Windows-Sprache, sofern unterstützt, sonst Englisch), "de", "en", "fr" oder "es". Wirkt nach einem Neustart.</summary>
    public string Language { get; set; } = "system";

    public NotificationSettings Notification { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public WebAccessSettings Web { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    /// <summary>Eigene OAuth-Client-IDs; leer = die in dieser Version eingebauten (siehe docs/OAUTH.md).</summary>
    public OAuthClientSettings OAuth { get; set; } = new();
    public ArchiveCheckSettings ArchiveCheck { get; set; } = new();

    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    public static string SettingsDirectory => IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailVault");

    static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static string DefaultDataDirectory => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MailVault");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load() => LoadFrom(SettingsPath);

    public static AppSettings LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException) { /* beschädigte Einstellungen → Standardwerte */ }
        return new AppSettings();
    }

    public void Save() => SaveTo(SettingsPath);

    public void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Benachrichtigung per E-Mail, wenn ein Postfach nicht abgerufen werden kann.</summary>
/// <summary>Regelmäßige Archivprüfung und ihr letztes Ergebnis (für die Startseite).</summary>
public sealed class ArchiveCheckSettings
{
    public bool Automatic { get; set; } = true;
    public int IntervalDays { get; set; } = 7;
    public DateTime? LastUtc { get; set; }
    public bool? LastOk { get; set; }
    public long LastMessages { get; set; }
    public List<string> LastProblems { get; set; } = new();
    /// <summary>Datenordner, für den das Ergebnis gilt (nach Wechsel des Archivs neu prüfen).</summary>
    public string? DataDirectory { get; set; }

    public bool IsDue(string dataDirectory, DateTime nowUtc) =>
        Automatic && (LastUtc is not { } last || !string.Equals(DataDirectory, dataDirectory, StringComparison.OrdinalIgnoreCase)
                      || nowUtc - last >= TimeSpan.FromDays(Math.Max(1, IntervalDays)));

    public void Store(ArchiveCheckResult r, string dataDirectory)
    {
        LastUtc = r.CheckedUtc;
        LastOk = r.Ok;
        LastMessages = r.MessagesChecked;
        LastProblems = r.Problems.ToList();
        DataDirectory = dataDirectory;
    }
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; }
    public ConnectionSecurity Security { get; set; } = ConnectionSecurity.Auto;
    public string UserName { get; set; } = "";
    public string? ProtectedPassword { get; set; }
    public bool AcceptInvalidCertificates { get; set; }
    /// <summary>Absenderadresse; leer = Benutzername.</summary>
    public string From { get; set; } = "";
    /// <summary>Empfänger, mehrere durch Komma oder Semikolon getrennt.</summary>
    public string To { get; set; } = "";
    /// <summary>Erst nach so vielen Fehlschlägen in Folge benachrichtigen (kurze Aussetzer ignorieren).</summary>
    public int FailureThreshold { get; set; } = 2;
    /// <summary>Bei andauerndem Fehler höchstens alle so viele Stunden erneut erinnern.</summary>
    public int RepeatHours { get; set; } = 24;
    /// <summary>Entwarnung senden, sobald der Abruf wieder klappt.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(ProtectedPassword, "Smtp");
        set => ProtectedPassword = Secret.Protect(value, "Smtp");
    }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(To) &&
                                (!string.IsNullOrWhiteSpace(From) || UserName.Contains('@'));
}

/// <summary>
/// Eigene App-Registrierungen bei Google und Microsoft. Haben Vorrang vor den beim Bauen eingebetteten Client-IDs;
/// so funktioniert die Anmeldung auch in selbst gebauten Versionen oder mit der Registrierung einer Firma.
/// </summary>
public sealed class OAuthClientSettings
{
    public string? MicrosoftClientId { get; set; }
    public string? GoogleClientId { get; set; }
    public string? ProtectedGoogleClientSecret { get; set; }

    [JsonIgnore]
    public string GoogleClientSecret
    {
        get => Secret.Unprotect(ProtectedGoogleClientSecret, "OAuthGoogleSecret");
        set => ProtectedGoogleClientSecret = Secret.Protect(value?.Trim(), "OAuthGoogleSecret");
    }
}

/// <summary>KI-Funktionen (Claude von Anthropic).</summary>
public sealed class AiSettings
{
    public string? ProtectedApiKey { get; set; }
    public string Model { get; set; } = AiModels.Default;
    /// <summary>Der Benutzer hat bestätigt, dass Inhalte zur Auswertung an Anthropic übertragen werden.</summary>
    public bool ConsentGiven { get; set; }
    /// <summary>Höchstzahl an E-Mails, deren Volltext pro Frage gelesen werden darf.</summary>
    public int MaxEmailsRead { get; set; } = 25;

    [JsonIgnore]
    public string ApiKey
    {
        get => Secret.Unprotect(ProtectedApiKey, "ClaudeApiKey");
        set => ProtectedApiKey = Secret.Protect(value?.Trim(), "ClaudeApiKey");
    }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrEmpty(ProtectedApiKey);
}

public static class AiModels
{
    public const string Default = "claude-opus-5-5";
    public static readonly (string Id, string Name)[] All =
    {
        ("claude-opus-5-5", "Claude Opus 5.5 – beste Qualität (Standard)"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5 – schneller, günstiger"),
        ("claude-haiku-5-5", "Claude Haiku 5.5 – sehr schnell, sehr günstig"),
    };
}

/// <summary>Updates von mailvault.zerze.de (siehe docs/UPDATES.md).</summary>
public sealed class UpdateSettings
{
    /// <summary>Regelmäßig nach Updates suchen.</summary>
    public bool AutoCheck { get; set; } = true;
    /// <summary>Gefundene Updates selbst herunterladen und installieren, sobald MailVault gerade nicht benutzt wird.</summary>
    public bool AutoInstall { get; set; } = true;

    public DateTime? LastCheckUtc { get; set; }
    /// <summary>Hinweis auf diese Version nicht erneut anzeigen.</summary>
    public string? DismissedCommit { get; set; }
}

/// <summary>Webzugriff im Heimnetz.</summary>
public sealed class WebAccessSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8025;
    /// <summary>true = aus dem ganzen Heimnetz erreichbar, false = nur auf diesem PC.</summary>
    public bool LanAccess { get; set; } = true;
}
