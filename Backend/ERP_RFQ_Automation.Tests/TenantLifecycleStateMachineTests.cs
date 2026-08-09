using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The offboarding state machine and its guards.
///
/// <para>Everything asserted here happens BEFORE any row is destroyed, which is why it can run on
/// the portable provider: a guard's whole value is that the destructive code is never reached. The
/// destruction itself, and the isolation and survival properties that only a real database can
/// demonstrate, are in <c>TenantLifecyclePostgreSqlTests</c>.</para>
/// </summary>
public sealed class TenantLifecycleStateMachineTests
{
    private const string GoodReason = "Contract terminated on 2026-07-31; customer confirmed offboarding.";

    // ---------------------------------------------------------------- entering the path

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.PastDue)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Provisioning)]
    public async Task Deletion_can_only_be_scheduled_for_an_archived_tenant(TenantStatus status)
    {
        // Archived is already the "this customer has left" decision and is only reachable through
        // Suspended, so a deletion can never be the first anybody hears that a tenant is going.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, $"status-{status}", status);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).ScheduleDeletionAsync(
                tenant.Id, new ScheduleTenantDeletionRequest { Reason = GoodReason },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        await using var verify = db.ContextFor(null);
        Assert.Empty(await verify.Set<TenantOffboarding>().ToListAsync());
        Assert.Empty(await verify.Set<TenantLifecycleEvent>().ToListAsync());
        Assert.Empty(await verify.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task Scheduling_starts_the_retention_clock_and_records_the_transition()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "clock", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var before = DateTime.UtcNow;
        var status = await TenantLifecycleHarness.Service(context).ScheduleDeletionAsync(
            tenant.Id, new ScheduleTenantDeletionRequest { Reason = GoodReason, RetentionDays = 30 },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        Assert.Equal(nameof(TenantOffboardingStage.PendingDeletion), status.Stage);
        Assert.Equal(30, status.RetentionDays);
        Assert.False(status.IsPurgeEligible);
        Assert.False(status.CanPurge);
        Assert.True(status.CanCancelDeletion);

        // The clock is stored, not derived: the window a customer was promised is a commitment
        // made on a date, and re-deriving it lets a later default change move it.
        await using var verify = db.ContextFor(null);
        var record = await verify.Set<TenantOffboarding>().SingleAsync();
        Assert.Equal(TenantOffboardingStage.PendingDeletion, record.Stage);
        Assert.NotNull(record.PurgeEligibleOn);
        Assert.InRange(
            record.PurgeEligibleOn!.Value,
            before.AddDays(30).AddSeconds(-30), DateTime.UtcNow.AddDays(30).AddSeconds(30));

        var lifecycle = await verify.Set<TenantLifecycleEvent>().SingleAsync();
        Assert.Equal(TenantLifecycleActions.ScheduleDeletion, lifecycle.Action);
        Assert.Equal(nameof(TenantOffboardingStage.NotScheduled), lifecycle.FromStage);
        Assert.Equal(nameof(TenantOffboardingStage.PendingDeletion), lifecycle.ToStage);
        Assert.Equal(GoodReason, lifecycle.Reason);

        var audit = await verify.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal(TenantLifecycleActions.ScheduleDeletion, audit.Action);
        Assert.Equal(tenant.Id, audit.ActAsTenantId);
        Assert.Equal(PlatformAuditResults.Success, audit.Result);
    }

    [Fact]
    public async Task The_tenant_stays_archived_for_the_whole_of_the_offboarding_path()
    {
        // The property the entire two-axis design exists to guarantee: TenantStatus never leaves
        // Archived, so TenantAccessSnapshot.IsAccessDenied, the extraction queue's blocked_tenants
        // CTE and the billing run's exclusion all keep working with no change.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "still-archived", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        await TenantLifecycleHarness.Service(context).ScheduleDeletionAsync(
            tenant.Id, new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        await using var verify = db.ContextFor(null);
        var persisted = await verify.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal(TenantStatus.Archived, persisted.Status);
    }

    [Fact]
    public async Task Scheduling_a_deletion_twice_is_refused()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "twice", TenantStatus.Archived);
        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.ScheduleDeletionAsync(tenant.Id,
                new ScheduleTenantDeletionRequest { Reason = GoodReason, RetentionDays = 7 },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
    }

    // ---------------------------------------------------------------- the retention window

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(TenantLifecycleOptions.AbsoluteMaximumRetentionDays + 1)]
    public async Task A_retention_window_outside_the_permitted_range_is_refused_not_clamped(int days)
    {
        // Refused rather than clamped: a caller who asked for three days and silently received
        // thirty has been told something untrue about when the data goes.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, $"window-{days}", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).ScheduleDeletionAsync(
                tenant.Id, new ScheduleTenantDeletionRequest { Reason = GoodReason, RetentionDays = days },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(400, refusal.SuggestedStatusCode);
        await using var verify = db.ContextFor(null);
        Assert.Empty(await verify.Set<TenantOffboarding>().ToListAsync());
    }

    [Fact]
    public async Task Configuration_cannot_lower_the_platform_retention_floor()
    {
        // The floor is a constant, not a setting. Every other number in TenantLifecycleOptions can
        // be tuned; this one is what makes "there is a retention window" a true statement about
        // the system rather than about its current configuration file.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "floor", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var permissive = new TenantLifecycleOptions { MinimumRetentionDays = 1, DefaultRetentionDays = 1 };
        var service = TenantLifecycleHarness.Service(context, options: permissive);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.ScheduleDeletionAsync(tenant.Id,
                new ScheduleTenantDeletionRequest { Reason = GoodReason, RetentionDays = 1 },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(400, refusal.SuggestedStatusCode);
        Assert.Contains(TenantLifecycleOptions.AbsoluteMinimumRetentionDays.ToString(), refusal.Message);

        // And the default is raised to the floor rather than honoured at one day.
        var status = await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);
        Assert.Equal(TenantLifecycleOptions.AbsoluteMinimumRetentionDays, status.RetentionDays);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("done")]
    public async Task Scheduling_requires_a_reason_a_reader_could_understand_later(string reason)
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, $"reason-{reason.Length}", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).ScheduleDeletionAsync(
                tenant.Id, new ScheduleTenantDeletionRequest { Reason = reason },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(400, refusal.SuggestedStatusCode);
    }

    // ---------------------------------------------------------------- leaving the path

    [Fact]
    public async Task Cancelling_clears_the_clock_and_leaves_both_transitions_on_the_record()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "cancel", TenantStatus.Archived);
        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        var status = await service.CancelDeletionAsync(tenant.Id,
            new CancelTenantDeletionRequest { Reason = "Customer renewed." },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        Assert.Equal(nameof(TenantOffboardingStage.NotScheduled), status.Stage);
        Assert.Null(status.PurgeEligibleOn);
        Assert.Null(status.RetentionDays);
        Assert.False(status.CanPurge);
        Assert.True(status.CanScheduleDeletion);

        await using var verify = db.ContextFor(null);
        var history = await verify.Set<TenantLifecycleEvent>()
            .OrderBy(e => e.Id).Select(e => e.Action).ToListAsync();
        Assert.Equal(
            [TenantLifecycleActions.ScheduleDeletion, TenantLifecycleActions.CancelDeletion], history);
    }

    [Fact]
    public async Task Cancelling_when_nothing_is_scheduled_is_refused()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "nothing", TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).CancelDeletionAsync(
                tenant.Id, new CancelTenantDeletionRequest { Reason = "None" },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
    }

    [Fact]
    public async Task Rescheduling_after_a_cancellation_starts_a_full_fresh_window()
    {
        // The clock is cleared, not paused. Resuming where it left off would give a tenant
        // cancelled at day 29 a one-day window on the second attempt.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "fresh", TenantStatus.Archived);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);
        await service.CancelDeletionAsync(tenant.Id,
            new CancelTenantDeletionRequest { Reason = "Paused." }, actor, null, CancellationToken.None);

        var status = await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);

        Assert.False(status.IsPurgeEligible);
        Assert.True(status.DaysUntilPurgeEligible >= TenantLifecycleOptions.AbsoluteMinimumRetentionDays);
    }

    // ---------------------------------------------------------------- the purge guards

    [Fact]
    public async Task A_purge_cannot_run_before_the_retention_window_elapses()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "too-soon", TenantStatus.Archived, 4_401);
        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("retention window", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // Not merely refused — not even STARTED. Intent is committed before destruction, so a
        // PurgeStarted row here would mean the guard ran after the point of no return.
        await using var verify = db.ContextFor(null);
        var record = await verify.Set<TenantOffboarding>().SingleAsync();
        Assert.Equal(TenantOffboardingStage.PendingDeletion, record.Stage);
        Assert.Null(record.PurgeStartedOn);
        Assert.DoesNotContain(
            await verify.Set<TenantLifecycleEvent>().Select(e => e.Action).ToListAsync(),
            action => action == TenantLifecycleActions.PurgeStarted);
    }

    [Fact]
    public async Task Seven_day_boundary_uses_the_application_clock_without_rewriting_the_schedule()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "seven-day-boundary", TenantStatus.Archived, 4_405);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason, RetentionDays = 7 },
            actor, null, CancellationToken.None);

        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        var early = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));
        Assert.Contains("retention window", early.Message, StringComparison.OrdinalIgnoreCase);

        await using (var beforeBoundary = db.ContextFor(null))
            Assert.Null((await beforeBoundary.Set<TenantOffboarding>().SingleAsync()).PurgeStartedOn);

        clock.Advance(TimeSpan.FromSeconds(1));
        // Exact eligibility passes the clock guard and reaches the deliberately unreachable owner
        // connection. The visible start/failure events prove the boundary passed without editing
        // PurgeEligibleOn in storage.
        await Assert.ThrowsAnyAsync<Exception>(() => service.PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
            actor, null, CancellationToken.None));

        await using var verify = db.ContextFor(null);
        var actions = await verify.Set<TenantLifecycleEvent>().Select(x => x.Action).ToListAsync();
        Assert.Contains(TenantLifecycleActions.PurgeStarted, actions);
        Assert.Contains(TenantLifecycleActions.PurgeFailed, actions);
    }

    [Fact]
    public async Task A_purge_whose_confirmation_does_not_name_the_tenant_is_refused()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "wrong-word", TenantStatus.Archived, 4_402);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        foreach (var attempt in new[] { "yes", "DELETE", tenant.Slug, tenant.Name.ToUpperInvariant() })
        {
            var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
                service.PurgeAsync(tenant.Id,
                    new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = attempt },
                    actor, null, CancellationToken.None));
            Assert.Equal(400, refusal.SuggestedStatusCode);
            Assert.Contains(tenant.Name, refusal.Message);
        }

        await using var verify = db.ContextFor(null);
        Assert.Null((await verify.Set<TenantOffboarding>().SingleAsync()).PurgeStartedOn);
    }

    [Fact]
    public async Task There_is_no_path_that_purges_a_tenant_without_a_scheduled_deletion()
    {
        // The retention window is the only control standing between a decision and an
        // irreversible act. A control that can be skipped by choosing a different endpoint is not
        // one, so there is deliberately no direct Archived -> Purged transition.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "direct", TenantStatus.Archived, 4_403);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("scheduled deletion", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_purge_that_fails_stays_pending_records_the_attempt_and_releases_its_lease()
    {
        // The ordering discipline, observed from the outside. Intent is committed before the
        // destruction opens, so the forbidden state — recorded as purged, data still present — is
        // structurally impossible, and the tolerable one is visible and re-runnable.
        //
        // PurgeStartedOn is also the concurrency lease, so a failure the service can SEE releases
        // it: the operator retries at once rather than waiting out a window that exists only for a
        // process that died without telling anyone. The durable record that it was attempted is
        // the PurgeStarted event, not the lease column.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "half", TenantStatus.Archived, 4_404);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        // The owner connection the purge needs is unreachable here, which is exactly the shape of
        // a purge that dies mid-flight.
        await Assert.ThrowsAnyAsync<Exception>(() => service.PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
            actor, null, CancellationToken.None));

        await using var verify = db.ContextFor(null);
        var record = await verify.Set<TenantOffboarding>().SingleAsync();
        Assert.Equal(TenantOffboardingStage.PendingDeletion, record.Stage);
        Assert.Null(record.PurgedOn);
        Assert.Null(record.PurgeStartedOn);
        Assert.Equal(GoodReason, record.PurgeReason);

        var actions = await verify.Set<TenantLifecycleEvent>().OrderBy(e => e.Id)
            .Select(e => e.Action).ToListAsync();
        Assert.Contains(TenantLifecycleActions.PurgeStarted, actions);
        Assert.Contains(TenantLifecycleActions.PurgeFailed, actions);
        Assert.DoesNotContain(TenantLifecycleActions.Purged, actions);

        var failure = await verify.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == TenantLifecycleActions.PurgeFailed);
        Assert.Equal(PlatformAuditResults.Failure, failure.Result);
    }

    [Fact]
    public async Task Only_one_purge_of_a_tenant_may_be_in_flight_at_a_time()
    {
        // FINDING R11. Every guard before the claim is a READ, so two Owners hitting purge at the
        // same instant both passed all of them, both swept, and the loser committed a completion
        // reporting zero rows destroyed — an offboarding record stating the tenant held nothing.
        // The claim is now a compare-and-set on the lease column, which the database serialises.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "race", TenantStatus.Archived, 4_440);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        // The first attempt claims the lease and then dies in the destructive step, exactly as a
        // crashed process would — leaving the lease held.
        await Assert.ThrowsAnyAsync<Exception>(() => service.PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
            actor, null, CancellationToken.None));

        // That failure was SEEN, so the lease was released and a retry is allowed immediately.
        await using (var reclaim = db.ContextFor(null))
            Assert.Null((await reclaim.Set<TenantOffboarding>().SingleAsync()).PurgeStartedOn);

        // Now simulate a purge that is genuinely still running: the lease is held and fresh.
        await using (var inFlight = db.ContextFor(null))
        {
            var record = await inFlight.Set<TenantOffboarding>().SingleAsync();
            record.PurgeStartedOn = clock.GetUtcNow().UtcDateTime;
            await inFlight.SaveChangesAsync();
        }

        await using var second = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(second, timeProvider: clock).PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("already in progress", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // And it refused BEFORE writing a second intent — the loser leaves no trace suggesting a
        // destruction it never performed.
        await using var verify = db.ContextFor(null);
        Assert.Equal(1, await verify.Set<TenantLifecycleEvent>()
            .CountAsync(e => e.Action == TenantLifecycleActions.PurgeStarted));
    }

    [Fact]
    public async Task A_stale_lease_from_a_dead_process_can_be_taken_over()
    {
        // The other half: a purge whose process died cannot block the tenant forever.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "stale", TenantStatus.Archived, 4_441);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        await using (var abandoned = db.ContextFor(null))
        {
            var record = await abandoned.Set<TenantOffboarding>().SingleAsync();
            record.PurgeStartedOn = clock.GetUtcNow().UtcDateTime
                                    - TenantOffboardingService.PurgeLease.Add(TimeSpan.FromMinutes(1));
            await abandoned.SaveChangesAsync();
        }

        await using var retry = db.ContextFor(null);
        // Reaches the destructive step (and fails there for want of a database) rather than being
        // refused by the lease — which is the assertion.
        var thrown = await Record.ExceptionAsync(() =>
            TenantLifecycleHarness.Service(retry, timeProvider: clock).PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.IsNotType<TenantOffboardingRefusedException>(thrown);
    }

    // ------------------------------------------------- coming back to life mid-retention window

    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Active)]
    public async Task A_tenant_restored_during_its_retention_window_cannot_be_purged(TenantStatus restoredTo)
    {
        // Restore and resume live on TenantsController and know nothing about a scheduled
        // deletion, so the schedule survives them. The status guard is what stops the purge while
        // the tenant is back in service.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, $"restored-{restoredTo}", TenantStatus.Archived, 4_450);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        await using (var restore = db.ContextFor(null))
        {
            var live = await restore.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenant.Id);
            live.Status = restoredTo;
            await restore.SaveChangesAsync();
        }

        await using var verify = db.ContextFor(null);
        var reloaded = TenantLifecycleHarness.Service(verify, timeProvider: clock);

        var status = await reloaded.GetStatusAsync(tenant.Id, CancellationToken.None);
        Assert.Equal(nameof(TenantOffboardingStage.PendingDeletion), status.Stage);
        Assert.True(status.IsPurgeEligible);   // the clock did elapse
        Assert.False(status.CanPurge);         // and it still may not be destroyed

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            reloaded.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));
        Assert.Equal(409, refusal.SuggestedStatusCode);

        // Refused before any intent to destroy was recorded.
        Assert.Null((await verify.Set<TenantOffboarding>().SingleAsync()).PurgeStartedOn);
    }

    [Fact]
    public async Task A_window_that_was_spent_back_in_service_does_not_justify_a_purge()
    {
        // The subtler half of the same hazard, and the one the status guard does NOT catch. A
        // tenant restored on day two, traded for a month and archived again for an unrelated
        // reason arrives at the purge Archived, with an elapsed window it spent alive. The
        // retention promise was about a tenant that was offboarded throughout.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "revived", TenantStatus.Archived, 4_451);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);

        // The restore, as TenantsController records it, and the re-archive afterwards.
        await using (var revived = db.ContextFor(null))
        {
            revived.Set<PlatformAuditLog>().Add(new PlatformAuditLog
            {
                ActorPlatformUserId = 7,
                ActAsTenantId = tenant.Id,
                Action = "tenant.restore",
                TargetType = nameof(Tenant),
                TargetId = tenant.Id.ToString(),
                CreatedOn = DateTime.UtcNow
            });
            await revived.SaveChangesAsync();
        }
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        await using var verify = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(verify, timeProvider: clock).PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("brought back into service", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await verify.Set<TenantOffboarding>().SingleAsync()).PurgeStartedOn);
    }

    [Fact]
    public async Task Cancelling_and_rescheduling_clears_the_restore_and_lets_the_purge_proceed()
    {
        // The documented way out: the operator cancels and schedules again, which starts a fresh
        // window and puts both decisions on the record. The new schedule post-dates the restore,
        // so the coherence guard is satisfied by construction rather than by an override.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "resched", TenantStatus.Archived, 4_452);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var actor = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        await using (var revived = db.ContextFor(null))
        {
            revived.Set<PlatformAuditLog>().Add(new PlatformAuditLog
            {
                ActorPlatformUserId = 7, ActAsTenantId = tenant.Id, Action = "tenant.restore",
                TargetType = nameof(Tenant), TargetId = tenant.Id.ToString(),
                CreatedOn = DateTime.UtcNow
            });
            await revived.SaveChangesAsync();
        }

        await service.CancelDeletionAsync(tenant.Id,
            new CancelTenantDeletionRequest { Reason = "Customer returned." }, actor, null, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = "Second offboarding; renewal lapsed again." },
            actor, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        await using var verify = db.ContextFor(null);
        // Reaches the destructive step rather than being refused: the guard is satisfied.
        var thrown = await Record.ExceptionAsync(() =>
            TenantLifecycleHarness.Service(verify, timeProvider: clock).PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.IsNotType<TenantOffboardingRefusedException>(thrown);
    }

    // ---------------------------------------------------------------- erasure, the other axis

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.PastDue)]
    [InlineData(TenantStatus.Provisioning)]
    public async Task Personal_data_cannot_be_erased_while_the_tenant_is_still_being_served(TenantStatus status)
    {
        // Erasure deactivates every user and replaces their sign-in address. On a live tenant that
        // is an unannounced outage, not a compliance action.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, $"live-{status}", status, 4_410);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).ErasePersonalDataAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
    }

    [Fact]
    public async Task Erasure_replaces_the_people_and_leaves_the_commercial_records_standing()
    {
        const long BusinessUnit = 4_411;
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "erase", TenantStatus.Suspended, BusinessUnit);

        await using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 900_101, BusinessUnit);
            seed.Users.Add(new User
            {
                FirstName = "Dana", LastName = "Rowe", Email = "dana@customer.test",
                PasswordHash = "hashed", ImageUrl = "https://cdn/avatar.png", Buid = BusinessUnit,
                IsActive = true, Timezone = "Asia/Riyadh", CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var result = await TenantLifecycleHarness.Service(context).ErasePersonalDataAsync(tenant.Id,
            new ConfirmTenantDestructionRequest
            {
                Reason = "Article 17 erasure request received from the data subject on 2026-08-01.",
                Confirmation = tenant.Name
            },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        Assert.True(result.IdentitiesErased > 0);
        Assert.Contains(TenantOffboardingDisclosure.ErasureIsNotDeletion, result.Disclosures);

        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Buid == BusinessUnit);
        Assert.DoesNotContain("dana", user.Email, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("@erased.invalid", user.Email, StringComparison.Ordinal);
        Assert.Equal("Erased", user.FirstName);
        Assert.False(user.IsActive);
        Assert.NotEqual("hashed", user.PasswordHash);

        // The whole point: the commercial record is untouched. An erasure that took the leads with
        // it would break the statutory retention it exists alongside.
        Assert.Equal(1, await verify.Leads.IgnoreQueryFilters().CountAsync(l => l.BusinessUnitId == BusinessUnit));

        // And it is NOT a move along the deletion path.
        var record = await verify.Set<TenantOffboarding>().SingleAsync();
        Assert.Equal(TenantOffboardingStage.NotScheduled, record.Stage);
        Assert.NotNull(record.PersonalDataErasedOn);
    }

    [Fact]
    public async Task Erasure_does_not_schedule_deletion_and_scheduling_does_not_mark_erasure_complete()
    {
        // The operations remain distinct: erasure does not schedule deletion, and scheduling does
        // not fabricate erasure proof. The readiness gate composes them only at final purge.
        using var db = new TenantLifecycleTestDb();
        var erasedOnly = await TenantLifecycleHarness.SeedTenantAsync(
            db, "erased-only", TenantStatus.Suspended, 4_420);
        var scheduledThenErased = await TenantLifecycleHarness.SeedTenantAsync(
            db, "both", TenantStatus.Archived, 4_421);

        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);
        var actor = TenantLifecycleHarness.Operator();
        var erasureReason = "Article 17 erasure request received from the data subject.";

        var erased = await service.ErasePersonalDataAsync(erasedOnly.Id,
            new ConfirmTenantDestructionRequest { Reason = erasureReason, Confirmation = erasedOnly.Name },
            actor, null, CancellationToken.None);
        Assert.Equal(erasedOnly.Id, erased.TenantId);

        await service.ScheduleDeletionAsync(scheduledThenErased.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        await service.ErasePersonalDataAsync(scheduledThenErased.Id,
            new ConfirmTenantDestructionRequest
            { Reason = erasureReason, Confirmation = scheduledThenErased.Name },
            actor, null, CancellationToken.None);

        await using var verify = db.ContextFor(null);

        var erasedRecord = await verify.Set<TenantOffboarding>()
            .SingleAsync(r => r.TenantId == erasedOnly.Id);
        Assert.Equal(TenantOffboardingStage.NotScheduled, erasedRecord.Stage);
        Assert.NotNull(erasedRecord.PersonalDataErasedOn);

        var bothRecord = await verify.Set<TenantOffboarding>()
            .SingleAsync(r => r.TenantId == scheduledThenErased.Id);
        Assert.Equal(TenantOffboardingStage.PendingDeletion, bothRecord.Stage);
        Assert.NotNull(bothRecord.PersonalDataErasedOn);
        Assert.NotNull(bothRecord.PurgeEligibleOn);
    }

    [Fact]
    public async Task A_purged_tenant_has_nothing_left_to_erase()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "gone", TenantStatus.Archived, 4_430);
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<TenantOffboarding>().Add(new TenantOffboarding
            {
                TenantId = tenant.Id, Stage = TenantOffboardingStage.Purged,
                PurgedOn = DateTime.UtcNow.AddDays(-1), PurgedBy = "operator@example.test"
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context).ErasePersonalDataAsync(tenant.Id,
                new ConfirmTenantDestructionRequest
                { Reason = "Article 17 erasure request received.", Confirmation = tenant.Name },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
    }

    // ---------------------------------------------------------------- the history itself

    [Fact]
    public async Task The_history_reconstructs_the_offboarding_and_names_the_tenant_on_every_row()
    {
        // The identity is copied onto every event rather than joined, because these rows have to
        // stay readable after the tenant they describe has been destroyed.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(db, "replay", TenantStatus.Archived);
        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);
        var actor = TenantLifecycleHarness.Operator("compliance@example.test", 11);

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, actor, null, CancellationToken.None);
        await service.CancelDeletionAsync(tenant.Id,
            new CancelTenantDeletionRequest { Reason = "Renewal signed." }, actor, null, CancellationToken.None);
        var status = await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = "Renewal lapsed; offboarding resumed." },
            actor, null, CancellationToken.None);

        Assert.Equal(3, status.History.Count);
        Assert.Equal(
            [TenantLifecycleActions.ScheduleDeletion, TenantLifecycleActions.CancelDeletion,
             TenantLifecycleActions.ScheduleDeletion],
            status.History.Select(e => e.Action).ToArray());
        Assert.All(status.History, e => Assert.Equal("compliance@example.test", e.ActorEmail));

        await using var verify = db.ContextFor(null);
        var events = await verify.Set<TenantLifecycleEvent>().ToListAsync();
        Assert.All(events, e =>
        {
            Assert.Equal(tenant.Slug, e.TenantSlug);
            Assert.Equal(tenant.Name, e.TenantName);
            Assert.Equal(nameof(TenantStatus.Archived), e.TenantStatus);
            Assert.False(string.IsNullOrWhiteSpace(e.Reason));
        });
    }

    [Fact]
    public async Task The_pending_queue_lists_only_tenants_whose_clock_is_running()
    {
        using var db = new TenantLifecycleTestDb();
        var pending = await TenantLifecycleHarness.SeedTenantAsync(db, "queued", TenantStatus.Archived);
        await TenantLifecycleHarness.SeedTenantAsync(db, "not-queued", TenantStatus.Archived);

        await using var context = db.ContextFor(null);
        var service = TenantLifecycleHarness.Service(context);
        await service.ScheduleDeletionAsync(pending.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        var queue = await service.ListPendingDeletionsAsync(CancellationToken.None);

        var only = Assert.Single(queue);
        Assert.Equal(pending.Id, only.TenantId);
        Assert.Equal(pending.Slug, only.TenantSlug);
        Assert.False(only.IsPurgeEligible);
        Assert.True(only.DaysUntilPurgeEligible > 0);
    }
}
