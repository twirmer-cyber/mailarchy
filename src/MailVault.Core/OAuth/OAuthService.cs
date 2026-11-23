using System.Text.Json;

using MailVault.Core.Localization;

namespace MailVault.Core.OAuth;

/// <summary>Gespeicherte Anmeldung eines Profils (ohne Zugriffstoken; das wird bei Bedarf neu geholt).</summary>
public sealed class OAuthAccount
{
    public MailAuth Provider { get; set; }
    public string Account { get; set; } = "";
    /// <summary>Refresh-Token, mit Windows-DPAPI für dieses Benutzerkonto verschlüsselt.</summary>
    public string ProtectedRefreshToken { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// oauth-tokens.json im Datenordner. Die Refresh-Tokens sind per DPAPI an das Windows-Benutzerkonto gebunden: Wird der
/// Datenordner auf einen anderen PC kopiert, ist dort eine neue Anmeldung nötig (wie bei gespeicherten Kennwörtern).
/// </summary>
public sealed class OAuthTokenStore
{
    public const string FileName = "oauth-tokens.json";
    const string Purpose = "OAuth";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    readonly string _path;
    readonly object _sync = new();
    Dictionary<Guid, OAuthAccount> _items;

    public OAuthTokenStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, FileName);
        _items = Load();
    }

    Dictionary<Guid, OAuthAccount> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<Guid, OAuthAccount>>(File.ReadAllText(_path), Json) ?? new()
                : new();
        }
        catch (JsonException) { return new(); } // beschädigt: dann ist eben eine neue Anmeldung nötig
    }

    public OAuthAccount? Get(Guid profileId)
    {
        lock (_sync) return _items.TryGetValue(profileId, out var a) ? Copy(a) : null;
    }

    public string? GetRefreshToken(Guid profileId)
    {
        lock (_sync)
            return _items.TryGetValue(profileId, out var a) && Secret.Unprotect(a.ProtectedRefreshToken, Purpose) is { Length: > 0 } t ? t : null;
    }

    public void Set(Guid profileId, MailAuth provider, string account, string refreshToken)
    {
        lock (_sync)
        {
            _items[profileId] = new OAuthAccount
            {
                Provider = provider,
                Account = account,
                ProtectedRefreshToken = Secret.Protect(refreshToken, Purpose)!,
                UpdatedUtc = DateTime.UtcNow,
            };
            SaveUnlocked();
        }
    }

    /// <summary>Stellt einen früheren Stand wieder her (null = Eintrag entfernen), z. B. wenn der Profileditor abgebrochen wird.</summary>
    public void Restore(Guid profileId, OAuthAccount? previous)
    {
        lock (_sync)
        {
            if (previous is null) _items.Remove(profileId); else _items[profileId] = Copy(previous);
            SaveUnlocked();
        }
    }

    public void Remove(Guid profileId)
    {
        lock (_sync)
        {
            if (_items.Remove(profileId)) SaveUnlocked();
        }
    }

    void SaveUnlocked()
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, _items, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    static OAuthAccount Copy(OAuthAccount a) => new()
    { Provider = a.Provider, Account = a.Account, ProtectedRefreshToken = a.ProtectedRefreshToken, UpdatedUtc = a.UpdatedUtc };
}

/// <summary>
/// Liefert Zugriffstokens für die Quellen. Ein Token gilt etwa eine Stunde und wird im Speicher gehalten; danach wird es
/// mit dem gespeicherten Refresh-Token erneuert. Ein vom Anbieter neu ausgegebenes Refresh-Token wird sofort gespeichert.
/// </summary>
public sealed class OAuthService
{
    /// <summary>Von der App beim Start gesetzt (Datenordner des geöffneten Archivs).</summary>
    public static OAuthService? Current { get; set; }

    readonly OAuthClient _client;
    readonly Func<MailAuth, OAuthProvider?> _providers;
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Dictionary<Guid, (string Token, DateTime ExpiresUtc)> _cache = new();

    public OAuthTokenStore Store { get; }

    /// <param name="providers">Anbieter je Verfahren; Standard sind die eingerichteten <see cref="OAuthProviders"/> (Tests setzen eigene).</param>
    public OAuthService(OAuthTokenStore store, HttpMessageHandler? handler = null, Func<MailAuth, OAuthProvider?>? providers = null)
    {
        Store = store;
        _client = new OAuthClient(handler);
        _providers = providers ?? OAuthProviders.For;
    }

    public Task<OAuthTokens> SignInAsync(MailAuth kind, string? loginHint, Action<string> openBrowser, CancellationToken ct) =>
        _client.SignInAsync(_providers(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind)), loginHint, openBrowser, ct);

    /// <summary>Speichert eine erfolgreiche Anmeldung für ein Profil (das Zugriffstoken kann gleich genutzt werden).</summary>
    public void Remember(Guid profileId, MailAuth kind, OAuthTokens tokens)
    {
        Store.Set(profileId, kind, tokens.Account ?? "", tokens.RefreshToken!);
        lock (_cache) _cache[profileId] = (tokens.AccessToken, tokens.ExpiresUtc);
    }

    public void Forget(Guid profileId)
    {
        Store.Remove(profileId);
        lock (_cache) _cache.Remove(profileId);
    }

    /// <summary>Verwirft zwischengespeicherte Zugriffstokens (nach einem Wechsel der Anmeldung im Editor).</summary>
    public void InvalidateCache(Guid profileId)
    {
        lock (_cache) _cache.Remove(profileId);
    }

    public async Task<string> GetAccessTokenAsync(ArchiveProfile profile, CancellationToken ct)
    {
        var provider = _providers(profile.AuthMethod) ?? throw new InvalidOperationException("Profil verwendet kein OAuth");
        await _lock.WaitAsync(ct);
        try
        {
            lock (_cache)
                if (_cache.TryGetValue(profile.Id, out var c) && c.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return c.Token;

            var account = Store.Get(profile.Id);
            var refresh = Store.GetRefreshToken(profile.Id);
            if (account is null || refresh is null || account.Provider != profile.AuthMethod)
                throw new OAuthException(CoreText.F("OAuth_SignInRequired", provider.DisplayName), reauthRequired: true);

            var t = await _client.RefreshAsync(provider, refresh, ct);
            if (t.RefreshToken is { } newRefresh && newRefresh != refresh)
                Store.Set(profile.Id, profile.AuthMethod, account.Account, newRefresh);
            lock (_cache) _cache[profile.Id] = (t.AccessToken, t.ExpiresUtc);
            return t.AccessToken;
        }
        finally { _lock.Release(); }
    }

    internal static OAuthService Require() =>
        Current ?? throw new OAuthException(CoreText.T("OAuth_Unavailable"));
}
