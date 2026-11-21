using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MailVault.Web;

/// <summary>In-Memory-Sitzungen mit zufälligem 256-Bit-Token und gleitendem Ablauf.</summary>
internal sealed class SessionStore
{
    internal sealed class Session
    {
        public required string UserName { get; init; }
        /// <summary>Hash zum Anmeldezeitpunkt – ändert sich das Kennwort, wird die Sitzung ungültig.</summary>
        public required string PasswordHash { get; init; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime ExpiresUtc { get; set; }
    }

    public static readonly TimeSpan SlidingExpiry = TimeSpan.FromHours(12);
    public static readonly TimeSpan AbsoluteExpiry = TimeSpan.FromDays(7);

    readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public string Create(string userName, string passwordHash)
    {
        Purge();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new Session { UserName = userName, PasswordHash = passwordHash, ExpiresUtc = DateTime.UtcNow + SlidingExpiry };
        return token;
    }

    public Session? Get(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100 || !_sessions.TryGetValue(token, out var s)) return null;
        var now = DateTime.UtcNow;
        if (now >= s.ExpiresUtc || now - s.CreatedUtc >= AbsoluteExpiry)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        s.ExpiresUtc = now + SlidingExpiry;
        return s;
    }

    public void Remove(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    public void RemoveUser(string userName)
    {
        foreach (var kv in _sessions)
            if (string.Equals(kv.Value.UserName, userName, StringComparison.OrdinalIgnoreCase)) _sessions.TryRemove(kv.Key, out _);
    }

    public void Clear() => _sessions.Clear();

    void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions)
            if (now >= kv.Value.ExpiresUtc || now - kv.Value.CreatedUtc >= AbsoluteExpiry) _sessions.TryRemove(kv.Key, out _);
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Bremst Anmeldeversuche: nach 5 Fehlversuchen je IP+Benutzer exponentiell wachsende Sperre
/// (30 s, 60 s, … bis 15 min); zusätzlich Sperre je IP nach 20 Fehlversuchen (Benutzernamen-Raten).
/// </summary>
internal sealed class LoginThrottle
{
    internal const int UserThreshold = 5;
    internal const int IpThreshold = 20;
    static readonly TimeSpan BaseLock = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    static readonly TimeSpan Forget = TimeSpan.FromHours(1);

    sealed class Entry
    {
        public int Failures;
        public DateTime LastFailureUtc;
        public DateTime LockedUntilUtc;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    static string UserKey(string ip, string user) => "u|" + ip + "|" + user.Trim().ToLowerInvariant();
    static string IpKey(string ip) => "i|" + ip;

    /// <summary>Liefert die verbleibende Sperrzeit oder null, wenn ein Versuch erlaubt ist.</summary>
    public TimeSpan? GetLockout(string ip, string user)
    {
        var now = DateTime.UtcNow;
        TimeSpan? max = null;
        foreach (var key in new[] { UserKey(ip, user), IpKey(ip) })
        {
            if (!_entries.TryGetValue(key, out var e)) continue;
            lock (e)
                if (e.LockedUntilUtc > now && (max is null || e.LockedUntilUtc - now > max)) max = e.LockedUntilUtc - now;
        }
        return max;
    }

    public void RegisterFailure(string ip, string user)
    {
        if (_entries.Count > 10_000) Purge();
        Bump(UserKey(ip, user), UserThreshold);
        Bump(IpKey(ip), IpThreshold);
    }

    public void RegisterSuccess(string ip, string user) => _entries.TryRemove(UserKey(ip, user), out _);

    void Bump(string key, int threshold)
    {
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e)
        {
            var now = DateTime.UtcNow;
            if (now - e.LastFailureUtc > Forget) e.Failures = 0;
            e.Failures++;
            e.LastFailureUtc = now;
            if (e.Failures >= threshold)
            {
                var factor = Math.Pow(2, Math.Min(e.Failures - threshold, 10));
                var lockFor = TimeSpan.FromTicks((long)Math.Min(BaseLock.Ticks * factor, MaxLock.Ticks));
                e.LockedUntilUtc = now + lockFor;
            }
        }
    }

    void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _entries)
            if (now - kv.Value.LastFailureUtc > Forget && kv.Value.LockedUntilUtc < now) _entries.TryRemove(kv.Key, out _);
    }
}
