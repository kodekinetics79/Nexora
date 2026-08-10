using System.Net;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.AI;

/// <summary>
/// Why a provider endpoint was classified the way it was. These strings are written to
/// logs and returned in governance decisions, so an operator can always answer
/// "which endpoint is this deployment pointed at, and why is it Local/External?"
/// without reading source. The 2026-08 incident — production silently running with
/// <c>Ollama__BaseUrl=https://ollama.com/</c> and therefore refusing every unstructured
/// extraction — was only discoverable by reading source. It must never be again.
/// </summary>
public static class AiProviderEndpointReasons
{
    /// <summary>
    /// Every address the configured base URL RESOLVES to is loopback: no data leaves the
    /// host. This is the only reason that yields <see cref="AiProviderClass.Local"/>.
    /// </summary>
    public const string LoopbackEndpoint = "loopback_endpoint";

    /// <summary>The configured base URL is NOT loopback: calls egress to a third party.</summary>
    public const string NonLoopbackEndpoint = "non_loopback_endpoint";

    /// <summary>No base URL was configured at all.</summary>
    public const string EndpointUnresolved = "endpoint_unresolved";

    /// <summary>The base URL is not a parseable absolute URI.</summary>
    public const string EndpointUnparseable = "endpoint_unparseable";

    /// <summary>The base URL uses a scheme other than http/https.</summary>
    public const string EndpointSchemeUnsupported = "endpoint_scheme_unsupported";

    /// <summary>The base URL embeds credentials (user:password@host) — never accepted.</summary>
    public const string EndpointCredentialsInUrl = "endpoint_credentials_in_url";

    /// <summary>
    /// The base URL names a host that LOOKS like loopback but the resolver could not answer
    /// for it. Fails closed to <see cref="AiProviderClass.External"/>: an unproven "Local"
    /// is the claim the whole single-box privacy guarantee rests on, so it is never granted
    /// on a name nobody could resolve.
    /// </summary>
    public const string EndpointNameResolutionFailed = "endpoint_name_resolution_failed";

    /// <summary>
    /// The base URL names a host that LOOKS like loopback — <c>localhost</c> and
    /// <c>loopback</c> both satisfy <see cref="Uri.IsLoopback"/> with no lookup whatsoever —
    /// but at least one address it actually resolves to is off this machine. This is the
    /// hosts-file / search-domain / DNS-rebinding case, and it is External.
    /// </summary>
    public const string EndpointNameResolvesOffHost = "endpoint_name_resolves_off_host";
}

/// <summary>
/// Name resolution, behind a seam so endpoint classification can be exercised without a
/// live resolver. There is exactly one production implementation
/// (<see cref="SystemAiEndpointHostResolver"/>); the seam exists for the tests that must
/// prove a name resolving off-box is refused, which cannot be staged through real DNS.
/// </summary>
public interface IAiEndpointHostResolver
{
    /// <summary>Resolves <paramref name="host"/>, or throws if it cannot be resolved.</summary>
    IReadOnlyList<IPAddress> Resolve(string host);
}

/// <inheritdoc />
public sealed class SystemAiEndpointHostResolver : IAiEndpointHostResolver
{
    public static readonly SystemAiEndpointHostResolver Instance = new();

    public IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);
}

