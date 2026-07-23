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

    internal static void ValidateResolvedAddresses(IReadOnlyCollection<IPAddress> addresses)
    {
        if (addresses.Count == 0 || addresses.Any(x => !IsPublicAddress(x)))
            throw new InvalidOperationException("The configured SMTP host resolves to a prohibited address.");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !IsSpecialIpv4(bytes);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !IsSpecialIpv6(bytes);
    }

    private static async Task<Socket> ConnectPublicSocketAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host.Trim().TrimEnd('.'), out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        ValidateResolvedAddresses(addresses);

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return socket;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = exception;
                if (exception is OperationCanceledException) throw;
            }
        }
        throw new SocketException((lastError as SocketException)?.ErrorCode ?? (int)SocketError.HostUnreachable);
    }

    private static bool IsSpecialIpv4(byte[] bytes)
        => bytes[0] == 10 || bytes[0] == 127 ||
           (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
           (bytes[0] == 169 && bytes[1] == 254) ||
           (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
           (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
           (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
           (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) ||
           (bytes[0] == 192 && bytes[1] == 168) ||
           (bytes[0] == 198 && bytes[1] is 18 or 19) ||
           (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
           (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
           bytes[0] == 0 || bytes[0] >= 224;

    private static bool IsSpecialIpv6(byte[] bytes)
        => HasPrefix(bytes, IPAddress.Parse("::").GetAddressBytes(), 96) ||
           HasPrefix(bytes, IPAddress.Parse("64:ff9b::").GetAddressBytes(), 96) ||
           HasPrefix(bytes, IPAddress.Parse("64:ff9b:1::").GetAddressBytes(), 48) ||
           HasPrefix(bytes, IPAddress.Parse("100::").GetAddressBytes(), 64) ||
           HasPrefix(bytes, IPAddress.Parse("100:0:0:1::").GetAddressBytes(), 64) ||
           HasPrefix(bytes, IPAddress.Parse("2001::").GetAddressBytes(), 23) ||
           HasPrefix(bytes, IPAddress.Parse("2001:db8::").GetAddressBytes(), 32) ||
           HasPrefix(bytes, IPAddress.Parse("2002::").GetAddressBytes(), 16) ||
           HasPrefix(bytes, IPAddress.Parse("3fff::").GetAddressBytes(), 20) ||
           HasPrefix(bytes, IPAddress.Parse("5f00::").GetAddressBytes(), 16) ||
           (bytes[0] & 0xFE) == 0xFC || bytes[0] == 0xFF;

    private static bool HasPrefix(byte[] address, byte[] prefix, int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        if (!address.AsSpan(0, wholeBytes).SequenceEqual(prefix.AsSpan(0, wholeBytes))) return false;
        var remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }
}
