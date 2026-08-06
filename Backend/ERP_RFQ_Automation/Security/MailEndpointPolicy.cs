using System.Net;
using System.Net.Sockets;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// The single authority on which mail endpoints this server is willing to open a socket to.
///
/// <para><b>Why this is a security control and not a validation helper.</b> The host, port and
/// credentials of a mailbox are supplied by a TENANT ADMINISTRATOR and stored in
/// <c>Email_Configurations</c>. Every mail path then connects to whatever that row says. Without
/// this policy a tenant admin can point the server at <c>169.254.169.254</c> and read the cloud
/// instance's metadata credentials, at <c>127.0.0.1:6379</c> to reach an internal Redis, or at any
/// RFC 1918 address to sweep the private network — using the server as a proxy, from inside the
/// trust boundary, with the response surfaced back through a connection-test screen. That is
/// classic SSRF, and the mailbox setup screen is the most direct route to it in the product.</para>
///
/// <para><b>Resolve-then-connect, not resolve-then-hand-the-name-back.</b> The addresses are
/// validated and one of those exact <see cref="IPEndPoint"/>s is connected. Handing the hostname
/// to MailKit after a separate DNS check would leave a DNS-rebinding window in which the name
/// resolves public for the check and private for the connection. Callers therefore receive a
/// connected <see cref="Socket"/> rather than permission to dial.</para>
///
/// <para><b>Loopback is rejected in every environment, deliberately.</b> A local mail sink is a
/// legitimate development convenience, but an environment-conditional bypass in an SSRF control is
/// exactly the kind of flag that reaches production set the wrong way. Point local testing at a
/// sink reachable by a non-loopback address instead.</para>
///
/// <para>The logic here is the long-standing implementation from
/// <see cref="MailKitOutboundSmtpTransport"/> and <c>SmtpController</c>, moved to one place so the
/// mailbox admin surface cannot drift from the send path. Those two call sites now delegate here
/// and keep their original member names, so the reflection-based tests that pin them still bind.</para>
/// </summary>
public static class MailEndpointPolicy
{
    /// <summary>Longest legal DNS name.</summary>
    private const int MaximumHostLength = 253;

    /// <summary>
    /// Syntactic admission check: is this host/port pair even eligible to be dialled? Cheap, does
    /// no DNS, and is safe to call while validating a form. It is NOT sufficient on its own — a
    /// public-looking name can still resolve to a private address, which is why
    /// <see cref="ConnectAsync"/> re-validates after resolution.
    /// </summary>
    public static bool IsAllowedEndpoint(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaximumHostLength || port is < 1 or > 65535)
            return false;

        var normalized = Normalize(host);

        // ".localhost" is reserved by RFC 6761 and resolves to loopback on every resolver.
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        return IPAddress.TryParse(normalized, out var literal)
            ? IsPublicAddress(literal)
            : Uri.CheckHostName(normalized) == UriHostNameType.Dns;
    }

    /// <summary>Trailing-dot-tolerant host normalisation. "mail.example.com." and
    /// "mail.example.com" are the same name; only the former survives a naive comparison.</summary>
    public static string Normalize(string host) => host.Trim().TrimEnd('.');

    /// <summary>
    /// Resolves <paramref name="host"/> and rejects the result unless EVERY address is publicly
    /// routable. All, not any: a name that resolves to one public and one private address would
    /// otherwise be dialled on whichever the OS returned first.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host, CancellationToken cancellationToken)
    {
        var normalized = Normalize(host);
        var addresses = IPAddress.TryParse(normalized, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(normalized, cancellationToken);

        ValidateResolvedAddresses(addresses);
        return addresses;
    }

    public static void ValidateResolvedAddresses(IReadOnlyCollection<IPAddress> addresses)
    {
        if (addresses.Count == 0 || addresses.Any(x => !IsPublicAddress(x)))
            throw new InvalidOperationException("The configured mail host resolves to a prohibited address.");
    }

    /// <summary>
    /// Resolves, validates, and returns a socket connected to one of the validated addresses.
    /// The caller owns disposal.
    /// </summary>
    public static async Task<Socket> ConnectAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAsync(host, cancellationToken);

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
                // A cancelled connect is the caller's deadline expiring, not this address being
                // unreachable — trying the next one would silently overrun the budget.
                if (exception is OperationCanceledException) throw;
            }
        }

        throw new SocketException((lastError as SocketException)?.ErrorCode ?? (int)SocketError.HostUnreachable);
    }

    public static bool IsPublicAddress(IPAddress address)
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
