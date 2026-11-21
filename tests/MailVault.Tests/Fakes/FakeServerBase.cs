using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailVault.Tests.Fakes;

/// <summary>Zeilenorientierter Lese-/Schreibzugriff auf eine Socket-Verbindung (Bytes, damit Literale exakt bleiben).</summary>
internal sealed class LineStream
{
    public static readonly Encoding Latin1 = Encoding.Latin1;
    readonly Stream _s;
    readonly byte[] _buf = new byte[16384];
    int _pos, _len;

    public LineStream(Stream s) => _s = s;

    async Task<bool> FillAsync(CancellationToken ct)
    {
        _pos = 0;
        _len = await _s.ReadAsync(_buf, ct);
        return _len > 0;
    }

    /// <summary>Liest eine Zeile ohne CRLF; null bei Verbindungsende.</summary>
    public async Task<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            if (_pos >= _len && !await FillAsync(ct)) return ms.Length > 0 ? ms.ToArray() : null;
            var i = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
            if (i < 0)
            {
                ms.Write(_buf, _pos, _len - _pos);
                _pos = _len;
                continue;
            }
            ms.Write(_buf, _pos, i - _pos);
            _pos = i + 1;
            var line = ms.ToArray();
            if (line.Length > 0 && line[^1] == '\r') Array.Resize(ref line, line.Length - 1);
            return line;
        }
    }

    public async Task<string?> ReadTextLineAsync(CancellationToken ct) =>
        await ReadLineAsync(ct) is { } b ? Latin1.GetString(b) : null;

    public async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
    {
        var result = new byte[n];
        var off = 0;
        while (off < n)
        {
            if (_pos >= _len && !await FillAsync(ct)) throw new EndOfStreamException();
            var take = Math.Min(n - off, _len - _pos);
            Buffer.BlockCopy(_buf, _pos, result, off, take);
            _pos += take;
            off += take;
        }
        return result;
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        await _s.WriteAsync(data, ct);
        await _s.FlushAsync(ct);
    }

    public Task WriteLineAsync(string line, CancellationToken ct) => WriteAsync(Latin1.GetBytes(line + "\r\n"), ct);
}

/// <summary>Gemeinsame Basis der Test-Server: TcpListener auf 127.0.0.1 mit zufälligem Port, Befehlsprotokoll.</summary>
public abstract class FakeServerBase : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    readonly List<TcpClient> _clients = new();
    readonly List<string> _log = new();
    readonly List<string> _mutating = new();

    public string Host => "127.0.0.1";
    public int Port { get; private set; }

    /// <summary>Alle empfangenen Befehlszeilen (Kennwörter maskiert), in Empfangsreihenfolge.</summary>
    public IReadOnlyList<string> Commands { get { lock (_log) return _log.ToList(); } }

    /// <summary>Alle empfangenen Befehle, die das Postfach verändern (könnten).</summary>
    public IReadOnlyList<string> MutatingCommands { get { lock (_log) return _mutating.ToList(); } }

    public void ClearLog()
    {
        lock (_log) { _log.Clear(); _mutating.Clear(); }
    }

    protected void Record(string line, bool mutating = false)
    {
        lock (_log)
        {
            _log.Add(line);
            if (mutating) _mutating.Add(line);
        }
    }

    protected void MarkMutating(string line)
    {
        lock (_log) _mutating.Add(line);
    }

    protected void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            lock (_clients) _clients.Add(client);
            _ = Task.Run(async () =>
            {
                try
                {
                    client.NoDelay = true;
                    await HandleClientAsync(new LineStream(client.GetStream()), _cts.Token);
                }
                catch { /* Verbindungsabbruch durch den Client */ }
                finally
                {
                    client.Dispose();
                    lock (_clients) _clients.Remove(client);
                }
            });
        }
    }

    internal abstract Task HandleClientAsync(LineStream io, CancellationToken ct);

    public virtual void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_clients) foreach (var c in _clients) c.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Ein Port, auf dem garantiert niemand lauscht (für "Server nicht erreichbar").</summary>
    public static int GetClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
