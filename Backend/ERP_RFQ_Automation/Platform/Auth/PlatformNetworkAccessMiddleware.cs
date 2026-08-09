using System.Net;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Configuration-backed network boundary for the control plane. The caller address is
/// taken exclusively from <see cref="HttpContext.Connection.RemoteIpAddress"/> after
/// ASP.NET's trusted-proxy ForwardedHeaders middleware has run. Raw forwarding headers
/// are deliberately never inspected here.
/// </summary>
public sealed class PlatformNetworkAccessMiddleware
{
    internal const string ConfigurationMode = "PlatformAccess:NetworkMode";
    internal const string ConfigurationCidrs = "PlatformAccess:AllowedCidrs";

    private readonly RequestDelegate _next;
    private readonly ILogger<PlatformNetworkAccessMiddleware> _logger;
    private readonly IReadOnlyList<NetworkRange> _allowedRanges;
    private readonly bool _enforced;

    public PlatformNetworkAccessMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<PlatformNetworkAccessMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var configuredMode = configuration[ConfigurationMode];
        if (string.IsNullOrWhiteSpace(configuredMode))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException(
                    $"{ConfigurationMode} must be explicitly set to AllowList or Any in Production.");
            configuredMode = "Any";
        }

        if (configuredMode.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            _allowedRanges = Array.Empty<NetworkRange>();
            return;
        }

        if (!configuredMode.Equals("AllowList", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{ConfigurationMode} must be either AllowList or Any.");

        _enforced = true;
        var configuredCidrs = configuration.GetSection(ConfigurationCidrs).Get<string[]>()
                              ?? Array.Empty<string>();
        if (configuredCidrs.Length == 0)
            throw new InvalidOperationException(
                $"{ConfigurationCidrs} must contain at least one network when {ConfigurationMode}=AllowList.");

        try
        {
            _allowedRanges = configuredCidrs.Select(NetworkRange.Parse).ToArray();
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{ConfigurationCidrs} contains an invalid IP address or CIDR prefix.", exception);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enforced || !context.Request.Path.StartsWithSegments("/api/platform"))
        {
            await _next(context);
            return;
        }

        var address = Normalize(context.Connection.RemoteIpAddress);
        if (address is not null && _allowedRanges.Any(range => range.Contains(address)))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Platform network access denied for client address {ClientAddress}",
            address?.ToString() ?? "unknown");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync("{\"error\":\"Platform access is not permitted from this network.\"}");
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    private sealed record NetworkRange(byte[] Network, int PrefixLength)
    {
        public static NetworkRange Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException();
            var parts = value.Trim().Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var parsed)) throw new FormatException();
            var address = Normalize(parsed)!;
            var bytes = address.GetAddressBytes();
            var maxPrefix = bytes.Length * 8;
            var prefix = parts.Length == 1
                ? maxPrefix
                : int.TryParse(parts[1], out var candidate) ? candidate : -1;
            if (prefix < 0 || prefix > maxPrefix) throw new FormatException();

            var network = bytes.ToArray();
            ApplyMask(network, prefix);
            return new NetworkRange(network, prefix);
        }

        public bool Contains(IPAddress address)
        {
            var candidate = Normalize(address)!.GetAddressBytes();
            if (candidate.Length != Network.Length) return false;
            ApplyMask(candidate, PrefixLength);
            return candidate.AsSpan().SequenceEqual(Network);
        }

        private static void ApplyMask(byte[] bytes, int prefixLength)
        {
            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;
            if (remainingBits != 0)
                bytes[fullBytes] &= (byte)(0xff << (8 - remainingBits));
            var zeroFrom = fullBytes + (remainingBits == 0 ? 0 : 1);
            Array.Clear(bytes, zeroFrom, bytes.Length - zeroFrom);
        }
    }
}
