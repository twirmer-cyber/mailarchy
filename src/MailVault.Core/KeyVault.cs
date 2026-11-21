using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>
/// Archivschutz: "Archivkennwort" + "Wiederherstellungsschlüssel".
/// Die Datenbank wird mit einem zufälligen 256-Bit-Schlüssel per SQLCipher verschlüsselt (Datenbankschlüssel).
/// Dieser Schlüssel liegt zweimal mit AES-256-GCM verpackt in der Schlüsseldatei: einmal mit einem aus dem Kennwort
/// abgeleiteten Schlüssel, einmal mit einem aus dem Wiederherstellungsschlüssel abgeleiteten (PBKDF2-HMAC-SHA256).
/// Ohne Schlüsseldatei ist das Archiv auch mit Kennwort nicht mehr zu öffnen – deshalb wird sie doppelt und
/// stromausfallsicher geschrieben und gehört ins Notfallblatt (siehe <see cref="EmergencySheet"/>).
/// </summary>
public static class KeyVault
{
    /// <summary>PBKDF2-Runden für neue Schlüssel (OWASP-Empfehlung für PBKDF2-HMAC-SHA256, Stand 2023+).</summary>
    public const int Iterations = 600_000;
    const int MinIterations = 100_000;      // ältere oder manipulierte Dateien mit weniger Runden werden abgelehnt
    const int MaxIterations = 10_000_000;   // schützt vor absichtlich unbrauchbar langsamen Dateien
    const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 Zeichen, ohne 0/O/1/I
    public const string BackupSuffix = ".backup";

    public sealed class KeyFile
    {
        public int Version { get; set; } = 1;
        public int Iterations { get; set; } = KeyVault.Iterations;
        public string PasswordSalt { get; set; } = "";
        public string PasswordWrapped { get; set; } = "";
        public string RecoverySalt { get; set; } = "";
        public string RecoveryWrapped { get; set; } = "";
        public bool RecoveryKeyExported { get; set; }
    }

