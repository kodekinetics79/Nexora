using System.Net;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Procurement.Discovery;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The wire shape of Ollama's web search, pinned: POST https://ollama.com/api/web_search with a
/// bearer key and <c>{ "query", "max_results" }</c>; back comes <c>{ "results": [ { title, url,
/// content } ] }</c>. No test here reaches the internet — the handler is a stub.
/// </summary>
public sealed class OllamaWebSearchProviderTests
{
    private const string Key = "test-search-key";

    [Fact]
    public async Task Posts_the_query_and_bearer_key_to_the_documented_endpoint_and_reads_the_results()
    {
        var handler = new RecordingHandler(Json("""
            {"results":[
              {"title":"Gulf Switchgear","url":"https://gulfswitchgear.com/lv431831","content":"In stock"},
              {"title":"No url here","url":"","content":"dropped"},
              {"title":"  Padded  ","url":" https://acme.com ","content":null}
            ]}
            """));
        var provider = Provider(handler, Key);

        var results = await provider.SearchAsync("Schneider LV431831 distributor Saudi Arabia", 10, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://ollama.com/api/web_search", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, handler.Request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("Schneider LV431831 distributor Saudi Arabia", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("max_results").GetInt32());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());

        Assert.Equal(2, results.Count);
        Assert.Equal(new WebSearchResult("Gulf Switchgear", "https://gulfswitchgear.com/lv431831", "In stock"), results[0]);
        Assert.Equal(new WebSearchResult("Padded", "https://acme.com", ""), results[1]);
    }

    [Fact]
    public async Task Max_results_is_clamped_to_the_providers_cap_of_ten()
    {
        var handler = new RecordingHandler(Json("""{"results":[]}"""));
        await Provider(handler, Key).SearchAsync("anything", 50, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(SupplierDiscoveryConfiguration.ProviderMaxResultsPerQuery,
            body.RootElement.GetProperty("max_results").GetInt32());
    }

    [Fact]
    public async Task A_non_success_status_is_a_search_failure_not_an_empty_answer()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid key"}""", Encoding.UTF8, "application/json")
        });
        var error = await Assert.ThrowsAsync<SupplierWebSearchException>(() =>
            Provider(handler, Key).SearchAsync("q", 10, CancellationToken.None));
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_search_failure()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>maintenance</html>", Encoding.UTF8, "application/json")
        });
        await Assert.ThrowsAsync<SupplierWebSearchException>(() =>
            Provider(handler, Key).SearchAsync("q", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var handler = new RecordingHandler(Json("""{"results":[]}"""));
        await Assert.ThrowsAsync<SupplierWebSearchException>(() =>
            Provider(handler, apiKey: null).SearchAsync("q", 10, CancellationToken.None));
        Assert.Null(handler.Request);
    }

    [Fact]
    public void The_destination_is_the_fixed_ollama_origin_classified_external()
    {
        var provider = Provider(new RecordingHandler(Json("""{"results":[]}""")), Key);
        Assert.Equal("https://ollama.com", provider.Destination.Endpoint);
        Assert.Equal(AiProviderEndpointResolver.OllamaProvider, provider.Destination.Provider);
        Assert.Equal(AiProviderClass.External, provider.Destination.ProviderClass);
        Assert.True(provider.Destination.IsResolved);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "  ", null)]
    [InlineData(null, "__OLLAMA_API_KEY__", null)]
    [InlineData(null, "model-key", "model-key")]
    [InlineData("search-key", "model-key", "search-key")]
    [InlineData("  search-key ", null, "search-key")]
    public void The_search_key_falls_back_to_the_model_key_and_treats_the_deploy_placeholder_as_absent(
        string? searchKey, string? modelKey, string? expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SupplierDiscoveryConfiguration.ApiKeyKey] = searchKey,
            [SupplierDiscoveryConfiguration.FallbackApiKeyKey] = modelKey
        }).Build();
        Assert.Equal(expected, SupplierDiscoveryConfiguration.ApiKey(configuration));
    }

    private static OllamaWebSearchProvider Provider(HttpMessageHandler handler, string? apiKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SupplierDiscoveryConfiguration.ApiKeyKey] = apiKey
        }).Build();
        return new OllamaWebSearchProvider(new HttpClient(handler), configuration, new NoopLogger<OllamaWebSearchProvider>());
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return response;
        }
    }
}
