using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MailVault.Core;
using MailVault.Core.Sources;
using MailVault.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

/// <summary>
/// Zusage "Nie etwas löschen vom Postfach": Archivierung gegen einen echten (im Prozess laufenden) IMAP-Server.
/// Geprüft wird der Serverzustand vorher/nachher und jeder einzelne Befehl, den MailVault sendet.
/// </summary>
public class ImapReadOnlyTests
{
    readonly ITestOutputHelper _out;
    public ImapReadOnlyTests(ITestOutputHelper output) => _out = output;

    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    internal static byte[] Msg(string subject, string body, int day, string from = "Bernd <bernd@example.org>")
    {
        var raw = ArchiveTests.MakeMessage(subject, body, from: from, to: "anna@example.org",
            date: new DateTimeOffset(2025, 1, day, 9, 15, 0, TimeSpan.FromHours(1)));
        Assert.True(raw.Length > 2 && raw[^2] == '\r' && raw[^1] == '\n', "Testnachricht muss mit CRLF enden");
        return raw;
    }

    /// <summary>INBOX (3), Sent (2), Projekte (leer), Projekte/2025 (2), Trash (1).</summary>
    static FakeImapServer CreateServer()
    {
        var s = new FakeImapServer();
        s.AddFolder("Sent", "\\Sent");
        s.AddFolder("Projekte");
        s.AddFolder("Projekte/2025");
        s.AddFolder("Trash", "\\Trash");
        s.AddMessage("INBOX", Msg("Ungelesen: Angebot Fenster", "Hallo Anna, anbei das Angebot.", 2));
        s.AddMessage("INBOX", Msg("Gelesen und beantwortet", "Danke für die Rückmeldung.", 3), new[] { "\\Seen", "\\Answered" });
        s.AddMessage("INBOX", Msg("Ungelesen und markiert", "Wichtig!\r\n.Zeile mit Punkt am Anfang", 4), new[] { "\\Flagged" });
        s.AddMessage("Sent", Msg("Re: Angebot", "Passt so.", 5, from: "Anna <anna@example.org>"), new[] { "\\Seen" });
        s.AddMessage("Sent", Msg("Urlaub", "Bin ab Montag weg.", 6, from: "Anna <anna@example.org>"), new[] { "\\Seen" });
        s.AddMessage("Projekte/2025", Msg("Projektplan Q1", "Meilensteine", 7));
        s.AddMessage("Projekte/2025", Msg("Statusbericht", "Alles im Plan", 8), new[] { "\\Seen" });
        s.AddMessage("Trash", Msg("Alter Newsletter", "Weg damit", 9), new[] { "\\Seen", "\\Deleted" });
        return s;
    }

    static ArchiveProfile Profile(FakeImapServer s, string? password = null, int? port = null) => new()
    {
        Name = "Test-IMAP",
        Type = ProfileType.Imap,
        Host = s.Host,
        Port = port ?? s.Port,
        Security = ConnectionSecurity.None,
        UserName = s.UserName,
        Password = password ?? s.Password,
    };

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    static string Verb(string commandLine)
    {
        var p = commandLine.Split(' ');
        return p.Length > 2 && p[1].Equals("UID", StringComparison.OrdinalIgnoreCase) ? "UID " + p[2].ToUpperInvariant() : p.Length > 1 ? p[1].ToUpperInvariant() : "";
    }

    void Dump(string title, IEnumerable<string> commands)
    {
        _out.WriteLine("--- " + title);
        foreach (var c in commands) _out.WriteLine(c);
    }

    /// <summary>Die Kernzusicherung: nur lesende Befehle, nur EXAMINE, nur PEEK.</summary>
    static void AssertReadOnlyProtocol(FakeImapServer s)
    {
        Assert.Empty(s.MutatingCommands);
        var verbs = s.Commands.Select(Verb).ToList();
        Assert.DoesNotContain("SELECT", verbs);
        string[] allowed = { "CAPABILITY", "AUTHENTICATE", "LOGIN", "NAMESPACE", "LIST", "LSUB", "ID", "EXAMINE", "UID SEARCH",
                             "SEARCH", "UID FETCH", "FETCH", "UNSELECT", "CLOSE", "NOOP", "LOGOUT", "STATUS", "ENABLE" };
        Assert.All(verbs, v => Assert.Contains(v, allowed));
        foreach (var fetch in s.Commands.Where(c => Verb(c).EndsWith("FETCH")))
        {
            var items = fetch.ToUpperInvariant();
            Assert.DoesNotContain("BODY[", items);              // nur BODY.PEEK[...]
            Assert.DoesNotMatch(@"\bRFC822(\.TEXT)?\b(?!\.SIZE|\.HEADER)", items);
        }
    }