    public static string NewDatabaseKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>30 Zeichen aus 32 = 150 Bit Zufall, gruppiert als XXXXX-XXXXX-… (256 ist durch 32 teilbar: keine Verzerrung).</summary>
    public static string NewRecoveryKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(30);
        var chars = bytes.Select(b => RecoveryAlphabet[b % RecoveryAlphabet.Length]).ToArray();
        return string.Join("-", Enumerable.Range(0, 6).Select(i => new string(chars, i * 5, 5)));
    }

    public static string NormalizeRecoveryKey(string key) =>
        new string(key.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static KeyFile Create(string dbKey, string password, string recoveryKey)
    {
        var kf = new KeyFile();
        SetPassword(kf, dbKey, password);
        SetRecoveryKey(kf, dbKey, recoveryKey);
        return kf;
    }

    public static void SetPassword(KeyFile kf, string dbKey, string password)
    {
        var ps = RandomNumberGenerator.GetBytes(16);
        kf.PasswordSalt = Convert.ToBase64String(ps);
        kf.PasswordWrapped = Wrap(dbKey, Derive(password, ps, kf.Iterations));
    }

    /// <summary>Ersetzt den Wiederherstellungsschlüssel; der alte ist danach wertlos.</summary>
    public static void SetRecoveryKey(KeyFile kf, string dbKey, string recoveryKey)
    {
        var rs = RandomNumberGenerator.GetBytes(16);
        kf.RecoverySalt = Convert.ToBase64String(rs);
        kf.RecoveryWrapped = Wrap(dbKey, Derive(NormalizeRecoveryKey(recoveryKey), rs, kf.Iterations));
        kf.RecoveryKeyExported = false;
    }

    /// <exception cref="CryptographicException">Kennwort falsch oder Schlüsseldatei beschädigt.</exception>
    public static string UnlockWithPassword(KeyFile kf, string password) =>
        Unwrap(kf.PasswordWrapped, Derive(password, Salt(kf.PasswordSalt), kf.Iterations), "Key_WrongPassword");

    /// <exception cref="CryptographicException">Wiederherstellungsschlüssel falsch oder Schlüsseldatei beschädigt.</exception>
    public static string UnlockWithRecoveryKey(KeyFile kf, string recoveryKey) =>
        Unwrap(kf.RecoveryWrapped, Derive(NormalizeRecoveryKey(recoveryKey), Salt(kf.RecoverySalt), kf.Iterations), "Key_WrongRecoveryKey");

    public static bool CheckRecoveryKey(KeyFile kf, string recoveryKey)
    {
        try { UnlockWithRecoveryKey(kf, recoveryKey); return true; }
        catch (CryptographicException) { return false; }
    }

    // ------------------------------------------------------------------ Datei

    /// <summary>
    /// Lädt die Schlüsseldatei. Ist sie beschädigt oder fehlt, wird die Sicherungskopie (<c>…json.backup</c>) verwendet.
    /// </summary>
    /// <exception cref="InvalidDataException">Beide Dateien fehlen oder sind unbrauchbar.</exception>
    public static KeyFile Load(string path)
    {
        Exception? first = null;
        foreach (var candidate in new[] { path, path + BackupSuffix })
        {
            if (!File.Exists(candidate)) continue;
            try { return Parse(File.ReadAllText(candidate)); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException) { first ??= ex; }
        }
        throw first as InvalidDataException ?? new InvalidDataException(CoreText.T("Key_Corrupt"), first);
    }

    /// <summary>Liest und prüft den Inhalt einer Schlüsseldatei (auch aus einem Notfallblatt).</summary>
    public static KeyFile Parse(string json)
    {
        KeyFile? kf;
        try { kf = JsonSerializer.Deserialize<KeyFile>(json); }
        catch (JsonException ex) { throw new InvalidDataException(CoreText.T("Key_Corrupt"), ex); }
        if (kf is null) throw new InvalidDataException(CoreText.T("Key_EmptyFile"));
        Validate(kf);
        return kf;
    }

    static void Validate(KeyFile kf)
    {
        bool ok = kf.Version == 1
            && kf.Iterations is >= MinIterations and <= MaxIterations
            && IsBase64(kf.PasswordSalt, 8, 64) && IsBase64(kf.RecoverySalt, 8, 64)
            && IsBase64(kf.PasswordWrapped, 29, 1024) && IsBase64(kf.RecoveryWrapped, 29, 1024);
        if (!ok) throw new InvalidDataException(CoreText.T("Key_Corrupt"));

        // Bewusst großzügige Grenzen: Eine zu strenge Prüfung darf niemanden aus seinem Archiv aussperren.
        static bool IsBase64(string s, int min, int max)
        {
            var buf = new byte[max + 4];
            return Convert.TryFromBase64String(s, buf, out var n) && n >= min && n <= max;
        }
    }

    public static string Serialize(KeyFile kf) => JsonSerializer.Serialize(kf, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Schreibt die Schlüsseldatei und eine identische Sicherungskopie, jeweils über eine temporäre Datei mit
    /// Durchschreiben auf die Platte – ein Stromausfall hinterlässt so nie eine leere oder halbe Datei.
    /// </summary>
    public static void Save(KeyFile kf, string path)
    {
        Validate(kf);
        var bytes = Encoding.UTF8.GetBytes(Serialize(kf));
        WriteDurable(path, bytes);
        WriteDurable(path + BackupSuffix, bytes);
    }

    static void WriteDurable(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Entfernt Schlüsseldatei und Sicherungskopie (nach dem Aufheben des Schutzes).</summary>
    public static void Delete(string path)
    {
        foreach (var f in new[] { path, path + BackupSuffix, path + ".tmp", path + BackupSuffix + ".tmp" })
            if (File.Exists(f)) File.Delete(f);
    }

    // ------------------------------------------------------------------ Krypto

    static byte[] Salt(string b64)
    {
        try { return Convert.FromBase64String(b64); }
        catch (FormatException ex) { throw new CryptographicException(CoreText.T("Key_Corrupt"), ex); }
    }

    static byte[] Derive(string secret, byte[] salt, int iterations)
    {
        if (iterations is < MinIterations or > MaxIterations) throw new CryptographicException(CoreText.T("Key_Corrupt"));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    static string Wrap(string plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    static string Unwrap(string wrapped, byte[] key, string wrongKeyText)
    {
        byte[] all;
        try { all = Convert.FromBase64String(wrapped); }
        catch (FormatException ex) { throw new CryptographicException(CoreText.T("Key_Corrupt"), ex); }
        if (all.Length < 29) throw new CryptographicException(CoreText.T("Key_Corrupt"));
        var plain = new byte[all.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        }
        catch (AuthenticationTagMismatchException ex) { throw new CryptographicException(CoreText.T(wrongKeyText), ex); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// Notfallblatt: eine Textdatei mit Wiederherstellungsschlüssel UND einer Kopie der Schlüsseldatei.
/// Damit lässt sich ein Archiv auch dann noch öffnen, wenn Kennwort und Schlüsseldatei verloren sind.
/// Die Kopie der Schlüsseldatei allein verrät nichts; das Blatt als Ganzes ist so geheim wie der Schlüssel selbst.
/// </summary>
public static class EmergencySheet
{
    const string Begin = "-----BEGIN MAILVAULT KEYFILE-----";
    const string End = "-----END MAILVAULT KEYFILE-----";

    public static string Build(string intro, KeyVault.KeyFile kf) =>
        intro.TrimEnd() + Environment.NewLine + Environment.NewLine + Begin + Environment.NewLine +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(kf)), Base64FormattingOptions.InsertLineBreaks) +
        Environment.NewLine + End + Environment.NewLine;

    /// <summary>Liest die Schlüsseldatei aus einem Notfallblatt oder einer reinen Schlüsseldatei (JSON).</summary>
    /// <exception cref="InvalidDataException">Kein gültiger Inhalt.</exception>
    public static KeyVault.KeyFile ReadKeyFile(string text)
    {
        var b = text.IndexOf(Begin, StringComparison.Ordinal);
        var e = text.IndexOf(End, StringComparison.Ordinal);
        if (b >= 0 && e > b)
        {
            var b64 = new string(text[(b + Begin.Length)..e].Where(c => !char.IsWhiteSpace(c)).ToArray());
            try { return KeyVault.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))); }
            catch (FormatException ex) { throw new InvalidDataException(CoreText.T("Key_Corrupt"), ex); }
        }
        return KeyVault.Parse(text);
    }
}

/// <summary>Die Archivdatei ist verschlüsselt, die Schlüsseldatei fehlt.</summary>
public sealed class KeyFileMissingException(string message) : IOException(message);
