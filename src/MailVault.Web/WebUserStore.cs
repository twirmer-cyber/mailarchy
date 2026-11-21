using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailVault.Core;

using MailVault.Core.Localization;

namespace MailVault.Web;

/// <summary>Benutzerkonto für den Web-Zugriff auf das Archiv.</summary>
public sealed class WebUser
{
    /// <summary>Anmeldename (Groß-/Kleinschreibung wird ignoriert, eindeutig).</summary>
    public string UserName { get; set; } = "";
    /// <summary>PBKDF2-SHA256-Hash im Format <c>pbkdf2-sha256$v1$Iterationen$Salt$Hash</c> (Base64).</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>Administratoren sehen das gesamte Archiv.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>Freigegebene Archivordner; jeweils inklusive aller Unterordner.</summary>
    public List<long> AllowedFolderIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public DateTime? LastLoginUtc { get; set; }

    public WebUser Clone() => new()
    {
        UserName = UserName, PasswordHash = PasswordHash, IsAdmin = IsAdmin,
        AllowedFolderIds = new List<long>(AllowedFolderIds), Enabled = Enabled, LastLoginUtc = LastLoginUtc,
    };
}

/// <summary>
/// Speichert die Web-Benutzer in <c>webusers.json</c> im Datenverzeichnis (atomar geschrieben).
/// Kennwörter werden ausschließlich als gesalzener PBKDF2-SHA256-Hash abgelegt.
/// </summary>
public sealed class WebUserStore
{
    public const string FileName = "webusers.json";
    public const int MinPasswordLength = 8;
    const string HashScheme = "pbkdf2-sha256";
    const string HashVersion = "v1";
    internal const int Iterations = 600_000;
    const int SaltBytes = 16;
    const int HashBytes = 32;

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly string _path;
    readonly object _sync = new();
    List<WebUser> _users;
    // Dummy-Hash, damit unbekannte Benutzernamen gleich lange brauchen wie falsche Kennwörter.
    static readonly Lazy<string> DummyHash = new(() => HashPassword(Guid.NewGuid().ToString()));

    public WebUserStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
        _users = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<WebUser>>(File.ReadAllText(_path), JsonOptions) ?? new()
            : new();
    }

    /// <summary>Kopien aller Benutzer (Änderungen wirken erst über <see cref="Upsert"/>).</summary>
    public IReadOnlyList<WebUser> All
    {
        get { lock (_sync) return _users.Select(u => u.Clone()).ToList(); }
    }

    public WebUser? Find(string userName)
    {
        lock (_sync) return FindUnlocked(userName)?.Clone();
    }

    WebUser? FindUnlocked(string userName) =>
        _users.FirstOrDefault(u => string.Equals(u.UserName, userName?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Legt einen Benutzer an oder aktualisiert ihn (Schlüssel: UserName). Für neue Benutzer ist ein
    /// Kennwort Pflicht; bei bestehenden bleibt das Kennwort erhalten, wenn <paramref name="newPassword"/> null ist.
    /// Der Wert von <see cref="WebUser.PasswordHash"/> im übergebenen Objekt wird ignoriert.
    /// </summary>
    public void Upsert(WebUser user, string? newPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = (user.UserName ?? "").Trim();
        if (name.Length == 0 || name.Length > 64 || name.Any(char.IsControl))
            throw new ArgumentException(CoreText.T("Web_InvalidUserName"), nameof(user));
        if (newPassword is not null && newPassword.Length < MinPasswordLength)
            throw new ArgumentException(CoreText.F("Web_PasswordTooShort", MinPasswordLength), nameof(newPassword));

        lock (_sync)
        {
            var existing = FindUnlocked(name);
            if (existing is null && newPassword is null)
                throw new ArgumentException(CoreText.T("Web_PasswordRequired"), nameof(newPassword));
            var copy = user.Clone();
            copy.UserName = name;
            copy.AllowedFolderIds = copy.AllowedFolderIds.Distinct().ToList();
            copy.PasswordHash = newPassword is not null ? HashPassword(newPassword) : existing!.PasswordHash;
            if (existing is not null) _users[_users.IndexOf(existing)] = copy;
            else _users.Add(copy);
            Persist();
        }
    }

    public void Delete(string userName)
    {
        lock (_sync)
        {
            if (_users.RemoveAll(u => string.Equals(u.UserName, userName?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
                Persist();
        }
    }

    /// <summary>Prüft die Anmeldedaten. Liefert null bei unbekanntem/deaktiviertem Benutzer oder falschem Kennwort.</summary>
    public WebUser? Verify(string userName, string password)
    {
        if (string.IsNullOrEmpty(userName) || password is null) return null;
        WebUser? u;
        lock (_sync) u = FindUnlocked(userName)?.Clone();
        var ok = VerifyPassword(password, u?.PasswordHash ?? DummyHash.Value);
        if (u is null || !ok || !u.Enabled) return null;

        lock (_sync)
        {
            if (FindUnlocked(u.UserName) is { } live)
            {
                live.LastLoginUtc = u.LastLoginUtc = DateTime.UtcNow;
                try { Persist(); } catch (IOException) { } // Zeitstempel ist nicht kritisch
            }
        }
        return u;
    }

    /// <summary>
    /// Menge der für den Benutzer sichtbaren Ordner-IDs (freigegebene Ordner samt Unterordnern,
    /// nur tatsächlich existierende Ordner). Null bedeutet: Administrator, alles sichtbar.
    /// </summary>
    public static HashSet<long>? GetAllowedFolderIds(WebUser user, MailArchive archive) =>
        GetAllowedFolderIds(user, archive.GetFolderTree());

    internal static HashSet<long>? GetAllowedFolderIds(WebUser user, IEnumerable<ArchiveFolder> tree)
    {
        if (user.IsAdmin) return null;
        var granted = new HashSet<long>(user.AllowedFolderIds);
        var result = new HashSet<long>();
        void Walk(ArchiveFolder f, bool inherited)
        {
            var allowed = inherited || granted.Contains(f.Id);
            if (allowed) result.Add(f.Id);
            foreach (var c in f.Children) Walk(c, allowed);
        }
        foreach (var r in tree) Walk(r, false);
        return result;
    }

    public static bool CanAccessFolder(WebUser user, long folderId, MailArchive archive) =>
        user.IsAdmin || GetAllowedFolderIds(user, archive)!.Contains(folderId);

    // ------------------------------------------------------------------ Kennwort-Hashing

    internal static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', HashScheme, HashVersion, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    internal static bool VerifyPassword(string password, string stored)
    {
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 5 || parts[0] != HashScheme || parts[1] != HashVersion) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iter) || iter < 1 || iter > 10_000_000)
            return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[3]); expected = Convert.FromBase64String(parts[4]); }
        catch (FormatException) { return false; }
        if (expected.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    void Persist()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_users, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
