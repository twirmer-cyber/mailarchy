using System.Security.Cryptography;
using MailKit.Net.Pop3;
using MailKit.Security;
using MailVault.Core;
using MailVault.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

/// <summary>Zusage "Nie etwas löschen vom Postfach" für POP3: es wird nie DELE gesendet.</summary>
public class Pop3ReadOnlyTests
{
    readonly ITestOutputHelper _out;
    public Pop3ReadOnlyTests(ITestOutputHelper output) => _out = output;

    const int N = 5;
    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;
    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    static FakePop3Server CreateServer()
    {
        var s = new FakePop3Server();
        for (var i = 1; i <= N; i++)
            s.AddMessage(ImapReadOnlyTests.Msg($"POP3-Nachricht {i}",
                i == 3 ? ".Zeile beginnt mit Punkt\r\n..zwei Punkte\r\n.\r\nEnde" : $"Inhalt {i}", i));
        return s;
    }

    static ArchiveProfile Profile(FakePop3Server s, string? password = null) => new()
    {
        Name = "Test-POP3",
        Type = ProfileType.Pop3,
        Host = s.Host,
        Port = s.Port,
        Security = ConnectionSecurity.None,
        UserName = s.UserName,
        Password = password ?? s.Password,
    };

    static void AssertReadOnlyProtocol(FakePop3Server s)
    {
        Assert.Empty(s.MutatingCommands);
        Assert.DoesNotContain(s.Commands, c => c.StartsWith("DELE", StringComparison.OrdinalIgnoreCase));
        string[] allowed = { "CAPA", "USER", "PASS", "STAT", "LIST", "UIDL", "TOP", "RETR", "NOOP", "QUIT" };
        Assert.All(s.Commands, c => Assert.Contains(c.Split(' ')[0].ToUpperInvariant(), allowed));
    }

    [Fact]
    public async Task Archives_all_messages_byte_identical_and_leaves_server_untouched()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        var result = await ImportJob.RunAsync(archive, Profile(server), null, Timeout());
        _out.WriteLine("--- POP3 Erstlauf");
        foreach (var c in server.Commands) _out.WriteLine(c);

        Assert.Equal(0, result.Errors);
        Assert.Equal(N, result.Archived);
        var inbox = archive.EnsureFolderPath(new[] { server.UserName, "INBOX" });
        Assert.Equal(server.Messages.Select(m => Hash(m.Raw)).OrderBy(h => h),
                     archive.GetMessages(inbox).Select(m => Hash(archive.GetRawMessage(m.Id))).OrderBy(h => h));

        Assert.Contains(server.Commands, c => c == "QUIT"); // Sitzung regulär beendet → ein DELE wäre jetzt wirksam geworden
        Assert.Equal(N, server.Messages.Count);
        Assert.Equal(before, server.Snapshot());
        AssertReadOnlyProtocol(server);
    }

    [Fact]
    public async Task Second_run_archives_nothing()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        await ImportJob.RunAsync(archive, Profile(server), null, Timeout());
        server.ClearLog();

        var second = await ImportJob.RunAsync(archive, Profile(server), null, Timeout());
        _out.WriteLine("--- POP3 Zweitlauf");
        foreach (var c in server.Commands) _out.WriteLine(c);

        Assert.Equal(0, second.Archived);
        Assert.Equal(N, second.AlreadyArchived);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("RETR", StringComparison.Ordinal));
        Assert.Equal(before, server.Snapshot());
        AssertReadOnlyProtocol(server);
    }

    [Fact]
    public async Task Wrong_password_throws_and_nothing_is_touched()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        await Assert.ThrowsAsync<AuthenticationException>(() => ImportJob.RunAsync(archive, Profile(server, "falsch"), null, Timeout()));

        Assert.Equal(before, server.Snapshot());
        Assert.Empty(server.MutatingCommands);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("RETR") || c.StartsWith("TOP"));
        Assert.Equal(0, archive.GetStats().MessageCount);
    }

    [Fact]
    public async Task Control_fake_really_deletes_on_DELE_and_QUIT()
    {
        using var server = CreateServer();
        var ct = Timeout();
        using (var client = new Pop3Client())
        {
            await client.ConnectAsync(server.Host, server.Port, SecureSocketOptions.None, ct);
            await client.AuthenticateAsync(server.UserName, server.Password, ct);
            await client.DeleteMessageAsync(0, ct);
            await client.DisconnectAsync(true, ct);
        }
        Assert.Equal(N - 1, server.Messages.Count);
        Assert.Single(server.MutatingCommands, c => c.StartsWith("DELE"));
    }
}
