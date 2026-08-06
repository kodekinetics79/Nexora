using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// WP-B2: the missing write path for <c>sales_rep_profiles</c>.
///
/// <para><c>SalesApplicationService.UpsertProfileAsync</c> was fully implemented — validation,
/// optimistic concurrency, idempotent replay — and reachable from no controller. The table
/// therefore held zero rows, the routing engine's eligibility gate could never be satisfied,
/// and 44 of 44 production leads routed to <c>NO_MATCH_EVIDENCE</c>/Unassigned. Routing was
/// never broken; it simply had no reps to choose from.</para>
///
/// <para>These tests pin the governance of the new endpoint. They are attribute- and
/// contract-level deliberately: the service behind it already carries its own validation tests,
/// and duplicating those here would assert the same logic twice.</para>
/// </summary>
public sealed class SalesRepRoutingProfileEndpointTests
{
    private static MethodInfo Action => typeof(CommercialIntelligenceController)
        .GetMethod(nameof(CommercialIntelligenceController.UpsertRepRoutingProfile))!;

    [Fact]
    public void TheWritePathExists_AndIsAPost()
    {
        // The regression this whole work package closes: before it, this method did not exist
        // and no HTTP route reached UpsertProfileAsync at all.
        Assert.NotNull(Action);
        var post = Assert.Single(Action.GetCustomAttributes<HttpPostAttribute>(true));
        Assert.Equal("reps/{userId:long}/routing-profile", post.Template);
    }

    [Fact]
    public void ItRequiresTheSamePermissionAsTheRestOfRepAdministration()
    {
        // "Users" + Edit, matching the existing reps endpoints — not a new module permission
        // invented for this endpoint, which nobody's roles would grant.
        var permissions = Action.GetCustomAttributes<RequireModulePermissionAttribute>(true);
        Assert.Contains(permissions, p => p.Policy == "ModulePermission:Users:Edit");
    }

    [Fact]
    public void ItRequiresAManagerRole()
    {
        // Deciding who leads route to is a management action. Without this, any user holding
        // Users:Edit could make themselves the routing target for every inbound RFQ.
        Assert.NotEmpty(Action.GetCustomAttributes<RequireManagerRoleAttribute>(true));
    }

    [Fact]
    public void TheBodyCannotSupplyTheActor_TheTenant_OrTheUserId()
    {
        // Everything that decides WHO is being changed and BY WHOM is server-derived: the rep
        // from the route, the tenant from the claim, the actor from the principal, the
        // idempotency key from the header. A request body that could set them would let a
        // manager write a profile into another tenant, or attribute the change to someone else.
        var bodyProperties = typeof(UpsertRepRoutingProfileRequest)
            .GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("UserId", bodyProperties);
        Assert.DoesNotContain("BusinessUnitId", bodyProperties);
        Assert.DoesNotContain("ActorId", bodyProperties);
        Assert.DoesNotContain("IdempotencyKey", bodyProperties);
    }

    [Fact]
    public void TheBodyCarriesEveryFieldTheRoutingEngineActuallyReads()
    {
        // Guards against the endpoint silently exposing only part of the profile, which would
        // leave territory or capacity permanently at their defaults with no way to set them.
        var bodyProperties = typeof(UpsertRepRoutingProfileRequest)
            .GetProperties().Select(p => p.Name).ToArray();

        foreach (var required in new[]
                 {
                     nameof(UpsertRepRoutingProfileRequest.IsRoutingEligible),
                     nameof(UpsertRepRoutingProfileRequest.CapacityPercent),
                     nameof(UpsertRepRoutingProfileRequest.DistributionWeight),
                     nameof(UpsertRepRoutingProfileRequest.TerritoryKeys),
                     nameof(UpsertRepRoutingProfileRequest.ProductCategoryKeys),
                     nameof(UpsertRepRoutingProfileRequest.EffectiveFromUtc),
                     nameof(UpsertRepRoutingProfileRequest.ExpectedVersion)
                 })
            Assert.Contains(required, bodyProperties);
    }

    [Fact]
    public void ADefaultBodyDescribesAnEligibleRepAtFullCapacity()
    {
        // The common case — "make this person routable" — must not require the caller to know
        // the engine's internals.
        var request = new UpsertRepRoutingProfileRequest();

        Assert.True(request.IsRoutingEligible);
        Assert.Equal(100, request.CapacityPercent);
        Assert.Equal(1m, request.DistributionWeight);
        // 0 means "create"; the service refuses 0 against an existing profile, so a second
        // caller cannot blindly overwrite the first.
        Assert.Equal(0, request.ExpectedVersion);
    }

    [Fact]
    public void TheServiceContractItCallsIsUnchanged()
    {
        // The endpoint is a write path onto an EXISTING service — no new engine, no new rules
        // table. If this signature ever changes, that reuse claim needs re-checking.
        var method = typeof(ISalesApplicationService).GetMethod(nameof(ISalesApplicationService.UpsertProfileAsync));
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(typeof(long), parameters[0].ParameterType);
        Assert.Equal(typeof(UpsertSalesRepProfileCommand), parameters[1].ParameterType);
    }
}
