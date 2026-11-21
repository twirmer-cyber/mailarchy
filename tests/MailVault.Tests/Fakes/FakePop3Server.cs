using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MailVault.Tests.Fakes;

/// <summary>
/// Minimaler POP3-Server (RFC 1939/2449) im Prozess. DELE markiert, QUIT entfernt die markierten Nachrichten
/// wirklich – genau wie ein echter Server. Ein DELE des Clients wäre damit im Postfachzustand sichtbar.
/// </summary>
public sealed class FakePop3Server : FakeServerBase
{
    public sealed record Message(string Uid, byte[] Raw);

    readonly object _sync = new();
    readonly List<Message> _messages = new();
    int _nextUid = 1;

    public string UserName { get; set; } = "anna@example.org";
    public string Password { get; set; } = "Geheim#123";

    public FakePop3Server() => Start();

    public void AddMessage(byte[] raw)
    {
        lock (_sync) _messages.Add(new Message("uid-" + _nextUid++.ToString("0000", CultureInfo.InvariantCulture), raw));
    }

    public IReadOnlyList<Message> Messages { get { lock (_sync) return _messages.ToList(); } }

    public string Snapshot()
    {
        lock (_sync)
            return string.Join("\n", _messages.Select(m => m.Uid + " " + Convert.ToHexString(SHA256.HashData(m.Raw))));
    }

    static byte[] DotStuff(byte[] data)
    {
        var ms = new MemoryStream();
        var lineStart = true;
        foreach (var b in data)
        {
            if (lineStart && b == '.') ms.WriteByte((byte)'.');
            ms.WriteByte(b);
            lineStart = b == '\n';
        }
        if (!lineStart) ms.Write("\r\n"u8);
        ms.Write(".\r\n"u8);
        return ms.ToArray();
    }

    static int HeaderEnd(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == '\r' && raw[1] == '\n') return 2;
        for (var i = 0; i + 3 < raw.Length; i++)
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n') return i + 4;
        return raw.Length;
    }

    static byte[] Top(byte[] raw, int lines)
    {
        var he = HeaderEnd(raw);
        var end = he;
        for (var n = 0; n < lines && end < raw.Length; n++)
        {
            var nl = Array.IndexOf(raw, (byte)'\n', end);
            end = nl < 0 ? raw.Length : nl + 1;
        }
        return raw[..end];
    }

    internal override async Task HandleClientAsync(LineStream io, CancellationToken ct)
    {
        string? user = null;
        List<Message>? drop = null; // Maildrop dieser Sitzung (Nummerierung bleibt während der Sitzung stabil)
        var deleted = new HashSet<int>();
        async Task Ok(string text = "") => await io.WriteLineAsync("+OK" + (text.Length > 0 ? " " + text : ""), ct);
        async Task Err(string text) => await io.WriteLineAsync("-ERR " + text, ct);

        await Ok("FakePOP3 ready");
        while (!ct.IsCancellationRequested)
        {
            var line = await io.ReadTextLineAsync(ct);
            if (line is null) return;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var verb = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
            Record(verb == "PASS" ? "PASS ****" : line, verb == "DELE");

            bool TryMsg(int argIndex, out int n, out Message m)
            {
                n = 0; m = null!;
                if (parts.Length <= argIndex || !int.TryParse(parts[argIndex], out n)) return false;
                if (drop is null || n < 1 || n > drop.Count || deleted.Contains(n)) return false;
                m = drop[n - 1];
                return true;
            }

            switch (verb)
            {
                case "CAPA":
                    await Ok("Capability list follows");
                    foreach (var c in new[] { "TOP", "UIDL", "USER", "RESP-CODES" }) await io.WriteLineAsync(c, ct);
                    await io.WriteLineAsync(".", ct);
                    continue;
                case "QUIT":
                    if (drop is not null)
                        lock (_sync) foreach (var n in deleted) _messages.Remove(drop[n - 1]);
                    await Ok("FakePOP3 signing off");
                    return;
                case "NOOP":
                    if (drop is null) await Err("Not authenticated"); else await Ok();
                    continue;
            }

            if (drop is null)
            {
                switch (verb)
                {
                    case "USER":
                        user = parts.Length > 1 ? line[5..] : "";
                        await Ok("Send password");
                        break;
                    case "PASS":
                        var pass = line.Length > 5 ? line[5..] : "";
                        if (user == UserName && pass == Password)
                        {
                            lock (_sync) drop = _messages.ToList();
                            await Ok("Mailbox locked and ready");
                        }
                        else
                        {
                            user = null;
                            await Err("[AUTH] Invalid credentials");
                        }
                        break;
                    default:
                        await Err("Not authenticated");
                        break;
                }
                continue;
            }

            var live = Enumerable.Range(1, drop.Count).Where(n => !deleted.Contains(n)).ToList();
            switch (verb)
            {
                case "STAT":
                    await Ok($"{live.Count} {live.Sum(n => (long)drop[n - 1].Raw.Length)}");
                    break;
                case "LIST":
                case "UIDL":
                {
                    string Item(int n) => verb == "LIST" ? $"{n} {drop[n - 1].Raw.Length}" : $"{n} {drop[n - 1].Uid}";
                    if (parts.Length > 1)
                    {
                        if (TryMsg(1, out var n, out _)) await Ok(Item(n)); else await Err("No such message");
                        break;
                    }
                    await Ok($"{live.Count} messages");
                    foreach (var n in live) await io.WriteLineAsync(Item(n), ct);
                    await io.WriteLineAsync(".", ct);
                    break;
                }
                case "RETR":
                    if (!TryMsg(1, out _, out var rm)) { await Err("No such message"); break; }
                    await Ok($"{rm.Raw.Length} octets");
                    await io.WriteAsync(DotStuff(rm.Raw), ct);
                    break;
                case "TOP":
                    if (!TryMsg(1, out _, out var tm) || parts.Length < 3 || !int.TryParse(parts[2], out var k) || k < 0)
                    {
                        await Err("No such message");
                        break;
                    }
                    await Ok();
                    await io.WriteAsync(DotStuff(Top(tm.Raw, k)), ct);
                    break;
                case "DELE":
                    if (!TryMsg(1, out var dn, out _)) { await Err("No such message"); break; }
                    deleted.Add(dn);
                    await Ok($"Message {dn} deleted");
                    break;
                case "RSET":
                    deleted.Clear();
                    await Ok();
                    break;
                default:
                    await Err("Unknown command");
                    break;
            }
        }
    }
}
