using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.HttpIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.PostgreSQL.OpportunityPriority;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class OpportunityPrioritiesAuthenticatedHttpTests(Release01BHttpApplication app)
{
    private const long TenantAManagerRole = 848_101;
    private const long TenantBManagerRole = 848_102;
    private const long TenantAManagerUser = 848_201;
    private const long TenantBManagerUser = 848_202;
    private const long LeadsModule = 84_001;

    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { HttpMethod.Get.Method, "/api/opportunity-priorities" },
        { HttpMethod.Post.Method, "/api/opportunity-priorities/reconcile" },
        { HttpMethod.Get.Method, "/api/opportunity-priorities/commercial-cases/1" },
        { HttpMethod.Post.Method, "/api/opportunity-priorities/1/feedback" }
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Critical_routes_challenge_unauthenticated_requests(string method, string path)
    {
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Critical_routes_forbid_roles_without_leads_permission(string method, string path)
    {
        using var client = Client(
            Release01BHttpApplication.DeniedRole,
            Release01BHttpApplication.TenantA,
            TenantAManagerUser);

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_without_tenant_context_is_forbidden()
    {
        using var client = Client(
            Release01BHttpApplication.AllowedRole,
            null,
            TenantAManagerUser);

        using var response = await client.GetAsync("/api/opportunity-priorities");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reader_can_query_but_cannot_run_manager_reconciliation()
    {
        using var client = Client(
            Release01BHttpApplication.AllowedRole,
            Release01BHttpApplication.TenantA,
            TenantAManagerUser);

        using var query = await client.GetAsync("/api/opportunity-priorities");
        await AssertStatusAsync(query, HttpStatusCode.OK);
        using var reconcile = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/opportunity-priorities/reconcile",
            new { CorrelationId = "reader-reconcile-denied", IdempotencyKey = "reader-reconcile-denied" },
            "reader-reconcile-denied");

        Assert.Equal(HttpStatusCode.Forbidden, reconcile.StatusCode);
    }

    [Fact]
    public async Task Managers_reconcile_their_tenants_and_cross_tenant_case_and_feedback_are_not_found()
    {
        await PrepareManagerRolesAsync();
        using var tenantA = Client(
            TenantAManagerRole,
            Release01BHttpApplication.TenantA,
            TenantAManagerUser);
        using var tenantB = Client(
            TenantBManagerRole,
            Release01BHttpApplication.TenantB,
            TenantBManagerUser);

        using var reconcileA = await ReconcileAsync(tenantA, $"tenant-a-{Guid.NewGuid():N}");
        await AssertStatusAsync(reconcileA, HttpStatusCode.OK);
        using var reconcileB = await ReconcileAsync(tenantB, $"tenant-b-{Guid.NewGuid():N}");
        await AssertStatusAsync(reconcileB, HttpStatusCode.OK);

        OpportunityRecommendation recommendationA;
        OpportunityRecommendation recommendationB;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            recommendationA = await db.OpportunityRecommendations.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.BusinessUnitId == Release01BHttpApplication.TenantA)
                .OrderByDescending(x => x.Id)
                .FirstAsync();
            recommendationB = await db.OpportunityRecommendations.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.BusinessUnitId == Release01BHttpApplication.TenantB)
                .OrderByDescending(x => x.Id)
                .FirstAsync();
        }

        using var ownCase = await tenantA.GetAsync(
            $"/api/opportunity-priorities/commercial-cases/{recommendationA.CommercialCaseId}");
        await AssertStatusAsync(ownCase, HttpStatusCode.OK);

        var feedbackKey = $"tenant-a-feedback-{Guid.NewGuid():N}";
        using var ownFeedback = await SendJsonAsync(
            tenantA,
            HttpMethod.Post,
            $"/api/opportunity-priorities/{recommendationA.Id}/feedback",
            new
            {
                ExpectedRecommendationId = recommendationA.Id,
                Decision = OpportunityFeedbackDecision.Accepted,
                ReplacementActionCode = (string?)null,
                Reason = "Authorized manager accepted the shadow recommendation.",
                SupersedesFeedbackId = (long?)null,
                CorrelationId = feedbackKey,
                IdempotencyKey = feedbackKey
            },
            feedbackKey);
        await AssertStatusAsync(ownFeedback, HttpStatusCode.OK);

        using var crossTenantCase = await tenantA.GetAsync(
            $"/api/opportunity-priorities/commercial-cases/{recommendationB.CommercialCaseId}");
        await AssertStatusAsync(crossTenantCase, HttpStatusCode.NotFound);

        var crossTenantKey = $"cross-tenant-feedback-{Guid.NewGuid():N}";
        using var crossTenantFeedback = await SendJsonAsync(
            tenantA,
            HttpMethod.Post,
            $"/api/opportunity-priorities/{recommendationB.Id}/feedback",
            new
            {
                ExpectedRecommendationId = recommendationB.Id,
                Decision = OpportunityFeedbackDecision.Accepted,
                ReplacementActionCode = (string?)null,
                Reason = "Cross-tenant authorization probe.",
                SupersedesFeedbackId = (long?)null,
                CorrelationId = crossTenantKey,
                IdempotencyKey = crossTenantKey
            },
            crossTenantKey);
        await AssertStatusAsync(crossTenantFeedback, HttpStatusCode.NotFound);
    }

    private HttpClient Client(long roleId, long? tenantId, long userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId, userId));
        return client;
    }

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method == HttpMethod.Post.Method
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : null
    };

    private static Task<HttpResponseMessage> ReconcileAsync(HttpClient client, string key) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/opportunity-priorities/reconcile",
            new { CorrelationId = key, IdempotencyKey = key },
            key);

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string key)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Correlation-ID", key);
        return await client.SendAsync(request);
    }

    private async Task PrepareManagerRolesAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        if (await db.SetupMasters.IgnoreQueryFilters().AnyAsync(x => x.SetupId == TenantAManagerRole))
            return;

        db.SetupMasters.AddRange(
            Role(TenantAManagerRole, Release01BHttpApplication.TenantA, "Opportunity Manager A", RoleRanks.Manager),
            Role(TenantBManagerRole, Release01BHttpApplication.TenantB, "Opportunity Manager B", RoleRanks.Manager));
        db.RolePermissions.AddRange(
            Permission(848_301, TenantAManagerRole, Release01BHttpApplication.TenantA),
            Permission(848_302, TenantBManagerRole, Release01BHttpApplication.TenantB));
        await db.SaveChangesAsync();
    }

    private static SetupMaster Role(long id, long tenantId, string name, short rank) =>
        Release01BHttpApplication.Role(id, tenantId, name, rank, "opportunity-priority-http-tests");

    private static RolePermission Permission(long id, long roleId, long tenantId) => new()
    {
        Id = id,
        RoleId = roleId,
        ModuleId = LeadsModule,
        BusinessUnitId = tenantId,
        CanEdit = true,
        CreatedBy = "opportunity-priority-http-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, received {(int)response.StatusCode}: {body}");
    }
}
