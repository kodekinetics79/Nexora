using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>Where the internet search key comes from, and how to tell that there is none.</summary>
public static class SupplierDiscoveryConfiguration
{
    /// <summary>The key meant for this feature. Set this when the search account differs from the model account.</summary>
    public const string ApiKeyKey = "SupplierDiscovery:ApiKey";

    /// <summary>Ollama's web search and Ollama's cloud models share one account key, so the model key is the fallback.</summary>
    public const string FallbackApiKeyKey = "Ollama:ApiKey";

    /// <summary>
    /// The one origin this provider posts to. Fixed rather than configurable: the tenant's consent
    /// is recorded against an exact origin, so a configurable one would let a deployment redirect
    /// consented traffic somewhere the tenant never saw.
    /// </summary>
    public const string Endpoint = "https://ollama.com";

    public const string SearchPath = "/api/web_search";

    /// <summary>Ollama caps <c>max_results</c> at 10 per call. Asking for more is answered with an error, not with more.</summary>
    public const int ProviderMaxResultsPerQuery = 10;

    /// <summary>
    /// The configured key, or null when there is none. The deploy-time placeholder
    /// (<c>__OLLAMA_API_KEY__</c>) that appsettings.json ships with is treated as "no key": sending
    /// it would fail every search with an authorization error the rep cannot act on, when the
    /// honest answer is that an administrator has not set the key yet.
    /// </summary>
    public static string? ApiKey(IConfiguration configuration)
    {
        var key = configuration[ApiKeyKey];
        if (string.IsNullOrWhiteSpace(key)) key = configuration[FallbackApiKeyKey];
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        return key.StartsWith("__", StringComparison.Ordinal) && key.EndsWith("__", StringComparison.Ordinal)
            ? null
            : key;
    }
}

/// <summary>
/// Ollama's web search, <c>POST https://ollama.com/api/web_search</c>.
///
/// <para>Request: <c>{ "query": "...", "max_results": N }</c> with <c>Authorization: Bearer &lt;key&gt;</c>.
/// Response: <c>{ "results": [ { "title", "url", "content" } ] }</c>. Verified against
/// docs.ollama.com/capabilities/web-search on 2026-09-16: <c>max_results</c> defaults to 5 and is
/// capped at 10.</para>
///
/// <para>The HTTP client is built on <see cref="AiEgressGuard"/> in Program.cs: no redirects are
/// followed and the connection is refused unless the host resolves to a public address, the same
/// discipline every AI client here is held to.</para>
/// </summary>
public sealed class OllamaWebSearchProvider : ISupplierWebSearchProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly ILogger<OllamaWebSearchProvider> _log;

    public OllamaWebSearchProvider(HttpClient http, IConfiguration configuration, ILogger<OllamaWebSearchProvider> log)
    {
        _http = http;
        _apiKey = SupplierDiscoveryConfiguration.ApiKey(configuration);
        _log = log;
        _http.BaseAddress ??= new Uri(SupplierDiscoveryConfiguration.Endpoint);
    }

    public AiProviderDescriptor Destination { get; } = AiProviderEndpoint.Describe(
        AiProviderEndpointResolver.OllamaProvider, SupplierDiscoveryConfiguration.Endpoint, model: null);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new SupplierWebSearchException("Internet supplier search has no key configured.");

        var body = JsonSerializer.Serialize(new SearchRequest(
            query.Trim(), Math.Clamp(maxResults, 1, SupplierDiscoveryConfiguration.ProviderMaxResultsPerQuery)), Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, SupplierDiscoveryConfiguration.SearchPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !ct.IsCancellationRequested)
        {
            throw new SupplierWebSearchException("The internet search provider did not answer.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The body may carry the provider's own error text; the status is what the operator needs.
                _log.LogWarning("Internet supplier search refused by provider: HTTP {Status} for query length {Length}.",
                    (int)response.StatusCode, query.Length);
                throw new SupplierWebSearchException(
                    $"The internet search provider answered HTTP {(int)response.StatusCode}.");
            }

            try
            {
                var parsed = await response.Content.ReadFromJsonAsync<SearchResponse>(Json, ct);
                return (parsed?.Results ?? [])
                    .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                    .Select(x => new WebSearchResult(x.Title?.Trim() ?? string.Empty, x.Url!.Trim(), x.Content?.Trim() ?? string.Empty))
                    .ToArray();
            }
            catch (JsonException exception)
            {
                throw new SupplierWebSearchException("The internet search provider answered with a body that could not be read.", exception);
            }
        }
    }

    private sealed record SearchRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults);

    private sealed class SearchResponse
    {
        [JsonPropertyName("results")]
        public List<SearchResponseItem>? Results { get; set; }
    }

    private sealed class SearchResponseItem
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}
