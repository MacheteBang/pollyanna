using System.Net.Sockets;
using Pollyanna.Configuration;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Pollyanna.Notifications;

/// <summary>One email, ready for the relay: the plain text and the same email as HTML (spec §15).</summary>
public sealed record OutgoingMail(string To, string Subject, string Body, string Html);

/// <summary>The mail relay as the outbox sees it. Tests replace it with an in-memory fake (spec §11).</summary>
public interface IMailSender
{
    Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken);

    /// <summary>Connects (and logs in, if configured) without sending anything, to check the settings.</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Sends through the configured relay with MailKit, one connection per email.</summary>
public sealed class SmtpMailSender(PollyannaSettings settings) : IMailSender
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    MailSettings Relay => settings.Mail ?? throw new InvalidOperationException("No mail relay is configured.");

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        var message = Build(mail, Relay.From);
        using var client = await ConnectAsync(cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    /// <summary>
    /// The message for <paramref name="mail"/>: <c>multipart/alternative</c>, the plain text first
    /// and the HTML second, both UTF-8 (spec §9).
    /// </summary>
    public static MimeMessage Build(OutgoingMail mail, MailboxAddress from)
    {
        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = mail.Subject;
        var plain = new TextPart("plain");
        plain.SetText(System.Text.Encoding.UTF8, mail.Body);
        var html = new TextPart("html");
        html.SetText(System.Text.Encoding.UTF8, mail.Html);
        message.Body = new MultipartAlternative { plain, html };
        return message;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    async Task<SmtpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var relay = Relay;
        var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(relay.Host, relay.Port, relay.Tls switch
            {
                SmtpTls.StartTls => SecureSocketOptions.StartTls,
                SmtpTls.Implicit => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.None,
            }, cancellationToken);
            if (relay.Username is not null)
                await client.AuthenticateAsync(relay.Username, relay.Password ?? "", cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What went wrong, for the log: the SMTP status or the kind of failure, never the exception's
    /// message, which can quote addresses back from the relay (docs/adr/0003).
    /// </summary>
    public static string Describe(Exception e) => e switch
    {
        SmtpCommandException c => $"the relay answered {(int)c.StatusCode} ({c.ErrorCode})",
        SmtpProtocolException => "SMTP protocol error",
        AuthenticationException => "the relay refused the login",
        SslHandshakeException => "TLS handshake failed",
        SocketException s => $"network error ({s.SocketErrorCode})",
        ServiceNotConnectedException or ServiceNotAuthenticatedException => "lost the connection to the relay",
        TimeoutException or OperationCanceledException => "timed out",
        IOException => "connection error",
        _ => e.GetType().Name,
    };
}
