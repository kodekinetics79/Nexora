using System.Net;
using System.Net.Sockets;
using ERP_RFQ_Automation.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ERP_RFQ_Automation.Security;

public interface IOutboundSmtpTransport
{
    Task SendAsync(EmailConfiguration configuration, MimeMessage message, CancellationToken cancellationToken);
}

public sealed class MailKitOutboundSmtpTransport : IOutboundSmtpTransport
{
    public async Task SendAsync(
        EmailConfiguration configuration, MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = 10_000 };
        using var socket = await ConnectPublicSocketAsync(
            configuration.Host, configuration.Port, cancellationToken);
        await client.ConnectAsync(socket, configuration.Host, configuration.Port,
            configuration.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
            cancellationToken);
        await client.AuthenticateAsync(configuration.Username, configuration.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    // Delegating wrappers. The implementation lives in MailEndpointPolicy so the mailbox admin
    // surface and this send path cannot drift apart; the member names stay put because
    // AdministrativeSecurityTests binds them by reflection.
    internal static void ValidateResolvedAddresses(IReadOnlyCollection<IPAddress> addresses)
        => MailEndpointPolicy.ValidateResolvedAddresses(addresses);

    internal static bool IsPublicAddress(IPAddress address)
        => MailEndpointPolicy.IsPublicAddress(address);

    private static Task<Socket> ConnectPublicSocketAsync(
        string host, int port, CancellationToken cancellationToken)
        => MailEndpointPolicy.ConnectAsync(host, port, cancellationToken);
}
