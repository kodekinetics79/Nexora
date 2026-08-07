using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// RED TEAM. Can the irreversible operation reach a tenant that is no longer leaving?
///
/// <para><b>The trap.</b> Scheduling a deletion requires <see cref="TenantStatus.Archived"/> and
/// says so loudly. Nothing UNSCHEDULES it when the tenant comes back. <c>TenantsController</c>'s
/// <c>Restore</c> (Archived → Suspended) and <c>Resume</c> (Suspended → Active) are
/// <c>Platform.TenantAdmin</c> — Owner OR SupportAdmin — and neither touches
/// <c>platform."TenantOffboardings"</c>: the controller does not reference the type at all. The
/// retention clock therefore keeps running underneath a live, serving, paying customer, and
/// <see cref="TenantOffboardingService.PurgeAsync"/> re-checks the stage, the eligibility date, the
/// typed name and the primary business unit — and never the tenant's status.</para>
///
/// <para>The asymmetry is visible in a single expression in <c>ToStatusDto</c>:
/// <c>CanScheduleDeletion</c> is <c>… &amp;&amp; tenant.Status == DeletionRequiresStatus</c> while
/// <c>CanPurge</c> is <c>CanPurge(stage) &amp;&amp; eligible</c>. The status guard is on the
/// reversible door and not on the irreversible one. <c>ErasePersonalDataAsync</c> — the OTHER
/// destructive verb — does check status, which is what makes this an omission rather than a
/// policy.</para>
///
/// <para><b>Why the operator does not notice.</b> <c>ListPendingDeletionsAsync</c> — the queue an
/// Owner works from — projects the tenant's Name and Slug and NOT its Status, so a reactivated
/// tenant sits in the pending-deletion list looking exactly like one that is still leaving.</para>
/// </summary>
public sealed class RedTeamPurgeReachabilityTests
{
    private const long BusinessUnitId = 91_401;

    private const string ScheduleReason =
        "Contract terminated on 2026-07-31; customer confirmed offboarding in writing.";
    private const string PurgeReason =
        "Retention window elapsed; destroying records per the terminated contract.";

