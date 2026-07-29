using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class SupplierNegotiationAuthenticatedHttpTests(Release01BHttpApplication app)
{
    [Fact]
    public async Task Negotiation_routes_enforce_auth_tenant_permission_and_idempotency()
    {
        var path = $"/api/supplier-quote-inbox/{Release01BHttpApplication.TenantASupplierQuoteId}/negotiation";
        using var anonymous = app.CreateClient();
        using var denied = Client(Release01BHttpApplication.DeniedRole,
            Release01BHttpApplication.TenantA);
        using var missingTenant = Client(Release01BHttpApplication.AllowedRole, null);
        using var allowed = Client(Release01BHttpApplication.AllowedRole,
            Release01BHttpApplication.TenantA);
        using var historyViewer = Client(Release01BHttpApplication.SupplierHistoryViewerRole,
            Release01BHttpApplication.TenantA);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await missingTenant.GetAsync(path)).StatusCode);

        var own = await allowed.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await historyViewer.GetAsync(path)).StatusCode);
        using (var body = JsonDocument.Parse(await own.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("quoteVersion").GetInt64());
            Assert.Equal(1, body.RootElement.GetProperty("currentRound")
                .GetProperty("roundNumber").GetInt32());
            Assert.Contains(body.RootElement.GetProperty("recommendations").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "FREIGHT_INCLUSIVE_OFFER");
        }

        var crossTenant = await allowed.GetAsync(
            $"/api/supplier-quote-inbox/{Release01BHttpApplication.TenantBSupplierQuoteId}/negotiation");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var decisionPath =
            $"/api/supplier-quote-inbox/{Release01BHttpApplication.TenantASupplierQuoteId}/negotiation-decisions";
        var payload = new DecisionRequest(1, "FREIGHT_INCLUSIVE_OFFER", "PREPARED",
            "Ask for a freight-inclusive Supplier offer.");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(denied, decisionPath, payload, "http-neg-denied", "http-neg-denied"))
            .StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(historyViewer, decisionPath, payload,
                "http-neg-viewer", "http-neg-viewer")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostAsync(allowed,
                $"/api/supplier-quote-inbox/{Release01BHttpApplication.TenantBSupplierQuoteId}/negotiation-decisions",
                payload, "http-neg-cross-tenant", "http-neg-cross-tenant")).StatusCode);

        var created = await PostAsync(allowed, decisionPath, payload,
            "http-neg-idempotent", "http-neg-created");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.False(createdBody.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(2, createdBody.RootElement.GetProperty("resultingQuoteVersion").GetInt64());

        var replay = await PostAsync(allowed, decisionPath, payload,
            "http-neg-idempotent", "http-neg-replay");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayBody.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(createdBody.RootElement.GetProperty("decisionId").GetInt64(),
            replayBody.RootElement.GetProperty("decisionId").GetInt64());

        var collision = await PostAsync(allowed, decisionPath, payload with
        {
            Reason = "A different request using the same key."
        }, "http-neg-idempotent", "http-neg-collision");
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        var stale = await PostAsync(allowed, decisionPath, payload with
        {
            Reason = "A fresh key with a stale expected quote version."
        }, "http-neg-stale", "http-neg-stale");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    private HttpClient Client(long roleId, long? tenantId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", app.Token(roleId, tenantId));
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path,
        object payload, string idempotencyKey, string correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Correlation-ID", correlationId);
        return client.SendAsync(request);
    }

    private sealed record DecisionRequest(long ExpectedQuoteVersion, string RecommendationCode,
        string Disposition, string Reason);
}
