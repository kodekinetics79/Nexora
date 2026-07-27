using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ERP_RFQ_Automation.LeadIdentity;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class AuthenticatedHttpRlsTests(Release01BHttpApplication app)
{
    [Fact]
    public async Task Operations_readiness_requires_permission_and_keeps_queue_counts_tenant_scoped()
    {
        using var anonymous = app.CreateClient();
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/operations/readiness")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.GetAsync("/api/operations/readiness")).StatusCode);

        var response = await allowed.GetAsync("/api/operations/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var extraction = body.RootElement.GetProperty("queues").EnumerateArray()
            .Single(x => x.GetProperty("key").GetString() == "extraction");
        Assert.Equal(1, extraction.GetProperty("pending").GetInt32());
        Assert.Equal(0, extraction.GetProperty("deadLetter").GetInt32());
        Assert.True(body.RootElement.GetProperty("healthChecks").GetArrayLength() >= 5);
        Assert.True(body.RootElement.GetProperty("blockingReasons").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Unauthenticated_request_is_challenged()
    {
        using var client = app.CreateClient();

        var response = await client.GetAsync(BatchPath(app.TenantABatchId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_role_without_module_permission_is_forbidden()
    {
        using var client = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);

        var response = await client.GetAsync(BatchPath(app.TenantABatchId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_without_tenant_claim_is_forbidden_by_tenant_guard()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, tenantId: null);

        var response = await client.GetAsync(BatchPath(app.TenantABatchId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("tenant claim", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Own_tenant_batch_is_returned_through_authorization_and_runtime_rls()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        var response = await client.GetAsync(BatchPath(app.TenantABatchId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(app.TenantABatchId, body.RootElement.GetProperty("batchId").GetGuid());
        Assert.Equal(2, body.RootElement.GetProperty("logicalInquiries").GetInt32());
    }

    [Fact]
    public async Task Cross_tenant_batch_is_not_disclosed()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        var response = await client.GetAsync(BatchPath(app.TenantBBatchId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Query_tenant_forgery_cannot_change_analytics_scope()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));

        var ownResponse = await client.GetAsync($"/api/LeadIngestion/analytics?from={from}&to={to}");
        var forgedResponse = await client.GetAsync(
            $"/api/LeadIngestion/analytics?from={from}&to={to}&businessUnitId={Release01BHttpApplication.TenantB}");

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forgedResponse.StatusCode);
        var ownPayload = await ownResponse.Content.ReadFromJsonAsync<AnalyticsPayload>();
        var forgedPayload = await forgedResponse.Content.ReadFromJsonAsync<AnalyticsPayload>();
        var ownVolume = Assert.Single(ownPayload!.Metrics, metric => metric.Key == "ingestion-volume");
        var forgedVolume = Assert.Single(forgedPayload!.Metrics, metric => metric.Key == "ingestion-volume");
        Assert.True(ownVolume.Value > 0);
        Assert.Equal(ownVolume.Value, forgedVolume.Value);
        Assert.Equal(ownVolume.OccurrenceIds.Order(), forgedVolume.OccurrenceIds.Order());
    }

    [Fact]
    public async Task Evidence_read_requires_permission_and_tenant_and_verifies_content()
    {
        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);

        var own = await allowed.GetAsync($"/api/File/attachment/{Release01BHttpApplication.TenantAAttachmentId}");
        Assert.True(own.StatusCode == HttpStatusCode.OK,
            $"Expected verified evidence 200, received {(int)own.StatusCode}: {await own.Content.ReadAsStringAsync()}");
        Assert.Equal("tenant-a-authoritative-evidence", await own.Content.ReadAsStringAsync());

        var forbidden = await denied.GetAsync($"/api/File/attachment/{Release01BHttpApplication.TenantAAttachmentId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var crossTenant = await allowed.GetAsync($"/api/File/attachment/{Release01BHttpApplication.TenantBAttachmentId}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
    }

    [Fact]
    public async Task Evidence_read_fails_closed_when_authoritative_bytes_change()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        app.CorruptTenantAEvidence();
        try
        {
            var response = await client.GetAsync($"/api/File/attachment/{Release01BHttpApplication.TenantAAttachmentId}");
            Assert.True(response.StatusCode == HttpStatusCode.Conflict,
                $"Expected integrity conflict, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.DoesNotContain("tampered-evidence", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            app.RestoreTenantAEvidence();
        }
    }

    [Fact]
    public async Task Possible_match_decision_requires_edit_permission_and_replays_idempotently()
    {
        var request = new MatchDecisionRequest("defer", Release01BHttpApplication.TenantALeadId, 1,
            "Needs customer confirmation", "http-match-defer");
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        var forbidden = await denied.PostAsJsonAsync(
            $"/api/LeadIngestion/match-reviews/{Release01BHttpApplication.TenantAMatchOccurrenceId}/decision", request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        var first = await allowed.PostAsJsonAsync(
            $"/api/LeadIngestion/match-reviews/{Release01BHttpApplication.TenantAMatchOccurrenceId}/decision", request);
        Assert.True(first.StatusCode == HttpStatusCode.OK,
            $"Expected match decision 200, received {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        var replay = await allowed.PostAsJsonAsync(
            $"/api/LeadIngestion/match-reviews/{Release01BHttpApplication.TenantAMatchOccurrenceId}/decision", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var state = await app.MatchDecisionStateAsync();
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, state.Classification);
        Assert.Equal(1, state.AuditCount);
    }

    [Fact]
    public async Task Possible_match_decision_does_not_disclose_cross_tenant_occurrence()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        var request = new MatchDecisionRequest("defer", Release01BHttpApplication.TenantBLeadId, 1,
            "Cross tenant probe", "http-cross-tenant-match");

        var response = await client.PostAsJsonAsync(
            "/api/LeadIngestion/match-reviews/999999999/decision", request);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound,
            $"Expected cross-tenant 404, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Dashboard_drill_down_uses_authenticated_tenant_and_permission()
    {
        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-2).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-1).ToString("O"));

        var response = await allowed.GetAsync($"/api/Dashboard/release-01?from={from}&to={to}&businessUnitId={Release01BHttpApplication.TenantB}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("release-01", payload.RootElement.GetProperty("definitionVersion").GetString());
        Assert.Equal("assigned_to_me", payload.RootElement.GetProperty("roleScope").GetProperty("scope").GetString());
        Assert.DoesNotContain(Release01BHttpApplication.TenantBLeadId.ToString(), payload.RootElement.GetRawText(), StringComparison.Ordinal);

        var forbidden = await denied.GetAsync($"/api/Dashboard/release-01?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Customer_and_contact_reads_enforce_permission_and_runtime_rls()
    {
        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);

        Assert.Equal(HttpStatusCode.OK,
            (await allowed.GetAsync($"/api/Customer/{Release01BHttpApplication.TenantACustomerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await allowed.GetAsync($"/api/Customer/{Release01BHttpApplication.TenantBCustomerId}?businessUnitId={Release01BHttpApplication.TenantB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.GetAsync($"/api/Customer/{Release01BHttpApplication.TenantACustomerId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await allowed.GetAsync($"/api/Contact/{Release01BHttpApplication.TenantAContactId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await allowed.GetAsync($"/api/Contact/{Release01BHttpApplication.TenantBContactId}?businessUnitId={Release01BHttpApplication.TenantB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.GetAsync($"/api/Contact/{Release01BHttpApplication.TenantAContactId}")).StatusCode);
    }

    [Fact]
    public async Task Processing_evidence_requires_auth_permission_and_authenticated_tenant_scope()
    {
        using var anonymous = app.CreateClient();
        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        var ownPath = $"/api/processing-evidence/rfqs/{Release01BHttpApplication.TenantAProcurementRfqId}";
        var crossTenantPath = $"/api/processing-evidence/rfqs/{Release01BHttpApplication.TenantBProcurementRfqId}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ownPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(ownPath)).StatusCode);

        var own = await allowed.GetAsync(ownPath);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var payload = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        Assert.Equal(Release01BHttpApplication.TenantALeadId,
            payload.RootElement.GetProperty("leadId").GetInt64());
        Assert.Contains(Release01BHttpApplication.TenantAProcurementRfqId,
            payload.RootElement.GetProperty("rfqs").EnumerateArray()
                .Select(x => x.GetProperty("rfqId").GetInt64()));

        Assert.Equal(HttpStatusCode.NotFound,
            (await allowed.GetAsync(crossTenantPath)).StatusCode);
    }

    private HttpClient Client(long roleId, long? tenantId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId));
        return client;
    }

    private static string BatchPath(Guid batchId) => $"/api/LeadIngestion/batches/{batchId}";

    private sealed record AnalyticsPayload(AnalyticsMetric[] Metrics);
    private sealed record AnalyticsMetric(string Key, decimal Value, long[] OccurrenceIds);
}