    /// <summary>
    /// FINDING R0 (data loss). The purge proceeds against a tenant that has been restored and
    /// resumed and is now <see cref="TenantStatus.Active"/>.
    ///
    /// <para>SKIPPED because it FAILS: it proves the defect. Remove the Skip to see it.</para>
    /// </summary>
    [Fact]
    public async Task A_purge_refuses_a_tenant_that_was_brought_back_to_life()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await ArmedForPurgeAsync(db);
        await SetStatusAsync(db, tenant.Id, TenantStatus.Active);

        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);

        // Refused for the right reason, not merely refused: the portable harness has no owner
        // connection, so a purge that gets PAST the guards dies on the connection instead.
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = PurgeReason, Confirmation = tenant.Name },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Contains("Active", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The regression that pins finding R0 shut, asserted at the point it used to fail open.
    ///
    /// <para>This began as a passing REPRODUCTION: the purge ran past every guard against a live
    /// tenant and died only when it reached for the owner connection the portable harness does not
    /// have — by which point it had already COMMITTED its intent to destroy, a
    /// <c>PurgeStarted</c> event and a <c>PurgeStartedOn</c> timestamp written against a tenant
    /// recorded as Active. On a real database the next statement was the DELETE sweep.</para>
    ///
    /// <para>It now asserts the inverse, and deliberately checks the record as well as the verdict:
    /// a refusal that had already written its intent would still leave an offboarding row claiming
    /// a destruction had begun, which is the state an operator would later have to interpret.</para>
    /// </summary>
    [Fact]
    public async Task A_tenant_brought_back_to_life_is_refused_before_any_intent_to_destroy_is_written()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await ArmedForPurgeAsync(db);

        // Exactly what TenantsController.Restore (Archived -> Suspended) and .Resume
        // (Suspended -> Active) do — both [Authorize(Policy = PlatformPolicies.TenantAdmin)],
        // so a SupportAdmin can perform them, and neither touches platform."TenantOffboardings".
        await SetStatusAsync(db, tenant.Id, TenantStatus.Active);

        await using (var context = db.ContextFor(null))
        {
            var service = TenantLifecycleHarness.Service(context);

            var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
                service.PurgeAsync(tenant.Id,
                    new ConfirmTenantDestructionRequest { Reason = PurgeReason, Confirmation = tenant.Name },
                    TenantLifecycleHarness.Operator(), null, CancellationToken.None));

            // The message has to name the actual obstacle. "Cancel the scheduled deletion" is the
            // only instruction that resolves this state, and the operator reading it may not be the
            // one who scheduled it.
            Assert.Contains("Active", refusal.Message);
            Assert.Contains("Cancel the scheduled deletion", refusal.Message);
        }

        await using (var verify = db.ContextFor(null))
        {
            var record = await verify.Set<TenantOffboarding>().AsNoTracking()
                .SingleAsync(r => r.TenantId == tenant.Id);
            Assert.Null(record.PurgeStartedOn);
            Assert.Equal(TenantOffboardingStage.PendingDeletion, record.Stage);

            var events = await verify.Set<TenantLifecycleEvent>().AsNoTracking()
                .Where(e => e.TenantId == tenant.Id).Select(e => e.Action).ToListAsync();
            Assert.DoesNotContain(TenantLifecycleActions.PurgeStarted, events);
        }
    }

    /// <summary>
    /// The contrast that makes it an omission rather than a decision: on the SAME Active tenant,
    /// the two operations that DO check status both refuse.
    /// </summary>
    [Fact]
    public async Task The_reversible_door_and_the_erasure_both_check_status_on_the_same_tenant()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await ArmedForPurgeAsync(db);
        await SetStatusAsync(db, tenant.Id, TenantStatus.Active);

        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);
        var actor = TenantLifecycleHarness.Operator();

        var scheduling = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.ScheduleDeletionAsync(tenant.Id,
                new ScheduleTenantDeletionRequest { Reason = ScheduleReason }, actor, null,
                CancellationToken.None));
        Assert.Contains("Archived", scheduling.Message, StringComparison.OrdinalIgnoreCase);

        var erasure = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.ErasePersonalDataAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = ScheduleReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));
        Assert.Contains("Active", erasure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why nobody sees it coming: the Owner's pending-deletion work queue does not carry the
    /// tenant's status, so a reactivated customer is indistinguishable from one still leaving.
    /// </summary>
    [Fact]
    public async Task REPRODUCTION_the_pending_deletion_queue_does_not_show_that_a_tenant_came_back()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await ArmedForPurgeAsync(db);
        await SetStatusAsync(db, tenant.Id, TenantStatus.Active);

        await using var context = db.ContextFor(null);
        var pending = await TenantLifecycleHarness.Service(context)
            .ListPendingDeletionsAsync(CancellationToken.None);

        var row = Assert.Single(pending);
        Assert.Equal(tenant.Id, row.TenantId);
        Assert.True(row.IsPurgeEligible);

        // There is no status on the DTO at all — not "it says Active", but "it cannot say".
        Assert.Null(typeof(PendingTenantDeletionDto).GetProperty("Status"));
        Assert.Null(typeof(PendingTenantDeletionDto).GetProperty("TenantStatus"));
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>An Archived tenant, scheduled for deletion, whose retention window has elapsed.</summary>
    private static async Task<Tenant> ArmedForPurgeAsync(TenantLifecycleTestDb db)
    {
        await using (var seed = db.ContextFor(null))
        {
            Support.Seed.BusinessUnit(seed, BusinessUnitId);
            await seed.SaveChangesAsync();
        }

        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "redteam-resurrected", TenantStatus.Archived, BusinessUnitId);

        await using var context = db.ContextFor(null);
        await TenantLifecycleHarness.Service(context).ScheduleDeletionAsync(
            tenant.Id, new ScheduleTenantDeletionRequest { Reason = ScheduleReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);
        await TenantLifecycleHarness.ElapseRetentionWindowAsync(context, tenant.Id);

        return tenant;
    }

    private static async Task SetStatusAsync(TenantLifecycleTestDb db, long tenantId, TenantStatus status)
    {
        await using var context = db.ContextFor(null);
        var tenant = await context.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        tenant.Status = status;
        await context.SaveChangesAsync();
    }
}
