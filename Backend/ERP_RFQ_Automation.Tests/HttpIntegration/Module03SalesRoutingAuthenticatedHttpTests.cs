using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Module03SalesRoutingHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class Module03SalesRoutingAuthenticatedHttpTests(Release01BHttpApplication app)
{
    private const long TenantAWorkItem = 918_001;
    private const long TenantADecision = 918_002;
    private const long TenantBWorkItem = 918_003;
    private const long TenantBDecision = 918_004;
    private const long TenantALead = 918_005;
    private const long TenantBLead = 918_006;

    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { HttpMethod.Get.Method, "/api/commercial-intelligence/routing-queue" },
        { HttpMethod.Get.Method, "/api/commercial-intelligence/routing-owner-options" },
        { HttpMethod.Get.Method, "/api/commercial-intelligence/account-owner-options" },
        { HttpMethod.Post.Method, $"/api/commercial-intelligence/routing-queue/{TenantAWorkItem}/assign" }
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Routing_routes_challenge_unauthenticated_and_forbid_denied_roles(string method, string path)
    {
        using var anonymous = app.CreateClient();
        using var anonymousResponse = await anonymous.SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var denied = Client(Release01BHttpApplication.DeniedRole,
            Release01BHttpApplication.TenantA, Release01BHttpApplication.GrowthRepUser);
        using var deniedResponse = await denied.SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
    }

    [Fact]
    public async Task Manager_assignment_is_tenant_scoped_versioned_audited_and_idempotent()
    {
        await PrepareRoutingQueueAsync();
        using var manager = Client(Release01BHttpApplication.GrowthManagerRole,
            Release01BHttpApplication.TenantA, Release01BHttpApplication.GrowthManagerUser);
        using var individual = Client(Release01BHttpApplication.AllowedRole,
            Release01BHttpApplication.TenantA, Release01BHttpApplication.GrowthRepUser);

        using var optionsResponse = await manager.GetAsync("/api/commercial-intelligence/routing-owner-options");
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        using var options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync());
        Assert.Contains(options.RootElement.EnumerateArray(), owner =>
            owner.GetProperty("userId").GetInt64() == Release01BHttpApplication.GrowthRepUser);
        Assert.DoesNotContain(options.RootElement.EnumerateArray(), owner =>
            owner.TryGetProperty("businessUnitId", out var tenant) && tenant.GetInt64() == Release01BHttpApplication.TenantB);

        using var queueResponse = await manager.GetAsync(
            $"/api/commercial-intelligence/routing-queue?sourceId={TenantAWorkItem}");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
        using var queue = JsonDocument.Parse(await queueResponse.Content.ReadAsStringAsync());
        var row = Assert.Single(queue.RootElement.EnumerateArray());
        Assert.Equal(TenantAWorkItem, row.GetProperty("sourceId").GetInt64());
        Assert.Equal(1, row.GetProperty("version").GetInt64());

        using var hiddenCrossTenant = await manager.GetAsync(
            $"/api/commercial-intelligence/routing-queue?sourceId={TenantBWorkItem}");
        Assert.Equal(HttpStatusCode.OK, hiddenCrossTenant.StatusCode);
        using var hiddenPayload = JsonDocument.Parse(await hiddenCrossTenant.Content.ReadAsStringAsync());
        Assert.Empty(hiddenPayload.RootElement.EnumerateArray());

        using var individualMutation = await AssignAsync(individual, TenantAWorkItem, 1,
            Release01BHttpApplication.GrowthRepUser, "individual-denied");
        Assert.Equal(HttpStatusCode.Forbidden, individualMutation.StatusCode);

        using var stale = await AssignAsync(manager, TenantAWorkItem, 0,
            Release01BHttpApplication.GrowthRepUser, "stale-version");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var crossTenant = await AssignAsync(manager, TenantBWorkItem, 1,
            Release01BHttpApplication.GrowthRepUser, "cross-tenant");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var idempotencyKey = $"module03-http-{Guid.NewGuid():N}";
        using var assigned = await AssignAsync(manager, TenantAWorkItem, 1,
            Release01BHttpApplication.GrowthRepUser, idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var firstBody = await assigned.Content.ReadAsStringAsync();

        using var replay = await AssignAsync(manager, TenantAWorkItem, 1,
            Release01BHttpApplication.GrowthRepUser, idempotencyKey);
        Assert.True(replay.StatusCode == HttpStatusCode.OK,
            $"Expected idempotent replay to return 200, got {(int)replay.StatusCode}: {await replay.Content.ReadAsStringAsync()}");
        using var firstResult = JsonDocument.Parse(firstBody);
        using var replayResult = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstResult.RootElement.GetProperty("decisionId").GetInt64(),
            replayResult.RootElement.GetProperty("decisionId").GetInt64());
        Assert.Equal(firstResult.RootElement.GetProperty("assignmentId").GetInt64(),
            replayResult.RootElement.GetProperty("assignmentId").GetInt64());

        using var historyResponse = await manager.GetAsync(
            $"/api/commercial-intelligence/leads/{TenantALead}/assignment-history");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var assignment = Assert.Single(history.RootElement.EnumerateArray(), value =>
            value.GetProperty("idempotencyKey").GetString() == idempotencyKey);
        Assert.Equal(Release01BHttpApplication.GrowthRepUser,
            assignment.GetProperty("ownerUserId").GetInt64());
    }

    private HttpClient Client(long roleId, long tenantId, long userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId, userId));
        return client;
    }

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method == HttpMethod.Post.Method
            ? JsonContent.Create(new { OwnerUserId = Release01BHttpApplication.GrowthRepUser, ExpectedVersion = 1 })
            : null
    };

    private static async Task<HttpResponseMessage> AssignAsync(
        HttpClient client, long workItemId, long expectedVersion, long ownerUserId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/commercial-intelligence/routing-queue/{workItemId}/assign")
        {
            Content = JsonContent.Create(new { OwnerUserId = ownerUserId, ExpectedVersion = expectedVersion })
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Correlation-ID", key);
        return await client.SendAsync(request);
    }

    private async Task PrepareRoutingQueueAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        await AddLeadAsync(db, Release01BHttpApplication.TenantA,
            Release01BHttpApplication.TenantACustomerId, TenantALead, "MODULE03-A");
        await AddLeadAsync(db, Release01BHttpApplication.TenantB,
            Release01BHttpApplication.TenantBCustomerId, TenantBLead, "MODULE03-B");
        if (!await db.SalesRepProfiles.IgnoreQueryFilters().AnyAsync(value =>
                value.BusinessUnitId == Release01BHttpApplication.TenantA
                && value.UserId == Release01BHttpApplication.GrowthRepUser))
        {
            db.SalesRepProfiles.Add(new SalesRepProfile
            {
                BusinessUnitId = Release01BHttpApplication.TenantA,
                UserId = Release01BHttpApplication.GrowthRepUser,
                IsRoutingEligible = true,
                CapacityPercent = 80,
                DistributionWeight = 1m,
                EffectiveFromUtc = DateTime.UtcNow.AddDays(-1),
                Version = 1,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedBy = "module03-http-tests",
                LastMutationIdempotencyKey = "module03-http-profile"
            });
        }
        await db.SaveChangesAsync();
        await AddWorkItemAsync(db, Release01BHttpApplication.TenantA,
            TenantALead, TenantADecision, TenantAWorkItem,
            Release01BHttpApplication.GrowthRepUser);
        await AddWorkItemAsync(db, Release01BHttpApplication.TenantB,
            TenantBLead, TenantBDecision, TenantBWorkItem, null);
        await db.SaveChangesAsync();
    }

    private static async Task AddLeadAsync(
        ErpRfqAutomationContext db,
        long tenantId,
        long customerId,
        long leadId,
        string reference)
    {
        if (await db.Leads.IgnoreQueryFilters().AnyAsync(value => value.Id == leadId)) return;

        var lead = new Lead
        {
            Id = leadId,
            BusinessUnitId = tenantId,
            Rfqno = reference,
            RecDate = DateTime.UtcNow,
            LeadSource = "Module03HttpIntegration",
            CreatedBy = "module03-http-tests",
            CreatedDate = DateTime.UtcNow
        };
        lead.ResolveCommercialIdentity(customerId, null, "EXACT");
        db.Leads.Add(lead);
    }

    private static async Task AddWorkItemAsync(
        ErpRfqAutomationContext db,
        long tenantId,
        long leadId,
        long decisionId,
        long workItemId,
        long? suggestedUserId)
    {
        if (await db.Set<UnassignedWorkItem>().IgnoreQueryFilters()
            .AnyAsync(value => value.Id == workItemId)) return;

        var decision = new LeadRoutingDecision
        {
            Id = decisionId,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            SuggestedUserId = suggestedUserId,
            MatchStatus = CustomerMatchStatus.NoEvidence,
            Outcome = RoutingOutcome.Unassigned,
            MatchConfidence = 0.82m,
            DecisionCode = "MODULE03_HTTP_REVIEW",
            Explanation = "{\"reason\":\"Representative workload and eligibility were measured.\"}",
            PolicyVersion = "routing-v1",
            CorrelationId = $"module03-http-{tenantId}",
            IdempotencyKey = $"module03-http-decision-{tenantId}",
            CreatedOn = DateTime.UtcNow.AddMinutes(-30)
        };
        db.Add(new UnassignedWorkItem
        {
            Id = workItemId,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            RoutingDecision = decision,
            SuggestedUserId = suggestedUserId,
            MatchConfidence = 0.82m,
            ReasonCode = "WORKLOAD_REVIEW",
            RequiredAction = "Confirm a sales owner",
            Priority = 90,
            Status = WorkItemStatus.Open,
            EnteredOn = DateTime.UtcNow.AddMinutes(-30),
            SlaDueOn = DateTime.UtcNow.AddMinutes(30),
            IdempotencyKey = $"module03-http-work-{tenantId}",
            Version = 1
        });
    }
}

[CollectionDefinition(Module03SalesRoutingHttpCollection.Name)]
public sealed class Module03SalesRoutingHttpCollection : ICollectionFixture<Release01BHttpApplication>
{
    public const string Name = "Module 03 sales routing HTTP";
}
