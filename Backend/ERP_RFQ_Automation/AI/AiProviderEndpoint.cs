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
    /// <summary>The configured base URL is a loopback address: no data leaves the host.</summary>
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
        // Classification rule is UNCHANGED from the original implementation
        // (`providerUri.IsLoopback ? Local : External`). Nothing that was External
        // before becomes Local here; only the *reason* is now explicit and logged.
        reason = uri.IsLoopback
            ? AiProviderEndpointReasons.LoopbackEndpoint
            : AiProviderEndpointReasons.NonLoopbackEndpoint;
        return true;
    }

    /// <summary>
    /// Builds the full descriptor for a configured provider. An endpoint that cannot be
    /// normalised is reported as <see cref="AiProviderClass.External"/> — fail closed.
    /// </summary>
    public static AiProviderDescriptor Describe(string provider, string? baseUrl, string? model)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim();
        var normalizedModel = NormalizeModel(model);
        if (!TryNormalize(baseUrl, out var endpoint, out var reason))
            return new(normalizedProvider, string.Empty, normalizedModel, AiProviderClass.External, reason);

        return new(normalizedProvider, endpoint, normalizedModel,
            reason == AiProviderEndpointReasons.LoopbackEndpoint
                ? AiProviderClass.Local
                : AiProviderClass.External,
            reason);
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
/// Resolves — once, at startup — the endpoint this process will actually call, and says
/// so loudly in the log. Registered as a singleton and touched during startup so the
/// line is always present in the deployment log, whether or not any document is ever
/// processed.
/// </summary>
public interface IAiProviderEndpointResolver
{
    AiProviderDescriptor Current { get; }
}

/// <inheritdoc />
public sealed class AiProviderEndpointResolver : IAiProviderEndpointResolver
{
    /// <summary>Provider label recorded on every AI request row for the Ollama-compatible client.</summary>
    public const string OllamaProvider = "Ollama";
    public const string DefaultBaseUrl = "http://127.0.0.1:11434/";
    public const string DefaultModel = "qwen2.5:14b";

    public AiProviderEndpointResolver(IConfiguration configuration, ILogger<AiProviderEndpointResolver> log)
    {
        Current = AiProviderEndpoint.Describe(
            OllamaProvider,
            configuration["Ollama:BaseUrl"] ?? DefaultBaseUrl,
            configuration["Ollama:Model"] ?? DefaultModel);

        if (Current.ProviderClass == AiProviderClass.External)
            log.LogWarning(
                "AI provider resolved as EXTERNAL. {Descriptor}. Unstructured document extraction " +
                "requires a per-tenant allow-list authorization for this exact endpoint; tenants " +
                "without one continue to fail closed.",
                Current);
        else
            log.LogInformation(
                "AI provider resolved as LOCAL. {Descriptor}. Unstructured document extraction " +
                "runs on the local model with no third-party egress.",
                Current);
    }

    public AiProviderDescriptor Current { get; }
}