    [Fact]
    public async Task Archives_all_folders_byte_identical_and_skips_trash()
    {
        using var server = CreateServer();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var sw = Stopwatch.StartNew();

        var result = await ImportJob.RunAsync(archive, Profile(server), null, Timeout());
        Dump($"IMAP Erstlauf ({sw.ElapsedMilliseconds} ms)", server.Commands);

        Assert.False(result.Cancelled);
        Assert.Equal(0, result.Errors);
        Assert.Equal(7, result.Archived);
        Assert.Equal(0, result.AlreadyArchived);

        var root = Assert.Single(archive.GetFolderTree());
        Assert.Equal(server.UserName, root.Name);
        Assert.Equal(new[] { "INBOX", "Projekte", "Sent" }, root.Children.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("2025", Assert.Single(root.Children.Single(c => c.Name == "Projekte").Children).Name);
        Assert.DoesNotContain(root.Children, c => c.Name == "Trash");
        Assert.Equal(7, root.TotalCount);

        foreach (var (serverFolder, archivePath) in new[]
                 {
                     ("INBOX", new[] { server.UserName, "INBOX" }),
                     ("Sent", new[] { server.UserName, "Sent" }),
                     ("Projekte/2025", new[] { server.UserName, "Projekte", "2025" }),
                 })
        {
            var expected = server.GetMessages(serverFolder).Select(m => Hash(m.Raw)).OrderBy(h => h).ToList();
            var folderId = archive.EnsureFolderPath(archivePath);
            var actual = archive.GetMessages(folderId).Select(m => Hash(archive.GetRawMessage(m.Id))).OrderBy(h => h).ToList();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Server_state_is_unchanged_and_only_examine_and_peek_are_used()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        await ImportJob.RunAsync(archive, Profile(server), null, Timeout());

        Assert.Equal(before, server.Snapshot());
        AssertReadOnlyProtocol(server);
        var verbs = server.Commands.Select(Verb).ToList();
        Assert.Equal(4, verbs.Count(v => v == "EXAMINE")); // INBOX, Projekte, Projekte/2025, Sent – Trash nie geöffnet
        Assert.DoesNotContain(server.Commands, c => c.Contains("Trash", StringComparison.Ordinal) && Verb(c) == "EXAMINE");
        Assert.Contains(server.Commands, c => c.Contains("BODY.PEEK[]", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unread_messages_stay_unread()
    {
        using var server = CreateServer();
        var flagsBefore = server.GetMessages("INBOX").Select(m => string.Join(' ', m.Flags)).ToList();
        Assert.Equal(new[] { "", "\\Answered \\Seen", "\\Flagged" }, flagsBefore);
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        await ImportJob.RunAsync(archive, Profile(server), null, Timeout());

        Assert.Equal(flagsBefore, server.GetMessages("INBOX").Select(m => string.Join(' ', m.Flags)).ToList());
        Assert.DoesNotContain("\\Seen", server.GetMessages("Projekte/2025")[0].Flags);
    }

    [Fact]
    public async Task Second_run_archives_nothing_new_and_downloads_no_bodies()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        await ImportJob.RunAsync(archive, Profile(server), null, Timeout());
        server.ClearLog();

        var second = await ImportJob.RunAsync(archive, Profile(server), null, Timeout());
        Dump("IMAP Zweitlauf", server.Commands);

        Assert.Equal(0, second.Archived);
        Assert.Equal(7, second.AlreadyArchived);
        Assert.Equal(0, second.Errors);
        Assert.Equal(7, archive.GetStats().MessageCount);
        Assert.DoesNotContain(server.Commands, c => c.Contains("BODY.PEEK[]", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, server.Snapshot());
        AssertReadOnlyProtocol(server);
    }

    [Fact]
    public async Task New_mail_between_runs_is_the_only_one_archived()
    {
        using var server = CreateServer();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        await ImportJob.RunAsync(archive, Profile(server), null, Timeout());

        var fresh = Msg("Neu eingetroffen", "Frische Nachricht", 20);
        server.AddMessage("INBOX", fresh);
        var before = server.Snapshot();
        server.ClearLog();

        var second = await ImportJob.RunAsync(archive, Profile(server), null, Timeout());

        Assert.Equal(1, second.Archived);
        Assert.Equal(7, second.AlreadyArchived);
        Assert.Single(server.Commands, c => c.Contains("BODY.PEEK[]", StringComparison.OrdinalIgnoreCase));
        var inbox = archive.EnsureFolderPath(new[] { server.UserName, "INBOX" });
        Assert.Contains(archive.GetMessages(inbox), m => Hash(archive.GetRawMessage(m.Id)) == Hash(fresh));
        Assert.Equal(before, server.Snapshot());
        Assert.Empty(server.GetMessages("INBOX").Last().Flags); // neue Mail bleibt ungelesen
        AssertReadOnlyProtocol(server);
    }

    [Fact]
    public async Task Wrong_password_throws_and_nothing_is_touched()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        await Assert.ThrowsAsync<AuthenticationException>(() => ImportJob.RunAsync(archive, Profile(server, password: "falsch"), null, Timeout()));

        Dump("IMAP falsches Kennwort", server.Commands);
        Assert.Equal(before, server.Snapshot());
        Assert.Empty(server.MutatingCommands);
        Assert.DoesNotContain(server.Commands, c => Verb(c) is "EXAMINE" or "SELECT" or "UID FETCH" or "FETCH");
        Assert.Equal(0, archive.GetStats().MessageCount);
    }

    [Fact]
    public async Task Unreachable_server_throws_quickly()
    {
        using var server = CreateServer();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            ImportJob.RunAsync(archive, Profile(server, port: FakeServerBase.GetClosedPort()), null, Timeout()));

        Assert.True(ex is SocketException or IOException or OperationCanceledException, ex.GetType().FullName);
        Assert.IsNotType<OperationCanceledException>(ex); // Verbindungsfehler, nicht erst der Test-Timeout
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"dauerte {sw.Elapsed}");
    }

    [Theory]
    [InlineData("PLAIN")]
    [InlineData("PLAIN-IR")]
    [InlineData("LOGIN")]
    public async Task Works_with_every_login_method(string mode)
    {
        using var server = CreateServer();
        server.AdvertiseAuthPlain = mode != "LOGIN";
        server.AdvertiseSaslIR = mode == "PLAIN-IR";
        var before = server.Snapshot();
        using var tmp = new TempDir();
        using var archive = MailArchive.Open(tmp.Path);

        var result = await ImportJob.RunAsync(archive, Profile(server), null, Timeout());

        Assert.Equal(7, result.Archived);
        Assert.Contains(server.Commands, c => Verb(c) == (mode == "LOGIN" ? "LOGIN" : "AUTHENTICATE"));
        Assert.Equal(before, server.Snapshot());
        AssertReadOnlyProtocol(server);
    }

    [Fact]
    public async Task ListFolders_via_SourceFactory_opens_no_folder()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        var ct = Timeout();

        await using (var source = SourceFactory.Create(Profile(server)))
        {
            await source.ConnectAsync(ct);
            var folders = (await source.ListFoldersAsync(ct)).Select(p => string.Join("/", p)).ToList();
            Assert.Equal("INBOX", folders[0]);
            Assert.Equal(new[] { "INBOX", "Projekte", "Projekte/2025", "Sent", "Trash" }, folders.OrderBy(f => f, StringComparer.Ordinal));
        }

        Dump("IMAP Ordnerliste", server.Commands);
        Assert.Equal(before, server.Snapshot());
        Assert.Empty(server.MutatingCommands);
        Assert.DoesNotContain(server.Commands, c => Verb(c) is "EXAMINE" or "SELECT");
        Assert.Contains(server.Commands, c => Verb(c) == "LOGOUT");
    }

    // ------------------------------------------------------------------ Gegenprobe: Der Fake erkennt Veränderungen wirklich

    [Fact]
    public async Task Control_fake_detects_a_deleting_client()
    {
        using var server = CreateServer();
        var before = server.Snapshot();
        var ct = Timeout();
        using (var client = new ImapClient())
        {
            await client.ConnectAsync(server.Host, server.Port, SecureSocketOptions.None, ct);
            await client.AuthenticateAsync(server.UserName, server.Password, ct);
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = await client.Inbox.SearchAsync(MailKit.Search.SearchQuery.All, ct);
            await client.Inbox.AddFlagsAsync(uids[0], MessageFlags.Deleted, true, ct);
            await client.Inbox.ExpungeAsync(ct);
            await client.DisconnectAsync(true, ct);
        }

        Assert.NotEqual(before, server.Snapshot());
        Assert.Equal(2, server.GetMessages("INBOX").Count);
        Assert.Contains(server.MutatingCommands, c => Verb(c) == "SELECT");
        Assert.Contains(server.MutatingCommands, c => Verb(c) == "UID STORE");
        Assert.Contains(server.MutatingCommands, c => Verb(c) == "EXPUNGE");
    }

    [Fact]
    public async Task Control_fake_sets_seen_on_non_peek_fetch()
    {
        using var server = CreateServer();
        var ct = Timeout();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port, ct);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.Latin1);
        async Task<string> Send(string tag, string cmd)
        {
            var bytes = Encoding.ASCII.GetBytes($"{tag} {cmd}\r\n");
            await stream.WriteAsync(bytes, ct);
            var sb = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                sb.AppendLine(line);
                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) break;
            }
            return sb.ToString();
        }
        await reader.ReadLineAsync(ct); // Begrüßung
        Assert.Contains("a1 OK", await Send("a1", $"LOGIN \"{server.UserName}\" \"{server.Password}\""));
        Assert.Contains("a2 OK [READ-WRITE]", await Send("a2", "SELECT INBOX"));
        Assert.Contains("\\Seen", await Send("a3", "FETCH 1 (BODY[])"));
        await Send("a4", "LOGOUT");

        Assert.Contains("\\Seen", server.GetMessages("INBOX")[0].Flags);
        Assert.Contains(server.MutatingCommands, c => c.Contains("BODY[]", StringComparison.Ordinal));
    }
}
