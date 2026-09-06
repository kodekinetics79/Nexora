using System.Security.Claims;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// One call, one justification, one verdict.
///
/// <para>Opening extraction for an external destination takes a policy update AND a destination
/// grant, behind two separate dialogs, of which the prominent one is the grant — and a grant
/// alone permits nothing. The reliably reachable state was therefore a tenant carrying a live
/// authorization, a policy nobody had edited, and a console reporting blocking controls: which is
/// exactly the state the pilot tenant was found in. These pin that the guided endpoint cannot
/// produce it, that it never takes a destination from the caller, and that it refuses the two
/// answers that look like decisions and are not.</para>
/// </summary>
public sealed class AiGuidedEnablementTests
{
    private const long TenantRowId = 77_001;
    private const long BusinessUnitId = 92_501;
    private const string Endpoint = "https://ollama.com";
    private const string Model = "deepseek-v4-pro";

    [Fact]
    public async Task ApprovedCloud_OpensThePolicyAndTheGrantTogether_AndReportsReady()
    {
        using var harness = new Harness();

        var result = await harness.Post(Request());

        var body = Assert.IsType<TenantAiEnablementResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Readiness.Ready);
        Assert.Equal(0, body.Readiness.BlockingCount);
        Assert.Equal(0, body.Readiness.WarningCount);

        // The destination is taken from the resolved descriptor, never from the caller — which is
        // what retires the ORDINAL, case-sensitive AllowedModel comparison: nobody types it.
        Assert.Equal(Model, body.Policy.AllowedModel);
        Assert.Equal("Ollama", body.Policy.AllowedProvider);
        Assert.True(body.Policy.ExternalProcessingAllowed);
        Assert.Equal(AiEgressPolicies.FullDocument, body.Policy.EgressPolicy);
        Assert.True(body.Policy.RedactionRequired);
        Assert.True(body.Policy.PrivacyReviewRequired);
        Assert.Equal(2_000_000, body.Policy.MonthlyHardTokenLimit);

