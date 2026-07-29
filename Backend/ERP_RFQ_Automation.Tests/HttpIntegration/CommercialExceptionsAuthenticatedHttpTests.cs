using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Exceptions;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class CommercialExceptionsAuthenticatedHttpTests(Release01BHttpApplication app)
{
    private const long ManagerRole = 829_101;
    private const long IndividualRole = 829_102;
    private const long TenantBManagerRole = 829_103;
    private const long ManagerUser = 839_101;
    private const long OwnerUser = 839_102;
    private const long OtherUser = 839_103;
    private const long TenantBManagerUser = 839_104;
    private const long LeadsModule = 84_001;

    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { HttpMethod.Get.Method, "/api/commercial-exceptions" },
        { HttpMethod.Post.Method, "/api/commercial-exceptions/refresh" },
        { HttpMethod.Post.Method, "/api/commercial-exceptions/1/transition" }
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Routes_challenge_unauthenticated_requests(string method, string path)
    {
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Routes_forbid_roles_without_leads_permission(string method, string path)
    {
        using var client = Client(Release01BHttpApplication.DeniedRole,
            Release01BHttpApplication.TenantA, ManagerUser);

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Routes_forbid_authenticated_requests_without_tenant_context()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, null, ManagerUser);

        using var response = await client.GetAsync("/api/commercial-exceptions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Manager_and_individual_reads_are_scoped_and_cross_tenant_transition_is_not_found()
    {
        await PrepareAuthorizationAndSourcesAsync();
        using var manager = Client(ManagerRole, Release01BHttpApplication.TenantA, ManagerUser);
        using var owner = Client(IndividualRole, Release01BHttpApplication.TenantA, OwnerUser);
        using var other = Client(IndividualRole, Release01BHttpApplication.TenantA, OtherUser);
        using var tenantBManager = Client(TenantBManagerRole,
            Release01BHttpApplication.TenantB, TenantBManagerUser);

        using var refreshA = await RefreshAsync(manager, "http-commercial-exceptions-a");
        await AssertStatusAsync(refreshA, HttpStatusCode.OK);
        using var refreshB = await RefreshAsync(tenantBManager, "http-commercial-exceptions-b");
        await AssertStatusAsync(refreshB, HttpStatusCode.OK);

        using var managerResponse = await manager.GetAsync("/api/commercial-exceptions");
        await AssertStatusAsync(managerResponse, HttpStatusCode.OK);
        using var managerPayload = JsonDocument.Parse(await managerResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, managerPayload.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, managerPayload.RootElement.GetProperty("active").GetInt32());
        Assert.Equal("tenant", managerPayload.RootElement.GetProperty("scope").GetString(), ignoreCase: true);
        Assert.Equal("complete", managerPayload.RootElement.GetProperty("coverageStatus").GetString(), ignoreCase: true);
        Assert.All(managerPayload.RootElement.GetProperty("sourceCoverage").EnumerateArray(),
            source => Assert.True(source.GetProperty("isAvailable").GetBoolean()));
        Assert.Equal(2, managerPayload.RootElement.GetProperty("items").GetArrayLength());
        Assert.All(managerPayload.RootElement.GetProperty("items").EnumerateArray(),
            item => Assert.True(item.GetProperty("sourceVersion").GetInt64() >= 1));

        using var ownerResponse = await owner.GetAsync("/api/commercial-exceptions");
        await AssertStatusAsync(ownerResponse, HttpStatusCode.OK);
        using var ownerPayload = JsonDocument.Parse(await ownerResponse.Content.ReadAsStringAsync());
        var owned = Assert.Single(ownerPayload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(nameof(CommercialExceptionType.OverdueFollowUp),
            owned.GetProperty("exceptionType").GetString());
        Assert.Equal(OwnerUser, owned.GetProperty("ownerUserId").GetInt64());

        using var otherResponse = await other.GetAsync("/api/commercial-exceptions");
        await AssertStatusAsync(otherResponse, HttpStatusCode.OK);
        using var otherPayload = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, otherPayload.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(otherPayload.RootElement.GetProperty("items").EnumerateArray());

        using var tenantBResponse = await tenantBManager.GetAsync("/api/commercial-exceptions");
        await AssertStatusAsync(tenantBResponse, HttpStatusCode.OK);
        using var tenantBPayload = JsonDocument.Parse(await tenantBResponse.Content.ReadAsStringAsync());
        var tenantBExceptionId = tenantBPayload.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        using var crossTenant = await SendJsonAsync(manager, HttpMethod.Post,
            $"/api/commercial-exceptions/{tenantBExceptionId}/transition", new
            {
                ExpectedVersion = 1,
                TargetStatus = nameof(CommercialExceptionStatus.Acknowledged),
                ActionCode = "ACKNOWLEDGE",
                Reason = "Cross-tenant probe",
                CorrelationId = "http-commercial-cross-tenant",
                IdempotencyKey = "http-commercial-cross-tenant",
                ActorId = "forged-actor"
            }, "http-commercial-cross-tenant");
        await AssertStatusAsync(crossTenant, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_retry_with_same_idempotency_key_and_new_correlation_replays_exact_result()
    {
        await PrepareAuthorizationAndSourcesAsync();
        using var manager = Client(ManagerRole, Release01BHttpApplication.TenantA, ManagerUser);
        var key = $"http-retry-{Guid.NewGuid():N}";

        using var first = await SendJsonAsync(manager, HttpMethod.Post,
            "/api/commercial-exceptions/refresh",
            new { CorrelationId = "transport-one", IdempotencyKey = key }, key, "transport-one");
        await AssertStatusAsync(first, HttpStatusCode.OK);
        var firstBody = await first.Content.ReadAsStringAsync();
        using var retry = await SendJsonAsync(manager, HttpMethod.Post,
            "/api/commercial-exceptions/refresh",
            new { CorrelationId = "transport-two", IdempotencyKey = key }, key, "transport-two");
        await AssertStatusAsync(retry, HttpStatusCode.OK);

        Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
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

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string key) =>
        SendJsonAsync(client, HttpMethod.Post, "/api/commercial-exceptions/refresh", new
        {
            CorrelationId = key,
            IdempotencyKey = key,
            ActorId = "forged-actor"
        }, key);

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string key,
        string? correlationId = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Correlation-ID", correlationId ?? key);
        return await client.SendAsync(request);
    }

    private async Task PrepareAuthorizationAndSourcesAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        if (!await db.SetupMasters.IgnoreQueryFilters().AnyAsync(x => x.SetupId == ManagerRole))
        {
            db.SetupMasters.AddRange(
                Role(ManagerRole, Release01BHttpApplication.TenantA, "Commercial Manager"),
                Role(IndividualRole, Release01BHttpApplication.TenantA, "Sales Representative"),
                Role(TenantBManagerRole, Release01BHttpApplication.TenantB, "Commercial Manager"));
            db.RolePermissions.AddRange(
                Permission(859_101, ManagerRole, Release01BHttpApplication.TenantA, canEdit: true),
                Permission(859_102, IndividualRole, Release01BHttpApplication.TenantA),
                Permission(859_103, TenantBManagerRole, Release01BHttpApplication.TenantB, canEdit: true));
            db.Users.AddRange(
                User(ManagerUser, Release01BHttpApplication.TenantA, "Manager"),
                User(OwnerUser, Release01BHttpApplication.TenantA, "Owner"),
                User(OtherUser, Release01BHttpApplication.TenantA, "Other"),
                User(TenantBManagerUser, Release01BHttpApplication.TenantB, "ManagerB"));
        }

        await AddSourcesAsync(db, Release01BHttpApplication.TenantA,
            Release01BHttpApplication.TenantALeadId, OwnerUser, 869_100);
        await AddSourcesAsync(db, Release01BHttpApplication.TenantB,
            Release01BHttpApplication.TenantBLeadId, TenantBManagerUser, 879_100);
        await db.SaveChangesAsync();
    }

    private static async Task AddSourcesAsync(
        ErpRfqAutomationContext db,
        long tenantId,
        long leadId,
        long ownerUserId,
        long offset)
    {
        if (!await db.Set<UnassignedWorkItem>().IgnoreQueryFilters()
            .AnyAsync(x => x.BusinessUnitId == tenantId && x.Id == offset + 2))
        {
            var decision = new LeadRoutingDecision
            {
                Id = offset + 1,
                BusinessUnitId = tenantId,
                LeadId = leadId,
                MatchStatus = CustomerMatchStatus.NoEvidence,
                Outcome = RoutingOutcome.Unassigned,
                DecisionCode = "NO_OWNER",
                Explanation = "{\"reason\":\"No deterministic owner was found.\"}",
                PolicyVersion = "routing-v1",
                CorrelationId = $"http-routing-{tenantId}",
                IdempotencyKey = $"http-routing-{tenantId}",
                CreatedOn = DateTime.UtcNow.AddHours(-3)
            };
            db.Add(new UnassignedWorkItem
            {
                Id = offset + 2,
                BusinessUnitId = tenantId,
                LeadId = leadId,
                RoutingDecision = decision,
                ReasonCode = "NO_OWNER",
                Status = WorkItemStatus.Open,
                Priority = 90,
                EnteredOn = DateTime.UtcNow.AddHours(-3),
                SlaDueOn = DateTime.UtcNow.AddHours(-1),
                RequiredAction = "Assign an owner",
                IdempotencyKey = $"http-unassigned-{tenantId}",
                Version = 1
            });
        }

        if (!await db.Set<FollowUpTask>().IgnoreQueryFilters()
            .AnyAsync(x => x.BusinessUnitId == tenantId && x.Id == offset + 3))
        {
            db.Add(new FollowUpTask
            {
                Id = offset + 3,
                BusinessUnitId = tenantId,
                AssignedToUserId = ownerUserId,
                AggregateType = CommercialAggregateType.Lead,
                AggregateId = leadId,
                DueAtUtc = DateTime.UtcNow.AddHours(-2),
                Status = FollowUpStatus.Open,
                Priority = 80,
                PurposeCode = "CUSTOMER_RESPONSE",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
                Version = 1,
                CreatedBy = "tests",
                CorrelationId = $"http-follow-up-{tenantId}",
                CreationIdempotencyKey = $"http-follow-up-{tenantId}"
            });
        }
    }

    private static SetupMaster Role(long id, long tenantId, string name) => new()
    {
        SetupId = id,
        SetupType = "Role",
        SetupCode = name.Replace(' ', '_').ToUpperInvariant(),
        SetupValue = name,
        BusinessUnitId = tenantId,
        IsActive = true,
        CreatedBy = "commercial-exception-http-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static RolePermission Permission(long id, long roleId, long tenantId, bool canEdit = false) => new()
    {
        Id = id,
        RoleId = roleId,
        ModuleId = LeadsModule,
        BusinessUnitId = tenantId,
        CanEdit = canEdit,
        CreatedBy = "commercial-exception-http-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static User User(long id, long tenantId, string firstName) => new()
    {
        Id = id,
        Buid = tenantId,
        FirstName = firstName,
        LastName = "Tester",
        Email = $"commercial-{id}@nexora.invalid",
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        IsActive = true,
        CreatedBy = "commercial-exception-http-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, received {(int)response.StatusCode}: {body}");
    }
}
