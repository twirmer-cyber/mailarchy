using System.Security.Cryptography;
using MailVault.Core;
using Xunit;

namespace MailVault.Tests;

/// <summary>
/// Härtung des Archivschutzes: Schlüsseldatei doppelt und prüfbar, Notfallblatt, neuer Wiederherstellungsschlüssel,
/// Aufräumen nach Abstürzen mitten im Ver-/Entschlüsseln. Leitfrage jedes Tests: Kommt der Benutzer an seine E-Mails?
/// </summary>
public class KeySafetyTests
{
    const string Pw = "Kiesel Laterne trotzig";

    static string ProtectedArchive(TempDir tmp, out string recovery)
    {
        using var archive = MailArchive.Open(tmp.Path);
        var f = archive.EnsureFolderPath(new[] { "Posteingang" });
        archive.StoreMessage(f, ArchiveTests.MakeMessage("Steuer 2025", "Belege anbei"));
        recovery = archive.EnableProtection(Pw);
        return tmp.Path;
    }

    static string Keys(string dir) => Path.Combine(dir, MailArchive.KeyFileName);

    [Fact]
    public void KeyFile_IsWrittenTwice_AndBackupIsUsedWhenMainIsDamaged()
    {
        using var tmp = new TempDir();
        var dir = ProtectedArchive(tmp, out _);
        Assert.True(File.Exists(Keys(dir) + KeyVault.BackupSuffix));

        File.WriteAllText(Keys(dir), "{ kaputt");
        using (var a = MailArchive.Open(dir, Pw)) Assert.Equal(1, a.GetStats().MessageCount);

        File.Delete(Keys(dir));
        Assert.True(MailArchive.IsProtectedArchive(dir));
        using (var a = MailArchive.Open(dir, Pw)) Assert.Equal(1, a.GetStats().MessageCount);
    }

    [Fact]
    public void BothKeyFilesDamaged_GivesClearError_NotWrongPassword()
    {
        using var tmp = new TempDir();
        var dir = ProtectedArchive(tmp, out _);
        File.WriteAllText(Keys(dir), "{}");
        File.WriteAllText(Keys(dir) + KeyVault.BackupSuffix, "nonsense");
        Assert.Throws<InvalidDataException>(() => MailArchive.Open(dir, Pw));
    }

    [Fact]
    public void MissingKeyFile_IsDetected_AndRestoredFromEmergencySheet()
    {
        using var tmp = new TempDir();
        var dir = ProtectedArchive(tmp, out var recovery);
        string sheet;
        using (var a = MailArchive.Open(dir, Pw))
            sheet = EmergencySheet.Build("Wiederherstellungsschlüssel:\n" + recovery, a.CurrentKeyFile!);

        KeyVault.Delete(Keys(dir));
        Assert.False(MailArchive.IsProtectedArchive(dir));
        Assert.True(MailArchive.IsKeyFileMissing(dir));
        Assert.Throws<KeyFileMissingException>(() => MailArchive.Open(dir)); // nie als leeres Archiv überschreiben

        var kf = EmergencySheet.ReadKeyFile(sheet);
        Assert.Throws<CryptographicException>(() => MailArchive.RestoreKeyFile(dir, kf, "AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA", "neu-neu-neu"));
        using (var a = MailArchive.RestoreKeyFile(dir, kf, recovery, "Neues Kennwort hier"))
            Assert.Equal(1, a.GetStats().MessageCount);
        using (var a = MailArchive.Open(dir, "Neues Kennwort hier")) Assert.True(a.IsProtected);
    }