/// <summary>
/// The canonical identity of the inference endpoint a deployment is pointed at:
/// the origin (scheme + host + non-default port, never a path, query, fragment or
/// credentials), the model, the resulting <see cref="AiProviderClass"/> and the reason
/// for that classification.
///
/// <para>
/// This is the value an operator authorizes. Deliberately NOT the raw configured URL:
/// the raw value can vary by trailing slash, casing, path suffix or default port and
/// still address the same third party, so matching on it would let a typo (or a
/// deliberate variation) slip past an allow-list. Normalising to an origin means one
/// authorization covers exactly one destination and nothing else.
/// </para>
/// </summary>
public sealed record AiProviderDescriptor(
    string Provider,
    string Endpoint,
    string Model,
    AiProviderClass ProviderClass,
    string ClassificationReason)
{
    /// <summary>True when a usable origin was derived from configuration.</summary>
    public bool IsResolved => !string.IsNullOrEmpty(Endpoint);

    /// <summary>
    /// A provider whose endpoint could not be resolved. Fails CLOSED: classified
    /// External, so it can never accidentally be treated as local processing.
    /// </summary>
    public static AiProviderDescriptor Unresolved(string provider = "unknown") =>
        new(provider, string.Empty, string.Empty, AiProviderClass.External,
            AiProviderEndpointReasons.EndpointUnresolved);

    /// <summary>Log/diagnostic form. Contains no API key and no document content.</summary>
    public override string ToString() =>
        $"provider={Provider} endpoint={(IsResolved ? Endpoint : "(unresolved)")} " +
        $"model={(string.IsNullOrEmpty(Model) ? "(unset)" : Model)} " +
        $"class={ProviderClass} reason={ClassificationReason}";
}

/// <summary>
/// Normalisation + classification of AI provider endpoints. Pure and side-effect free
/// so the same rules are applied identically by the LLM client, the allow-list gate and
/// the operator-facing authorization API — there is exactly one definition of
/// "which endpoint is this" in the system.
/// </summary>
public static class AiProviderEndpoint
{
    /// <summary>Sentinel meaning "any model at this endpoint". Stored, never null, so the
    /// unique index over (tenant, provider, endpoint, model) actually constrains.</summary>
    public const string AnyModel = "*";

    public const int MaxEndpointLength = 255;
    public const int MaxProviderLength = 100;
    public const int MaxModelLength = 255;

    /// <summary>
    /// Reduces a configured base URL to its canonical origin, lower-cased, with the
    /// default port elided. Returns false (with a reason) for anything that cannot be
    /// safely reduced — an unparseable value, a non-http(s) scheme, or a URL carrying
    /// credentials.
    ///
    /// <para><b>The reason it reports on success is a SYNTACTIC hint, not a
    /// classification.</b> <see cref="Uri.IsLoopback"/> returns true for the bare hostnames
    /// <c>localhost</c> and <c>loopback</c> with no DNS lookup at all, so a hosts entry, a
    /// DNS search domain or a rebinding answer can make either resolve off-box while this
    /// method still says "loopback". Only <see cref="Describe(string,string?,string?,IAiEndpointHostResolver?)"/>
    /// classifies, and it classifies by RESOLVING. Callers that need a security decision
    /// must use the descriptor, never this reason.</para>
    /// </summary>
    public static bool TryNormalize(string? value, out string endpoint, out string reason)
    {
        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = AiProviderEndpointReasons.EndpointUnresolved;
            return false;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            reason = AiProviderEndpointReasons.EndpointUnparseable;
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = AiProviderEndpointReasons.EndpointSchemeUnsupported;
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            reason = AiProviderEndpointReasons.EndpointCredentialsInUrl;
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        if (string.IsNullOrEmpty(host))
        {
            reason = AiProviderEndpointReasons.EndpointUnparseable;
            return false;
        }
        var candidate = uri.IsDefaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{uri.Port}";
        if (candidate.Length > MaxEndpointLength)
        {
            reason = AiProviderEndpointReasons.EndpointUnparseable;
            return false;
        }

        endpoint = candidate;
        reason = uri.IsLoopback
            ? AiProviderEndpointReasons.LoopbackEndpoint
            : AiProviderEndpointReasons.NonLoopbackEndpoint;
        return true;
    }

    /// <summary>Extracts the host from an already-normalised origin.</summary>
    public static string HostOf(string endpointOrigin) =>
        Uri.TryCreate(endpointOrigin, UriKind.Absolute, out var uri) ? uri.IdnHost : string.Empty;

