using System.Net.Sockets;

using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>Übersetzt technische Fehler in verständliche Meldungen mit Lösungshinweis (in der Oberflächensprache).</summary>
public static class ErrorText
{
    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerException!;
        return ex switch
        {
            MailKit.Security.AuthenticationException a when a.Message.Contains("Application-specific", StringComparison.OrdinalIgnoreCase) ||
                                            a.Message.Contains("app password", StringComparison.OrdinalIgnoreCase) =>
                CoreText.T("Err_AppPasswordRequired"),
            MailKit.Security.AuthenticationException a =>
                CoreText.T("Err_AuthFailed") + Detail(a.Message),
            SslHandshakeException s =>
                CoreText.T("Err_Tls") + Detail(s.Message),
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
                CoreText.T("Err_HostNotFound"),
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                CoreText.T("Err_ConnectionRefused"),
            SocketException { SocketErrorCode: SocketError.TimedOut } or TimeoutException =>
                CoreText.T("Err_Timeout"),
            SocketException se => CoreText.F("Err_Network", se.Message),
            ImapCommandException ic => CoreText.F("Err_Imap", ic.ResponseText),
            Pop3CommandException pc => CoreText.F("Err_Pop3", pc.StatusText),
            SmtpCommandException sc => CoreText.F("Err_Smtp", (int)sc.StatusCode, sc.Message),
            ProtocolException pe => CoreText.T("Err_Protocol") + Detail(pe.Message),
            ServiceNotConnectedException => CoreText.T("Err_NotConnected"),
            FileNotFoundException fnf => CoreText.F("Err_FileNotFound", fnf.FileName ?? fnf.Message),
            DirectoryNotFoundException dnf => CoreText.F("Err_DirectoryNotFound", dnf.Message),
            IOException io when io.InnerException is SocketException inner => Describe(inner),
            IOException io => CoreText.F("Err_Io", io.Message),
            UnauthorizedAccessException => CoreText.T("Err_AccessDenied"),
            _ => ex.Message,
        };
    }

    static string Detail(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "" : CoreText.F("Err_ServerDetail", message.Trim());
}
