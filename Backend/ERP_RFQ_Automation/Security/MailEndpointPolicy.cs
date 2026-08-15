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
/// <para><b>Loopback is rejected unless a Development host explicitly opts in.</b> This class
/// used to refuse loopback in every environment, and its stated reason was sound: an
/// environment-conditional bypass in an SSRF control is exactly the kind of flag that reaches
/// production set the wrong way. That objection is answered STRUCTURALLY rather than by
/// discipline — see <see cref="EnableLoopbackForLocalDevelopment"/>. The allowance cannot be
/// turned on by configuration alone: the enabling call itself refuses outside Development, so a
/// production deployment carrying the flag set true is not a vulnerability, it is a no-op with a
/// loud log line. It is also scoped to LOOPBACK ONLY — private, link-local, carrier-grade-NAT and
/// every other special range stay refused everywhere, because the SSRF risk that matters is
/// dialling internal infrastructure, and 127.0.0.0/8 is not that.</para>
///
/// <para>It exists because the alternative was worse in practice: with no local mail sink
/// reachable at all, the mailbox journey could not be exercised end to end on a developer
/// machine, so the one path that loses a customer's mail was only ever tested against doubles.</para>
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

    /// <summary>Configuration key that REQUESTS the allowance. Requesting is not granting.</summary>
    public const string LoopbackAllowanceKey = "Mail:AllowLoopbackForLocalDevelopment";

    private static volatile bool _loopbackAllowed;

    /// <summary>True when a Development host has explicitly opted in. False everywhere else.</summary>
    public static bool IsLoopbackAllowed => _loopbackAllowed;

    /// <summary>
    /// Grants the loopback allowance, and ONLY from a Development host.
    /// </summary>
    /// <param name="isDevelopmentEnvironment">
    /// <c>IHostEnvironment.IsDevelopment()</c>. The gate is a PARAMETER rather than a
    /// configuration read so the refusal is a property of the call itself: there is no key,
    /// environment variable or appsettings file that can grant this on a non-Development host,
    /// which is what makes "the flag reached production set the wrong way" a no-op rather than a
    /// hole.
    /// </param>
    /// <param name="requested">The value of <see cref="LoopbackAllowanceKey"/>.</param>
    /// <returns>True when the allowance is now active.</returns>
    public static bool EnableLoopbackForLocalDevelopment(bool isDevelopmentEnvironment, bool requested)
    {
        _loopbackAllowed = isDevelopmentEnvironment && requested;
        return _loopbackAllowed;
    }

    /// <summary>Test hook: restores the default (refused) state.</summary>
    internal static void ResetLoopbackAllowance() => _loopbackAllowed = false;

    /// <summary>
    /// Loopback, and nothing else. Private ranges, link-local, CGNAT and the rest stay refused
    /// even under the allowance: the SSRF risk this control exists for is a mail server dialling
    /// internal infrastructure, and 127.0.0.0/8 reaches only the machine already running the code.
    /// </summary>
    private static bool IsAllowedLoopback(IPAddress address)
        => _loopbackAllowed && IPAddress.IsLoopback(address);

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
            return _loopbackAllowed;

        return IPAddress.TryParse(normalized, out var literal)
            ? IsPublicAddress(literal) || IsAllowedLoopback(literal)
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
        // ALL, not any: a name resolving to one public and one private address must not be
        // dialled on whichever the OS returned first. The loopback allowance widens what counts
        // as acceptable; it does not weaken the all-must-pass rule.
        if (addresses.Count == 0 || addresses.Any(x => !IsPublicAddress(x) && !IsAllowedLoopback(x)))
            throw new InvalidOperationException("The configured mail host resolves to a prohibited address.");
    }

    /// <summary>
    /// Resolves, validates, and returns a socket connected to one of the validated addresses.
    /// The caller owns disposal.
    /// </summary>
    public static Task<Socket> ConnectAsync(
        string host, int port, CancellationToken cancellationToken)
        => ConnectAsync(host, port, address => IsPublicAddress(address) || IsAllowedLoopback(address),
            "The configured mail host resolves to a prohibited address.", cancellationToken);

    /// <summary>
    /// The same resolve-then-connect discipline against a caller-supplied admission rule.
    ///
    /// <para>Mail is not the only outbound dial in this server that takes a name from
    /// configuration or from a tenant. The AI egress guard needs the mirror-image rule — a
    /// deployment that declares itself LOCAL must reach loopback and nothing else — and it
    /// needs it applied at connect time, not once at startup, for exactly the DNS-rebinding
    /// reason this class already documents. Sharing the loop rather than copying it means
    /// there is one implementation of "resolve, admit EVERY address or none, then connect to
    /// one of the addresses you actually validated".</para>
    /// </summary>
    /// <param name="isAdmissible">Applied to every resolved address. All must pass, not any.</param>
    /// <param name="rejectionMessage">Surfaced when the admission rule refuses the name.</param>
    public static async Task<Socket> ConnectAsync(
        string host, int port, Func<IPAddress, bool> isAdmissible, string rejectionMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isAdmissible);
        var normalized = Normalize(host);
        var addresses = IPAddress.TryParse(normalized, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(normalized, cancellationToken);

        if (addresses.Length == 0 || addresses.Any(x => !isAdmissible(x)))
            throw new InvalidOperationException(rejectionMessage);

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

    /// <summary>
    /// True only for an address that is this machine. The IPv4-mapped-IPv6 unwrap is the
    /// point: <c>::ffff:127.0.0.1</c> is loopback and
    /// <see cref="IPAddress.IsLoopback(IPAddress)"/> alone does not say so.
    /// </summary>
    public static bool IsLoopbackAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
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