    /// <summary>
    /// Builds the full descriptor for a configured provider, and this is where the
    /// deployment's central privacy claim is actually decided.
    ///
    /// <para><b>Local is a resolution result, never a string test.</b> The previous rule was
    /// <c>uri.IsLoopback</c>, evaluated once, on the configured string. That is true for the
    /// bare names <c>localhost</c> and <c>loopback</c> without any lookup, so a hosts entry
    /// or a DNS search domain was enough to make a deployment egress every document it
    /// processed while the startup log said "LOCAL … no third-party egress". Here a host
    /// that CLAIMS loopback must prove it: the name is resolved and EVERY address it returns
    /// must be loopback (IPv4-mapped IPv6 unwrapped, so <c>::ffff:127.0.0.1</c> counts and
    /// <c>::ffff:169.254.169.254</c> does not). One off-box answer, or no answer at all,
    /// and the descriptor is External.</para>
    ///
    /// <para><b>The asymmetry is deliberate.</b> A host that does not even claim loopback is
    /// External without a lookup — that direction never needs proof, because External is the
    /// conservative verdict and DNS on the startup path should not be able to turn an
    /// external endpoint into a local one by accident.</para>
    ///
    /// <para>Classification here is point-in-time. The window between it and the request is
    /// closed separately, at connect time, by <see cref="AiEgressGuard"/>.</para>
    /// </summary>
    public static AiProviderDescriptor Describe(
        string provider, string? baseUrl, string? model, IAiEndpointHostResolver? hostResolver = null)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim();
        var normalizedModel = NormalizeModel(model);
        if (!TryNormalize(baseUrl, out var endpoint, out var reason))
            return new(normalizedProvider, string.Empty, normalizedModel, AiProviderClass.External, reason);

        if (reason != AiProviderEndpointReasons.LoopbackEndpoint)
            return new(normalizedProvider, endpoint, normalizedModel,
                AiProviderClass.External, AiProviderEndpointReasons.NonLoopbackEndpoint);