        // ...and the grant the old flow made an operator author separately, with the same words.
        var grant = Assert.Single(await harness.Grants());
        Assert.Equal(Endpoint, grant.Endpoint);
        Assert.Equal(Model, grant.Model);
        Assert.True(grant.UnstructuredDocumentsAllowed);
        Assert.Contains("DPA ref", grant.Justification, StringComparison.Ordinal);
        Assert.Equal("user:7", grant.AuthorizedBy);
    }

    [Fact]
    public async Task RedactedFieldsOnly_LeavesWholeDocumentsShut_WithoutFailingTheCall()
    {
        // The safer half of the consent question. It is a real answer, not a partial one: the
        // call succeeds, and the report says plainly that whole documents will not go.
        using var harness = new Harness();

        var result = await harness.Post(Request(cloudEgress: AiEgressPolicies.RedactedFieldsOnly));

        var body = Assert.IsType<TenantAiEnablementResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(AiEgressPolicies.RedactedFieldsOnly, body.Policy.EgressPolicy);
        Assert.False(body.Readiness.Ready);
        Assert.Equal("egress_policy_forbids_whole_documents", body.Readiness.FirstBlockingReason);
        Assert.False(Assert.Single(await harness.Grants()).UnstructuredDocumentsAllowed);
    }

    [Fact]
    public async Task UnboundedSpend_HasToBeChosen_NotOmitted()
    {
        using var harness = new Harness();

        // Neither answer given, and both given, are the same mistake: nobody decided.
        foreach (var ambiguous in new[]
                 {
                     Request(hardLimit: null, noCeiling: false),
                     Request(hardLimit: 2_000_000, noCeiling: true)
                 })
        {
            var refused = Assert.IsType<BadRequestObjectResult>((await harness.Post(ambiguous)).Result);
            Assert.Contains("Exactly one of the two", refused.Value!.ToString(), StringComparison.Ordinal);
        }
        Assert.Empty(await harness.Grants());

        // Chosen deliberately, it is accepted — and the report says what it costs.
        var chosen = await harness.Post(Request(hardLimit: null, noCeiling: true));
        var body = Assert.IsType<TenantAiEnablementResult>(Assert.IsType<OkObjectResult>(chosen.Result).Value);
        Assert.Null(body.Policy.MonthlyHardTokenLimit);
        Assert.True(body.Readiness.Ready);
        Assert.Equal(1, body.Readiness.WarningCount);
    }

    [Fact]
    public async Task AZeroCeilingIsRefused_BecauseItIsAKillSwitchWearingABudgetsClothes()
    {
        using var harness = new Harness();

        var refused = Assert.IsType<BadRequestObjectResult>((await harness.Post(Request(hardLimit: 0))).Result);

        Assert.Contains("Off posture", refused.Value!.ToString(), StringComparison.Ordinal);
        Assert.Empty(await harness.Grants());
    }

    [Fact]
    public async Task ARefusedAnswerWritesNothingAtAll()
    {
        // The failure mode this endpoint exists to remove is the half-written tenant, so a
        // refusal must not leave a grant behind for a policy that never opened.
        using var harness = new Harness();
        var beforeVersion = (await harness.Policy()).Version;

        var refused = await harness.Post(Request(justification: "ok"));

        Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Empty(await harness.Grants());
        var policy = await harness.Policy();
        Assert.Equal(beforeVersion, policy.Version);
        Assert.False(policy.ExternalProcessingAllowed);
    }

    [Fact]
    public async Task AStalePolicyVersionIsRefusedBeforeAnythingIsWritten()
    {
        using var harness = new Harness();

        var stale = await harness.Post(Request(version: 99));

        Assert.IsType<ConflictObjectResult>(stale.Result);
        Assert.Empty(await harness.Grants());
    }

    [Fact]
    public async Task Off_ClosesProcessingAndTheProviderLock_WithoutRevokingAnybodysGrant()
    {
        // Revocation is a deliberate, separately attributed act. Turning AI off is the master
        // switch; it must not quietly rewrite the tenant's egress history to look like a
        // decision somebody made about a destination.
        using var harness = new Harness();
        await harness.Post(Request());

        var off = await harness.Post(Request(posture: AiPostures.Off, version: 2));

        var body = Assert.IsType<TenantAiEnablementResult>(Assert.IsType<OkObjectResult>(off.Result).Value);
        Assert.False(body.Policy.IsEnabled);
        Assert.False(body.Policy.ExternalProcessingAllowed);
        Assert.Null(body.Policy.AllowedModel);
        Assert.False(body.Readiness.Ready);
        var grant = Assert.Single(await harness.Grants());
        Assert.Null(grant.RevokedOn);
    }

    [Fact]
    public async Task GoingBeyondThePlansPackage_IsRefusedUntilSomebodyOwnsIt()
    {
        // The plan is what the customer bought. Cloud extraction on a plan that sells private
        // extraction is a deliberate exception, and an exception with nobody's name on it becomes
        // the permanent configuration nobody can explain.
        using var harness = new Harness(planPackage: AiPackages.Private, planAllowance: 2_000_000);

        var refused = await harness.Post(Request());

        var bad = Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Contains("Private extraction", bad.Value!.ToString(), StringComparison.Ordinal);
        Assert.Empty(await harness.Grants());
        Assert.False((await harness.Policy()).ExternalProcessingAllowed);
    }

    [Fact]
    public async Task AnOwnedException_IsRecordedOnTheRow_AndClearedWhenItEnds()
    {
        using var harness = new Harness(planPackage: AiPackages.Private, planAllowance: 2_000_000);

        var granted = await harness.Post(Request(deviationReason: "Pilot extension agreed with Intelliflow IT, ref INTF-114."));

        var body = Assert.IsType<TenantAiEnablementResult>(Assert.IsType<OkObjectResult>(granted.Result).Value);
        Assert.Contains(body.Policy.PlanDeviations, x => x.Contains("approved cloud provider", StringComparison.Ordinal));
        var policy = await harness.Policy();
        Assert.Contains("INTF-114", policy.PlanDeviationReason!, StringComparison.Ordinal);
        Assert.Equal("info@kodekinetics.com", policy.PlanDeviationApprovedBy);
        Assert.NotNull(policy.PlanDeviationApprovedOn);

        // Back inside the plan, and the approval goes with it: a stale approver on a tenant that
        // no longer deviates reads as an approval that is still in force.
        var back = await harness.Post(Request(posture: AiPostures.Off, version: 2));
        Assert.IsType<OkObjectResult>(back.Result);
        var settled = await harness.Policy();
        Assert.Null(settled.PlanDeviationReason);
        Assert.Null(settled.PlanDeviationApprovedBy);
        Assert.Null(settled.PlanDeviationApprovedOn);
    }

    [Fact]
    public async Task StayingInsideThePlansPackage_NeedsNoException()
    {
        using var harness = new Harness(planPackage: AiPackages.Cloud, planAllowance: 2_000_000);

        var result = await harness.Post(Request());

        var body = Assert.IsType<TenantAiEnablementResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(body.Policy.PlanDeviations);
        Assert.Equal(AiPackages.Cloud, body.Policy.PlanAiPackage);
        Assert.Null((await harness.Policy()).PlanDeviationReason);
    }

    private static TenantAiEnablementRequest Request(
        string posture = AiPostures.ApprovedCloud,
        string? cloudEgress = AiEgressPolicies.FullDocument,
        long? hardLimit = 2_000_000,
        bool noCeiling = false,
        long version = 1,
        string? deviationReason = null,
        string justification = "Signed DPA ref INTF-2026-114, clause 4.2.") => new()
    {
        PlanDeviationReason = deviationReason,
        Posture = posture,
        CloudEgress = cloudEgress,
        Purposes = [AiPurposes.RfqExtraction],
        MonthlyHardTokenLimit = hardLimit,
        NoMonthlyCeiling = noCeiling,
        Version = version,
        Justification = justification
    };

    /// <summary>
    /// The controller over a real SQLite database with the real gate, the real pre-flight and the
    /// real audit service behind it — the endpoint's whole value is that those three agree after
    /// it returns, and a mocked one of them would assert nothing.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly TestDb _db = new();
        private readonly ServiceProvider _services;
        private readonly ErpRfqAutomationContext _outer;

        public Harness(string? planPackage = null, long? planAllowance = null)
        {
            using (var seed = _db.ContextFor(null))
            {
                Seed.EnsureBusinessUnit(seed, BusinessUnitId);
                long? planId = null;
                if (planPackage is not null)
                {
                    var plan = new Plan
                    {
                        Code = "internal-pilot-qa",
                        Name = "Internal Pilot QA",
                        AiPackage = planPackage,
                        AiMonthlyTokenAllowance = planAllowance,
                        AiAllowanceUnlimited = planAllowance is null
                    };
                    seed.Set<Plan>().Add(plan);
                    seed.SaveChanges();
                    planId = plan.Id;
                }
                seed.Set<Tenant>().Add(new Tenant
                {
                    Id = TenantRowId,
                    PlanId = planId,
                    Name = "Intelliflow Systems",
                    Slug = "intelliflow",
                    Status = TenantStatus.Active,
                    PrimaryBusinessUnitId = BusinessUnitId,
                    CreatedBy = "test",
                    CreatedOn = DateTime.UtcNow
                });
                seed.AiProcessingPolicies.Add(
                    AiProcessingPolicy.CreateSecureDefault(BusinessUnitId, "tenant-provisioning", DateTime.UtcNow));
                seed.SaveChanges();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = Endpoint + "/",
                ["Ollama:Model"] = Model
            }).Build();

            var scopeAccessor = new TenantScopeAccessor();
            _services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<ITenantScopeAccessor>(scopeAccessor)
                .AddSingleton<ITenantContext>(new StubTenant(BusinessUnitId))
                .AddSingleton<IAiProviderEndpointResolver>(new AiProviderEndpointResolver(
                    configuration, NullLogger<AiProviderEndpointResolver>.Instance))
                .AddScoped(_ => _db.ContextFor(scopeAccessor.BusinessUnitId ?? BusinessUnitId))
                .AddScoped<AiExternalProviderTrustService>()
                .AddScoped<AiExtractionReadinessService>()
                .AddScoped<IPlatformAuditService, PlatformAuditService>()
                .BuildServiceProvider();

            _outer = _db.ContextFor(null);
            ScopeAccessor = scopeAccessor;
        }

        private TenantScopeAccessor ScopeAccessor { get; }

        public Task<ActionResult<TenantAiEnablementResult>> Post(TenantAiEnablementRequest request) =>
            Controller().SetAiEnablement(TenantRowId, request, CancellationToken.None);

        public async Task<List<AiExternalProviderAuthorization>> Grants()
        {
            await using var read = _db.ContextFor(null);
            return await read.AiExternalProviderAuthorizations.IgnoreQueryFilters()
                .Where(x => x.BusinessUnitId == BusinessUnitId).ToListAsync();
        }

        public async Task<AiProcessingPolicy> Policy()
        {
            await using var read = _db.ContextFor(null);
            return await read.AiProcessingPolicies.IgnoreQueryFilters()
                .SingleAsync(x => x.BusinessUnitId == BusinessUnitId);
        }

        private TenantsController Controller()
        {
            var http = new DefaultHttpContext { User = Owner() };
            http.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString();
            return new TenantsController(
                _outer,
                new PlatformAuditService(_outer, NullLogger<PlatformAuditService>.Instance),
                NullLogger<TenantsController>.Instance,
                _services.GetRequiredService<IServiceScopeFactory>(),
                ScopeAccessor,
                ProvisioningFixture.Baseline(_outer),
                ProvisioningFixture.Invitations(_outer))
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
        }

        private static ClaimsPrincipal Owner() => new(new ClaimsIdentity(
        [
            new Claim("sub", "7"),
            new Claim("email", "info@kodekinetics.com"),
            new Claim("platformRole", "Owner")
        ], "Platform"));

        public void Dispose()
        {
            _outer.Dispose();
            _services.Dispose();
            _db.Dispose();
        }
    }
}
