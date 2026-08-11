using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class GrowthIntelligenceAuthenticatedHttpTests(Release01BHttpApplication app)
{
    [Fact]
    public async Task Growth_routes_enforce_auth_role_tenant_and_idempotent_manager_acknowledgement()
    {
        var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var from = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var workspacePath = $"/api/commercial-intelligence/coaching-recovery?from={from}&to={to}";
        using var anonymous = app.CreateClient();
        using var denied = Client(Release01BHttpApplication.DeniedRole,
            Release01BHttpApplication.TenantA, Release01BHttpApplication.GrowthRepUser);
        using var missingTenant = Client(Release01BHttpApplication.AllowedRole, null,
            Release01BHttpApplication.GrowthRepUser);
        using var rep = Client(Release01BHttpApplication.AllowedRole,
            Release01BHttpApplication.TenantA, Release01BHttpApplication.GrowthRepUser);
        using var manager = Client(Release01BHttpApplication.GrowthManagerRole,
            Release01BHttpApplication.TenantA, Release01BHttpApplication.GrowthManagerUser);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(workspacePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(workspacePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await missingTenant.GetAsync(workspacePath)).StatusCode);

        var own = await rep.GetAsync(workspacePath);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using (var ownBody = JsonDocument.Parse(await own.Content.ReadAsStringAsync()))
            Assert.Equal("assigned_to_me", ownBody.RootElement.GetProperty("scope").GetString());

        var tenantWide = await manager.GetAsync(workspacePath);
        Assert.Equal(HttpStatusCode.OK, tenantWide.StatusCode);
        using var tenantBody = JsonDocument.Parse(await tenantWide.Content.ReadAsStringAsync());
        Assert.Equal("tenant", tenantBody.RootElement.GetProperty("scope").GetString());
        var finding = Assert.Single(tenantBody.RootElement.GetProperty("coachingFindings").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "OVERDUE_FOLLOW_UP");
        var findingKey = finding.GetProperty("findingKey").GetString()!;
        Assert.Equal(Release01BHttpApplication.GrowthManagerUser,
            finding.GetProperty("salesRepUserId").GetInt64());
        Assert.Equal("/procurement/rfqs/view/396060", finding.GetProperty("actionRoute").GetString());

        var healthPath = $"/api/intelligence/customers/{Release01BHttpApplication.TenantACustomerId}/health?from={from}&to={to}";
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(healthPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(
            $"/api/intelligence/customers/{Release01BHttpApplication.TenantBCustomerId}/health?from={from}&to={to}"))
            .StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(healthPath)).StatusCode);

        var acknowledgementPath =
            $"/api/commercial-intelligence/coaching/{findingKey}/acknowledgements";
        var payload = new { disposition = "ACKNOWLEDGED", reason = "Reviewed with the sales representative.", from, to };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostAsync(rep, acknowledgementPath, payload, "growth-rep-denied")).StatusCode);

        var created = await PostAsync(manager, acknowledgementPath, payload, "growth-http-idempotent");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("ACKNOWLEDGED", createdBody.RootElement.GetProperty("disposition").GetString());
        var acknowledgedAt = createdBody.RootElement.GetProperty("acknowledgedAt").GetDateTime();

        var replay = await PostAsync(manager, acknowledgementPath, payload, "growth-http-idempotent");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(acknowledgedAt, replayBody.RootElement.GetProperty("acknowledgedAt").GetDateTime());
        // The create answer is built from the entity in memory and the replay answer from the row
        // read back, so the two only agree when the value written was already at PostgreSQL's
        // microsecond resolution AND the column materialises as UTC. Comparing the parsed
        // DateTimes catches the precision half; DateTime.Equals ignores Kind, so it does not catch
        // the half where the replay loses its "Z" and a consumer reads it in local time. The raw
        // JSON does.
        Assert.Equal(createdBody.RootElement.GetProperty("acknowledgedAt").GetRawText(),
            replayBody.RootElement.GetProperty("acknowledgedAt").GetRawText());

        var concurrentKey = $"growth-http-concurrent-{Guid.NewGuid():N}";
        var concurrentRequests = new[]
        {
            PostAsync(manager, acknowledgementPath, payload, concurrentKey),
            PostAsync(manager, acknowledgementPath, payload, concurrentKey)
        };
        var concurrentResponses = await Task.WhenAll(concurrentRequests);
        Assert.All(concurrentResponses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        foreach (var response in concurrentResponses) response.Dispose();

        var collision = await PostAsync(manager, acknowledgementPath, new
        {
            disposition = "DISMISSED",
            reason = "A different decision with the same key.",
            from,
            to
        }, "growth-http-idempotent");
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
    }

    private HttpClient Client(long roleId, long? tenantId, long userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", app.Token(roleId, tenantId, userId));
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path,
        object payload, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Correlation-ID", idempotencyKey);
        return client.SendAsync(request);
    }
}