        var host = HostOf(endpoint);
        if (IPAddress.TryParse(host, out var literal))
            return new(normalizedProvider, endpoint, normalizedModel,
                MailEndpointPolicy.IsLoopbackAddress(literal)
                    ? AiProviderClass.Local
                    : AiProviderClass.External,
                MailEndpointPolicy.IsLoopbackAddress(literal)
                    ? AiProviderEndpointReasons.LoopbackEndpoint
                    : AiProviderEndpointReasons.NonLoopbackEndpoint);

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = (hostResolver ?? SystemAiEndpointHostResolver.Instance).Resolve(host);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException
                                              or ArgumentException or InvalidOperationException)
        {
            return new(normalizedProvider, endpoint, normalizedModel,
                AiProviderClass.External, AiProviderEndpointReasons.EndpointNameResolutionFailed);
        }

        if (addresses.Count == 0)
            return new(normalizedProvider, endpoint, normalizedModel,
                AiProviderClass.External, AiProviderEndpointReasons.EndpointNameResolutionFailed);

        return addresses.All(MailEndpointPolicy.IsLoopbackAddress)
            ? new(normalizedProvider, endpoint, normalizedModel,
                AiProviderClass.Local, AiProviderEndpointReasons.LoopbackEndpoint)
            : new(normalizedProvider, endpoint, normalizedModel,
                AiProviderClass.External, AiProviderEndpointReasons.EndpointNameResolvesOffHost);
    }

    public static string NormalizeProvider(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? string.Empty : provider.Trim();

    public static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();

    /// <summary>Case-insensitive provider comparison (provider names are labels, not secrets).</summary>
    public static bool ProviderMatches(string authorized, string actual) =>
        string.Equals(authorized, actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>Origins are already lower-cased by <see cref="TryNormalize"/>; compare exactly.</summary>
    public static bool EndpointMatches(string authorized, string actual) =>
        !string.IsNullOrEmpty(actual) && string.Equals(authorized, actual, StringComparison.Ordinal);

    /// <summary>
    /// Model ids are case-sensitive at every provider we support, so the comparison is
    /// ordinal. <see cref="AnyModel"/> authorizes every model at that endpoint — an
    /// explicit, visible, per-row choice rather than a silent default.
    /// </summary>
    public static bool ModelMatches(string authorized, string actual) =>
        authorized == AnyModel || string.Equals(authorized, actual, StringComparison.Ordinal);
}

/// <summary>
/// The deployment's declared inference stance, resolved exactly once at startup from the
/// same endpoint classification the enforcement uses.
///
/// <para>
/// This is INFORMATIONAL/TELEMETRY, never an enforcement path: the per-tenant allow-list
/// (<see cref="IAiExternalProviderTrust"/>) decides whether an external endpoint may be
/// used, and the external-dependency ceiling in <see cref="AiGovernanceService"/> governs
/// unauthorized external usage. The posture exists so an operator can see the
/// deployment's stance in the product (AI Trust Center) and in the startup log without
/// reading configuration or source.
/// </para>
/// </summary>
public enum InferencePosture
{
    /// <summary>The configured inference endpoint is loopback: extraction runs locally.</summary>
    LocalFirst,

    /// <summary>
    /// The configured inference endpoint is external: every governed call egresses, and is
    /// lawful only under a per-tenant allow-list authorization for that exact endpoint.
    /// </summary>
    ExternalAuthorized
}

public static class InferencePostures
{
    /// <summary>One derivation rule, shared by the startup resolver and the ledger.</summary>
    public static InferencePosture For(AiProviderClass providerClass) =>
        providerClass == AiProviderClass.Local
            ? InferencePosture.LocalFirst
            : InferencePosture.ExternalAuthorized;
}

/// <summary>
/// Resolves — once, at startup — the endpoint this process will actually call, and says
/// so loudly in the log. Registered as a singleton and touched during startup so the
/// line is always present in the deployment log, whether or not any document is ever
/// processed.
/// </summary>
public interface IAiProviderEndpointResolver
{
    AiProviderDescriptor Current { get; }

    /// <summary>The declared inference posture derived from <see cref="Current"/> at startup.</summary>
    InferencePosture Posture { get; }

    /// <summary>
    /// EVERY inference destination this process can call, not just the extraction one.
    ///
    /// <para>This existing only for Ollama was a hole rather than an omission: the agent
    /// posts whole conversations to Anthropic, and because that origin had no descriptor it
    /// could never appear in an allow-list row, and therefore could never be authorized,
    /// expired or revoked. The only surviving control was a free-text provider-NAME match in
    /// the policy row. A destination the governance model cannot name is a destination the
    /// governance model does not govern.</para>
    /// </summary>
    IReadOnlyList<AiProviderDescriptor> All { get; }

    /// <summary>
    /// The descriptor for an exact provider/model pair, or null when this process has no
    /// such destination. Null is a REFUSAL at every call site — an endpoint that cannot be
    /// named cannot be authorized.
    /// </summary>
    AiProviderDescriptor? Find(string provider, string model);
}

/// <summary>
/// The Claude Messages API destination the sourcing agent posts whole conversations to.
///
/// <para>The origin lived as a private <c>const</c> string inside the LLM client, which is
/// why it was outside the endpoint model entirely. It lives here now for one reason: the
/// allow-list matches on a NORMALISED ORIGIN, so an origin that no descriptor names can
/// never be granted, expired or revoked. The base URL is overridable
/// (<c>Agent:Anthropic:BaseUrl</c>) so a gateway or a test double is a configuration change
/// rather than a code change — and whatever it is set to is classified and authorized by
/// the same rules as every other destination.</para>
/// </summary>
public static class AnthropicProviderDefaults
{
    /// <summary>Provider label recorded on every AI request row for the agent client.</summary>
    public const string Provider = "Anthropic";

    /// <summary>The vendor's published API origin. Overridable; never bypassable.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    public const string MessagesPath = "/v1/messages";
    public const string DefaultModel = "claude-sonnet-5";

    public const string BaseUrlKey = "Agent:Anthropic:BaseUrl";
    public const string ModelKey = "Agent:Anthropic:Model";

    public static string BaseUrl(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration[BaseUrlKey]) ? DefaultBaseUrl : configuration[BaseUrlKey]!;

    public static string Model(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration[ModelKey]) ? DefaultModel : configuration[ModelKey]!;

    /// <summary>The absolute request URI, derived from the same base URL that is classified.</summary>
    public static string MessagesUrl(IConfiguration configuration) =>
        new Uri(new Uri(BaseUrl(configuration).TrimEnd('/') + "/"), MessagesPath.TrimStart('/')).ToString();
}

/// <inheritdoc />
public sealed class AiProviderEndpointResolver : IAiProviderEndpointResolver
{
    /// <summary>Provider label recorded on every AI request row for the Ollama-compatible client.</summary>
    public const string OllamaProvider = "Ollama";
    public const string DefaultBaseUrl = "http://127.0.0.1:11434/";
    public const string DefaultModel = "qwen2.5:14b";

    private readonly AiProviderDescriptor[] _all;

    public AiProviderEndpointResolver(IConfiguration configuration, ILogger<AiProviderEndpointResolver> log)
        : this(configuration, log, hostResolver: null)
    {
    }

    /// <param name="hostResolver">
    /// Injected only by tests that must stage a name resolving off this machine — the
    /// hosts-file / search-domain / rebinding case that no real resolver can be asked to
    /// reproduce on demand. Null in production, where the system resolver is used.
    /// </param>
    public AiProviderEndpointResolver(
        IConfiguration configuration, ILogger<AiProviderEndpointResolver> log,
        IAiEndpointHostResolver? hostResolver)
    {
        Current = AiProviderEndpoint.Describe(
            OllamaProvider,
            configuration["Ollama:BaseUrl"] ?? DefaultBaseUrl,
            configuration["Ollama:Model"] ?? DefaultModel,
            hostResolver);
        Posture = InferencePostures.For(Current.ProviderClass);

        // The agent's destination is a first-class descriptor, on the same footing as the
        // extraction one, so it can be named in an allow-list row.
        Anthropic = AiProviderEndpoint.Describe(
            AnthropicProviderDefaults.Provider,
            AnthropicProviderDefaults.BaseUrl(configuration),
            AnthropicProviderDefaults.Model(configuration),
            hostResolver);

        _all = [Current, Anthropic];

        if (Current.ProviderClass == AiProviderClass.External)
            log.LogWarning(
                "AI provider resolved as EXTERNAL. Posture={Posture}. {Descriptor}. Unstructured document " +
                "extraction requires a per-tenant allow-list authorization for this exact endpoint; tenants " +
                "without one continue to fail closed. The external-dependency ceiling governs " +
                "unauthorized external usage only; authorized calls are exempt from that ratio.",
                Posture, Current);
        else
            log.LogInformation(
                "AI provider resolved as LOCAL. Posture={Posture}. {Descriptor}. Unstructured document " +
                "extraction runs on the local model with no third-party egress.",
                Posture, Current);

        log.LogInformation(
            "Agent inference destination registered. {Descriptor}. Agent calls to this endpoint require a " +
            "per-tenant allow-list authorization naming this exact origin.",
            Anthropic);
    }

    public AiProviderDescriptor Current { get; }

    /// <summary>The agent's Claude Messages destination.</summary>
    public AiProviderDescriptor Anthropic { get; }

    /// <inheritdoc />
    public InferencePosture Posture { get; }

    /// <inheritdoc />
    public IReadOnlyList<AiProviderDescriptor> All => _all;

    /// <inheritdoc />
    public AiProviderDescriptor? Find(string provider, string model) =>
        _all.FirstOrDefault(x => x.IsResolved
                                 && AiProviderEndpoint.ProviderMatches(x.Provider, provider)
                                 && string.Equals(x.Model, model, StringComparison.Ordinal));
}
