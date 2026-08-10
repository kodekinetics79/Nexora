using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Sec-D1. Tenant-status enforcement and plan limits must not be switched off by the platform
/// plane being unreadable.
///
/// <para><b>The defect these pin shut.</b> <c>TenantAccessService</c> wrapped its whole resolution
/// in <c>catch (Exception) → allow</c>, returning the identical snapshot the legacy-BU path
/// returns: no tenant, no status, no plan. <c>IsAccessDenied</c> was therefore false and the
/// status-guard middleware admitted the request. The failure that catch was most likely to see is
/// a <c>42501</c> on the column-scoped platform read — which is a WHOLE-DEPLOYMENT condition, not
/// a per-tenant blip: any build cut between 20260805105320 (which narrowed the tenant plane to
/// column-level SELECT) and 20260808163605 (which granted <c>Plans."Features"</c>, projected by
/// <c>CoreQuery</c>) answers it on every request. Suspension, past-due gating, archival and every
/// plan limit were off for every tenant, re-decided every ten seconds, with one log line.</para>
///
/// <para><b>Why these tests can exist at all.</b> The portable lane is SQLite, which has neither
/// roles nor column privileges, so no test could ever have reproduced the 42501 itself — which is
/// exactly why the defect survived. What is asserted here instead is the BEHAVIOUR the 42501
/// produces: a resolution that throws. That is reproducible anywhere, and it is the thing that
/// actually decides whether a tenant is admitted.</para>
/// </summary>
public sealed class TenantAccessFailClosedTests
{
    private const long Bu = 4242;

    // ---- the resolution itself -------------------------------------------

