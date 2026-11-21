using MailVault.Core;
using Xunit;
using Xunit.Abstractions;

namespace MailVault.Tests;

public class AutoConfigTests
{
    readonly ITestOutputHelper _out;
    public AutoConfigTests(ITestOutputHelper output) => _out = output;

    const string Sample = """
        <?xml version="1.0"?>
        <clientConfig version="1.1">
          <emailProvider id="example.de">
            <incomingServer type="imap">
              <hostname>imap.example.de</hostname><port>143</port><socketType>STARTTLS</socketType>
              <username>%EMAILADDRESS%</username><authentication>password-cleartext</authentication>
            </incomingServer>
            <incomingServer type="imap">
              <hostname>imap.example.de</hostname><port>993</port><socketType>SSL</socketType>
              <username>%EMAILADDRESS%</username><authentication>password-cleartext</authentication>
            </incomingServer>
            <incomingServer type="pop3">
              <hostname>pop.example.de</hostname><port>995</port><socketType>SSL</socketType>
              <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication>
            </incomingServer>
          </emailProvider>
        </clientConfig>
        """;

    [Fact]
    public void Parses_thunderbird_autoconfig()
    {
        var r = AutoConfig.Parse(Sample, "max@example.de", "max", "example.de", "Test")!;
        Assert.Equal("imap.example.de", r.Imap!.Host);
        Assert.Equal(143, r.Imap.Port);                       // erste verschlüsselte Variante laut Anbieter
        Assert.Equal(ConnectionSecurity.StartTls, r.Imap.Security);
        Assert.Equal("max@example.de", r.Imap.UserName);
        Assert.Equal("max", r.Pop3!.UserName);
        Assert.Equal(ConnectionSecurity.SslOnConnect, r.Pop3.Security);
        Assert.Null(r.Hint);
    }

    [Fact]
    public void Detects_oauth_only_providers()
    {
        var xml = Sample.Replace("password-cleartext", "OAuth2");
        var r = AutoConfig.Parse(xml, "max@example.de", "max", "example.de", "Test")!;
        Assert.False(r.Imap!.PasswordAllowed);
        Assert.Contains("OAuth", r.Hint);
    }

    [Theory]
    [InlineData("mx00.ionos.de", "ionos.de")]
    [InlineData("aspmx.l.google.com", "google.com")]
    [InlineData("mail.protection.outlook.com", "outlook.com")]
    [InlineData("mx1.example.co.uk", "example.co.uk")]
    [InlineData("example.org", "example.org")]
    public void Base_domain_of_mail_exchanger(string host, string expected) => Assert.Equal(expected, AutoConfig.BaseDomain(host));

    /// <summary>Echte Abfragen; nur mit MAILVAULT_LIVE_AUTOCONFIG=1. Übertragen wird jeweils nur die Domain.</summary>
    [Theory]
    [InlineData("test@gmx.de")]
    [InlineData("test@web.de")]
    [InlineData("test@t-online.de")]
    [InlineData("test@gmail.com")]
    [InlineData("test@outlook.com")]
    [InlineData("test@posteo.de")]
    [InlineData("test@mailbox.org")]
    [InlineData("test@icloud.com")]
    [InlineData("test@freenet.de")]
    [InlineData("test@netflix.com")]   // eigene Domain bei Google Workspace (MX-Weg)
    [InlineData("test@golem.de")]      // eigene Domain bei Microsoft 365 (MX-Weg)
    public async Task Live_discovery(string email)
    {
        if (Environment.GetEnvironmentVariable("MAILVAULT_LIVE_AUTOCONFIG") != "1") return;
        var r = await AutoConfig.DiscoverAsync(email);
        _out.WriteLine(r is null ? $"{email}: nichts gefunden"
            : $"{email}: IMAP={r.Imap?.Host}:{r.Imap?.Port} {r.Imap?.Security} user={r.Imap?.UserName} | POP3={r.Pop3?.Host}:{r.Pop3?.Port} | {r.Source} | {r.Hint}");
        Assert.NotNull(r);
    }
}