    [Fact]
    public void EmergencySheet_FromOtherArchive_IsRejected()
    {
        using var t1 = new TempDir();
        using var t2 = new TempDir();
        ProtectedArchive(t1, out var r1);
        var dir2 = ProtectedArchive(t2, out _);
        var kf1 = KeyVault.Load(Keys(t1.Path));
        KeyVault.Delete(Keys(dir2));
        var ex = Assert.Throws<CryptographicException>(() => MailArchive.RestoreKeyFile(dir2, kf1, r1, "egal-egal-egal"));
        Assert.False(File.Exists(Keys(dir2))); // falsche Schlüsseldatei wird nicht übernommen
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void NewRecoveryKey_ReplacesOld_AndCheckWorks()
    {
        using var tmp = new TempDir();
        var dir = ProtectedArchive(tmp, out var oldKey);
        using var a = MailArchive.Open(dir, Pw);
        Assert.True(a.CheckRecoveryKey(oldKey));
        Assert.Throws<CryptographicException>(() => a.CreateNewRecoveryKey("falsch"));
        var newKey = a.CreateNewRecoveryKey(Pw);
        Assert.False(a.CheckRecoveryKey(oldKey));
        Assert.True(a.CheckRecoveryKey(newKey.ToLowerInvariant().Replace("-", " ")));
        Assert.False(a.RecoveryKeyExported);
        Assert.True(a.CheckPassword(Pw));
        Assert.False(a.CheckPassword("nein"));
    }

    [Fact]
    public void CrashAfterSwap_WhileEncrypting_PendingKeyFileIsPromoted()
    {
        using var tmp = new TempDir();
        var dir = ProtectedArchive(tmp, out _);
        // Zustand simulieren: Datenbank schon verschlüsselt, endgültige Schlüsseldatei noch nicht geschrieben.
        File.Move(Keys(dir), Keys(dir) + ".pending");
        File.Delete(Keys(dir) + KeyVault.BackupSuffix);
        using var a = MailArchive.Open(dir, Pw);
        Assert.Equal(1, a.GetStats().MessageCount);
        Assert.False(File.Exists(Keys(dir) + ".pending"));
    }

    [Fact]
    public void CrashBetweenFileMoves_RestoresDatabase_AndWipesPlainCopy()
    {
        using var tmp = new TempDir();
        var dir = ProtectedArchive(tmp, out _);
        var db = Path.Combine(dir, MailArchive.DbFileName);
        File.Move(db, db + ".new");                       // archive.db fehlt, vollständige neue Datei liegt daneben
        File.WriteAllText(db + ".bak", "SQLite format 3\0 alte unverschlüsselte Kopie");
        using (var a = MailArchive.Open(dir, Pw)) Assert.Equal(1, a.GetStats().MessageCount);
        Assert.False(File.Exists(db + ".bak"));
        Assert.False(File.Exists(db + ".new"));
    }

    [Fact]
    public void CrashWhileDecrypting_AfterSwap_ArchiveOpensUnprotected()
    {
        using var tmp = new TempDir();
        var dir = tmp.Path;
        using (var a = MailArchive.Open(dir))
            a.StoreMessage(a.EnsureFolderPath(new[] { "X" }), ArchiveTests.MakeMessage("A", "B"));
        // Datenbank unverschlüsselt, aber Schlüsseldatei und verschlüsselte Altkopie noch da.
        var kf = KeyVault.Create(KeyVault.NewDatabaseKey(), Pw, KeyVault.NewRecoveryKey());
        KeyVault.Save(kf, Keys(dir));
        File.WriteAllText(Path.Combine(dir, MailArchive.DbFileName + ".bak"), "verschlüsselte Altkopie");
        Assert.False(MailArchive.IsProtectedArchive(dir));
        using var b = MailArchive.Open(dir);
        Assert.False(b.IsProtected);
        Assert.Equal(1, b.GetStats().MessageCount);
    }

    [Fact]
    public void ManipulatedIterations_AreRejected()
    {
        var kf = KeyVault.Create(KeyVault.NewDatabaseKey(), Pw, KeyVault.NewRecoveryKey());
        kf.Iterations = 1;
        Assert.Throws<InvalidDataException>(() => KeyVault.Parse(KeyVault.Serialize(kf)));
        Assert.Throws<CryptographicException>(() => KeyVault.UnlockWithPassword(kf, Pw));
    }

    [Theory]
    [InlineData("passwort", PasswordStrength.Level.VeryWeak)]
    [InlineData("12345678", PasswordStrength.Level.VeryWeak)]
    [InlineData("Passwort2024!", PasswordStrength.Level.VeryWeak)]
    [InlineData("aaaaaaaaaaaaaaaa", PasswordStrength.Level.VeryWeak)]
    [InlineData("kiesel laterne trotzig ofen mai", PasswordStrength.Level.VeryStrong)]
    [InlineData("Tz9#qLm2", PasswordStrength.Level.Fair)]      // 8 zufällige Zeichen ≈ 52 Bit
    public void PasswordStrength_RatesSensibly(string pw, PasswordStrength.Level expected) =>
        Assert.Equal(expected, PasswordStrength.Rate(pw));
}