    /// <summary>
    /// A context whose platform read cannot complete — the portable-lane stand-in for the
    /// <c>42501</c> a missing column grant produces. Built over its own empty database, so the
    /// platform tables genuinely are not readable and the query genuinely throws; the SqlState
    /// carried by a real 42501 is irrelevant to the decision under test, because the code this
    /// replaces caught EVERY exception and answered "allow".
    ///
    /// <para>Deliberately NOT the shared <see cref="TestDb"/> connection: breaking that would
    /// break the recovery half of these tests too, and a test that cannot show recovery is not
    /// showing that the refusal is bounded.</para>
    /// </summary>
    private static (TenantAccessService Service, IDisposable Owner) BrokenPlane(IMemoryCache cache)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open(); // opened, but EnsureCreated is never called: no platform tables exist
        var context = new ErpRfqAutomationContext(
            new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseSqlite(connection).Options,
            new StubTenant(null));
        return (new TenantAccessService(context, cache, NullLogger<TenantAccessService>.Instance), connection);
    }

    [Fact]
    public async Task An_unreadable_platform_plane_resolves_as_UNRESOLVABLE_and_denies()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (service, owner) = BrokenPlane(cache);
        using var _ = owner;

        var access = await service.GetAccessAsync(Bu);

        // The three assertions that fail the moment the `catch → allow` is restored.
        Assert.True(access.IsUnresolvable);
        Assert.Equal(TenantAccessResolution.Unresolvable, access.Resolution);
        Assert.True(access.IsAccessDenied);

        // And it must not be mistakable for the legacy-BU snapshot, which is the shape the
        // fail-open returned and which is still admitted.
        Assert.False(access.HasTenant);
        Assert.NotNull(access.UnresolvedReason);
    }

    [Fact]
    public async Task A_business_unit_with_no_tenant_row_is_still_RESOLVED_and_still_admitted()
    {
        // The other half of the distinction, and the reason this is not simply "deny on null".
        // A legacy BusinessUnit with no platform Tenant row is a FACT the plane answered with;
        // turning that into a denial would refuse every such customer on the strength of a row
        // that was never expected to exist.
        using var db = new TestDb();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, Bu);
        context.SaveChanges();

        var access = await new TenantAccessService(
            context, cache, NullLogger<TenantAccessService>.Instance).GetAccessAsync(Bu);

        Assert.False(access.IsUnresolvable);
        Assert.Equal(TenantAccessResolution.Resolved, access.Resolution);
        Assert.False(access.HasTenant);
        Assert.False(access.IsAccessDenied);
    }

    [Fact]
    public async Task The_refusal_is_cached_only_briefly_so_a_restored_grant_recovers_without_a_restart()
    {
        // A denial that outlived the outage would turn one missing grant into a manual
        // intervention on every node. The short TTL is the recovery path, so it is asserted.
        using var db = new TestDb();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var (service, owner) = BrokenPlane(cache);
        using var ownerScope = owner;

        var denied = await service.GetAccessAsync(Bu);
        Assert.True(denied.IsUnresolvable);
        Assert.True(cache.TryGetValue(TenantAccessService.CacheKey(Bu), out _));

        Assert.True(TenantAccessService.FailureCacheTtl < TenantAccessService.CacheTtl);
        Assert.True(TenantAccessService.FailureCacheTtl <= TimeSpan.FromSeconds(30));

        // Evicting stands in for the TTL elapsing: the next resolution must re-read the plane
        // rather than be permanently latched to the refusal.
        cache.Remove(TenantAccessService.CacheKey(Bu));
        using var healthy = db.ContextFor(null);
        Seed.BusinessUnit(healthy, Bu);
        healthy.SaveChanges();
        var recovered = await new TenantAccessService(
            healthy, cache, NullLogger<TenantAccessService>.Instance).GetAccessAsync(Bu);
        Assert.False(recovered.IsUnresolvable);
    }

    // ---- the request path -------------------------------------------------

    private sealed class FixedAccess(TenantAccessSnapshot snapshot) : ITenantAccessService
    {
        public Task<TenantAccessSnapshot> GetAccessAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(snapshot with { BusinessUnitId = businessUnitId });
    }

    private static DefaultHttpContext GuardContext(ITenantAccessService access)
    {
        var services = new ServiceCollection();
        services.AddSingleton(access);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = "/api/Lead";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("businessUnitId", Bu.ToString())], "test"));
        return context;
    }

    [Fact]
    public async Task The_status_guard_refuses_an_unresolvable_tenant_with_503_instead_of_admitting_it()
    {
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(
            TenantAccessSnapshot.Unresolved(Bu, "platform plane unreadable")));

        await middleware.InvokeAsync(context);

        // THE assertion. Before Sec-D1 nextCalled was true and the request was served.
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        // 503 and not 403: we cannot claim the tenant is suspended, only that we could not tell.
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(context.Response.Headers.RetryAfter.ToString()));
    }

    [Fact]
    public async Task A_resolved_active_tenant_is_still_served()
    {
        // The control that proves the test above is not simply "the guard denies everything".
        var nextCalled = false;
        var middleware = new TenantStatusGuardMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = GuardContext(new FixedAccess(
            new TenantAccessSnapshot(Bu, 1, TenantStatus.Active, null)));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    // ---- entitlements -----------------------------------------------------

    [Fact]
    public async Task Every_entitlement_check_denies_when_the_platform_plane_did_not_answer()
    {
        // An unresolvable snapshot has a null Plan, and every limit below treats a null plan as
        // "no limit". Without an explicit guard the outage would hand out unlimited seats,
        // unlimited documents and every typed feature — the same fail-open one layer down.
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var service = new EntitlementService(
            new FixedAccess(TenantAccessSnapshot.Unresolved(Bu, "platform plane unreadable")), context);

        Assert.False((await service.CheckSeatAvailabilityAsync(Bu)).Allowed);
        Assert.False((await service.CheckDocumentQuotaAsync(Bu)).Allowed);
        Assert.False((await service.CheckFeatureAsync(Bu, TypedEntitlementCatalog.RuntimeAvailableKeys.First())).Allowed);
    }

    // ---- the grant contract ----------------------------------------------

    [Fact]
    public void Every_field_of_the_plan_projection_has_a_column_in_the_boot_grant_assertion()
    {
        // The drift that produced the defect, made mechanical. `Plan.Features` was added to
        // PlanSnapshot and therefore to CoreQuery's projection, and the GRANT for it arrived in a
        // migration three days later. Anyone adding the next property to PlanSnapshot without
        // adding its column here fails this test rather than shipping a build that answers 42501
        // on every tenant request.
        var projected = typeof(PlanSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var granted = TenantAccessGrantContract.RequiredColumns
            .Where(column => column.QualifiedTable.Contains("Plans", StringComparison.Ordinal))
            .Select(column => column.Column)
            .ToHashSet(StringComparer.Ordinal);

        var ungranted = projected.Except(granted).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(ungranted.Count == 0,
            "These PlanSnapshot fields are read from platform.\"Plans\" by TenantAccessService but "
            + "are not in TenantAccessGrantContract.RequiredColumns, so a deployment missing their "
            + "GRANT would answer 42501 on every tenant request and nothing would say so at boot:\n  "
            + string.Join("\n  ", ungranted));
    }

    [Fact]
    public void The_boot_grant_assertion_covers_both_roles_the_resolution_can_execute_as()
    {
        // nexora_identity_app serves /api/Auth/Login. A grant that covered only the tenant role
        // would let the platform boot and then refuse every SIGN-IN, which is the same outage
        // wearing a different hat.
        Assert.Contains("nexora_tenant_app", TenantAccessGrantContract.ExecutionRoles);
        Assert.Contains("nexora_identity_app", TenantAccessGrantContract.ExecutionRoles);

        // Status and PlanId are what suspension and plan limits are actually decided from.
        Assert.Contains(TenantAccessGrantContract.RequiredColumns,
            column => column.QualifiedTable.Contains("Tenants", StringComparison.Ordinal)
                      && column.Column == "Status");
        Assert.Contains(TenantAccessGrantContract.RequiredColumns,
            column => column.QualifiedTable.Contains("Plans", StringComparison.Ordinal)
                      && column.Column == "Features");
    }
}
