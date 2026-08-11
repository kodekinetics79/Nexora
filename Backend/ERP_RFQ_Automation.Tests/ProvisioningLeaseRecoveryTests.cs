using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Governed recovery of a provisioning execution whose runner died holding it, and the resume
/// that follows.
///
/// <para><b>The state this pins.</b> An execution is claimed under a lease that expires, so a dead
/// node does not park a tenant forever — but "expired" is a deadline computed from a configuration
/// value, not evidence that anything died. An operator staring at an execution stuck in
/// <c>Running</c> had two options: wait, or write the lease columns by hand. The second is a lease
/// steal with no reason, no record and nothing stopping it landing on an execution a healthy
/// runner was in the middle of. Each test below pins one property of the path that replaced
/// it.</para>
/// </summary>
public sealed class ProvisioningLeaseRecoveryTests
{
    private const string Actor = "owner@nexora.app";

    // ---- 1. stale execution recovery ---------------------------------------------------------

    [Fact]
    public async Task A_stale_execution_is_recovered_and_resumes_at_the_first_incomplete_step()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        // A real half-provision: the founding-admin step fails on an address a rival holds, so
        // five steps commit and three do not.
        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("stalled-co", "ada@stalled.test", planId));
        var executionId = submitted.Execution!.Id;
        var rivalId = await SeedRivalUserAsync(harness, "ada@stalled.test");
        await harness.Runner().RunAsync(executionId);

        var failed = await harness.ReloadAsync(executionId);
        Assert.Equal(ProvisioningExecutionState.Failed, failed.State);
        var businessUnitId = failed.ProvisionedBusinessUnitId!.Value;

        // Now the shape a node that died mid-step actually leaves behind: still Running, still
        // naming an owner, with a lease and a heartbeat that stopped moving twenty minutes ago.
        await StrandAsync(harness, executionId, silentFor: TimeSpan.FromMinutes(20));

        using (var scope = harness.Scope())
        {
            var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();

            var assessment = await recovery.AssessAsync(executionId);
            Assert.NotNull(assessment);
            Assert.Equal(ProvisioningLeaseStaleness.Abandoned, assessment!.Staleness);
            Assert.True(assessment.IsRecoverable);
            // Stated before anything is authorised: this is where the work picks up.
            Assert.Equal(ProvisioningStepCodes.FoundingAdmin, assessment.FirstIncompleteStep);
            Assert.Contains(ProvisioningStepCodes.Tenant, assessment.CompletedSteps);
            Assert.Contains(ProvisioningStepCodes.FoundingRole, assessment.CompletedSteps);

            var result = await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
                executionId, "Runner node was terminated mid-step during a deploy.", Actor, 7));

            Assert.Equal(ProvisioningRecoveryOutcome.Recovered, result.Outcome);
            Assert.Equal("dead-node:1", result.Before!.LeaseOwner);
            Assert.Null(result.After!.LeaseOwner);
            Assert.Null(result.After.LeaseToken);
            Assert.Null(result.After.LeaseUntil);
            Assert.Equal(nameof(ProvisioningExecutionState.Pending), result.After.State);

            // THE property that makes a resume a resume: the request identity is untouched, so the
            // work continues under the key the caller already holds rather than as a second
            // provision wearing the same name.
            Assert.Equal(result.Before.IdempotencyKey, result.After.IdempotencyKey);
            Assert.Equal(result.Before.RequestFingerprint, result.After.RequestFingerprint);
        }

        var recovered = await harness.ReloadAsync(executionId);
        Assert.Equal(ProvisioningExecutionState.Pending, recovered.State);
        Assert.Equal(1, recovered.RecoveredAttemptCount);
        Assert.Equal(Actor, recovered.LastRecoveredBy);
        Assert.Contains("terminated mid-step", recovered.LastRecoveryReason);
        Assert.Equal(submitted.Execution.IdempotencyKey, recovered.IdempotencyKey);

        // And the resume itself: cause removed, runner takes it, everything already committed is
        // kept and nothing is built twice.
        await RemoveRivalUserAsync(harness, rivalId);
        var outcome = await harness.Runner().RunAsync(executionId);
        Assert.NotNull(outcome);

        var finished = await harness.ReloadAsync(executionId);
        Assert.Equal(ProvisioningExecutionState.Succeeded, finished.State);
        Assert.Equal(businessUnitId, finished.ProvisionedBusinessUnitId);
        Assert.Equal(failed.TenantId, finished.TenantId);

        // The steps that had committed were never attempted again.
        foreach (var code in new[]
                 {
                     ProvisioningStepCodes.Tenant, ProvisioningStepCodes.BusinessUnit,
                     ProvisioningStepCodes.LifecycleStatuses, ProvisioningStepCodes.AiPolicy,
                     ProvisioningStepCodes.FoundingRole
                 })
            Assert.Equal(1, finished.Steps.Single(s => s.StepCode == code).AttemptCount);

        await using var db = harness.Context();
        Assert.Equal(1, await db.Set<Tenant>().IgnoreQueryFilters().CountAsync(t => t.Slug == "stalled-co"));
        Assert.Equal(1, await db.Set<BusinessUnit>().CountAsync(b => b.BusinessUnitCode == "STALLED-CO"));
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "ada@stalled.test"));
    }

    // ---- 2. the negative: a live lease is not stealable ---------------------------------------

    [Fact]
    public async Task A_live_lease_is_refused_by_the_recovery_path_and_by_retry()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("busy-corp", "ada@busy.test", planId));
        var executionId = submitted.Execution!.Id;

        // A runner that took the lease five seconds ago and is inside a step.
        await StrandAsync(harness, executionId, silentFor: TimeSpan.FromSeconds(5),
            leaseRemaining: TimeSpan.FromMinutes(5));

        var before = await harness.ReloadAsync(executionId);

        using (var scope = harness.Scope())
        {
            var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();

            Assert.Equal(ProvisioningLeaseStaleness.Live,
                (await recovery.AssessAsync(executionId))!.Staleness);

            var result = await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
                executionId, "I think it is stuck.", Actor, 7));

            // Not a rate limit to retry through: the holder is presumed to be mid-step.
            Assert.Equal(ProvisioningRecoveryOutcome.LeaseStillLive, result.Outcome);
            Assert.Contains("never transferable", result.Error);

            // A refused transfer must also be an INERT one. Nothing moved, including the counter
            // that would otherwise let repeated refusals look like repeated recoveries.
            var service = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var retry = await service.RetryAsync(executionId, stepCode: null, Actor);
            Assert.Equal(ProvisioningCommandOutcome.Busy, retry.Outcome);
        }

        var after = await harness.ReloadAsync(executionId);
        Assert.Equal(before.LeaseOwner, after.LeaseOwner);
        Assert.Equal(before.LeaseToken, after.LeaseToken);
        Assert.Equal(before.LeaseUntil, after.LeaseUntil);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(0, after.RecoveredAttemptCount);
        Assert.Null(after.LastRecoveredBy);

        // And no audit row: an operation that did nothing must not leave a record saying it did.
        await using var db = harness.Context();
        Assert.Empty(await db.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.Action == ProvisioningAuditActions.LeaseRecovered).ToListAsync());
    }

    [Fact]
    public async Task An_execution_that_has_only_just_gone_quiet_is_not_yet_recoverable()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("cooling-ltd", "ada@cooling.test", planId));
        var executionId = submitted.Execution!.Id;

        // The lease has lapsed — a RUNNER may reclaim this, and will — but the silence is a
        // minute old against a ten-minute grace. A step that outran its lease looks exactly like
        // this, and the correct answer to that is a longer lease, not a second runner.
        await StrandAsync(harness, executionId, silentFor: TimeSpan.FromMinutes(1));

        using var scope = harness.Scope();
        var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();

        Assert.Equal(ProvisioningLeaseStaleness.Cooling,
            (await recovery.AssessAsync(executionId))!.Staleness);

        var result = await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
            executionId, "Deploy rolled the node.", Actor, 7));
        Assert.Equal(ProvisioningRecoveryOutcome.NotYetStale, result.Outcome);
        Assert.Equal(0, (await harness.ReloadAsync(executionId)).RecoveredAttemptCount);
    }

    // ---- 3. audit evidence --------------------------------------------------------------------

    [Fact]
    public async Task A_recovery_writes_audit_evidence_carrying_both_sides_of_the_ownership_change()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("evidenced-co", "ada@evidenced.test", planId));
        var executionId = submitted.Execution!.Id;
        await StrandAsync(harness, executionId, silentFor: TimeSpan.FromMinutes(30));

        using (var scope = harness.Scope())
        {
            var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();
            var result = await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
                executionId, "Node evicted; confirmed gone from the fleet.", Actor, 7));
            Assert.Equal(ProvisioningRecoveryOutcome.Recovered, result.Outcome);
        }

        await using var db = harness.Context();
        var audit = await db.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(a => a.Action == ProvisioningAuditActions.LeaseRecovered);

        // Attributed to the human who declared the attempt dead, never to the process.
        Assert.Equal(7, audit.ActorPlatformUserId);
        Assert.Equal(PlatformAuditResults.Success, audit.Result);
        Assert.Equal(nameof(ProvisioningExecution), audit.TargetType);
        Assert.Equal(executionId.ToString(), audit.TargetId);

        // The evidence, not a claim that evidence was taken: both halves of the ownership, the
        // staleness that justified the transfer, and the reason a human gave for it.
        var metadata = audit.Metadata!;
        Assert.Contains("\"before\"", metadata);
        Assert.Contains("\"after\"", metadata);
        Assert.Contains("dead-node:1", metadata);
        Assert.Contains(nameof(ProvisioningLeaseStaleness.Abandoned), metadata);
        Assert.Contains("Node evicted", metadata);
        Assert.Contains("\"idempotencyPreserved\":true", metadata);
        Assert.Contains("\"fingerprintPreserved\":true", metadata);
        Assert.Contains("released-to-queue", metadata);

        // Never the credential and never a token that could activate anything.
        Assert.DoesNotContain("PasswordHash", metadata);
        Assert.DoesNotContain("AdminPassword", metadata);
    }

    // ---- 4. completed-step idempotency: probe, then skip ---------------------------------------

    [Fact]
    public async Task A_resume_probes_a_step_marked_failed_after_it_committed_and_does_not_repeat_it()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(
            ProvisioningHarness.Request("ambiguous-co", "ada@ambiguous.test", planId));
        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);
        var executionId = execution.Id;
        var userIdBefore = execution.FoundingUserId!.Value;

        // The ambiguous commit, reconstructed: the founding-admin step's transaction COMMITTED —
        // the Users row is on the ground and its id is on the execution — and the runner then saw
        // an exception it could not distinguish from a real failure, so the journal (written on a
        // separate connection, which is exactly why it survives) recorded a failure for work that
        // exists. A blind retry here is the one action capable of doing damage.
        await using (var db = harness.Context())
        {
            var stuck = await db.Set<ProvisioningExecution>()
                .Include(e => e.Steps).SingleAsync(e => e.Id == executionId);
            var step = stuck.Steps.Single(s => s.StepCode == ProvisioningStepCodes.FoundingAdmin);
            step.Status = ProvisioningStepStatus.Failed;
            step.FailureCode = "connection-lost";
            step.FailureReason = "The commit acknowledgement never arrived.";
            stuck.State = ProvisioningExecutionState.Failed;
            stuck.FailedStep = ProvisioningStepCodes.FoundingAdmin;
            stuck.CompletedOn = DateTime.UtcNow.AddMinutes(-30);
            stuck.AttemptCount = 0;
            await db.SaveChangesAsync();
        }

        var attemptsBefore = (await harness.ReloadAsync(executionId)).Steps
            .Single(s => s.StepCode == ProvisioningStepCodes.FoundingAdmin).AttemptCount;

        var outcome = await harness.Runner().RunAsync(executionId);
        Assert.NotNull(outcome);

        var resumed = await harness.ReloadAsync(executionId);
        var reconciled = resumed.Steps.Single(s => s.StepCode == ProvisioningStepCodes.FoundingAdmin);

        Assert.Equal(ProvisioningStepStatus.Succeeded, reconciled.Status);
        Assert.Null(reconciled.FailureCode);
        // Not re-run: the attempt counter did not move, because the step was never started.
        Assert.Equal(attemptsBefore, reconciled.AttemptCount);
        // And the green tick says WHY it is green, so nobody mistakes a probe for work done.
        Assert.Contains("\"reconciled\":true", reconciled.Detail);

        await using var verify = harness.Context();
        // DUPLICATE FIRST-ADMIN PREVENTION, which is the whole point: exactly one account holds
        // this address, and it is the same account the first attempt created.
        Assert.Equal(1, await verify.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Email == "ada@ambiguous.test"));
        Assert.Equal(userIdBefore, resumed.FoundingUserId);

        // DUPLICATE TENANT PREVENTION: one tenant, one business unit, one founding role.
        Assert.Equal(1, await verify.Set<Tenant>().IgnoreQueryFilters()
            .CountAsync(t => t.Slug == "ambiguous-co"));
        Assert.Equal(1, await verify.Set<BusinessUnit>()
            .CountAsync(b => b.BusinessUnitCode == "AMBIGUOUS-CO"));
        Assert.Equal(1, await verify.SetupMasters.IgnoreQueryFilters()
            .CountAsync(s => s.BusinessUnitId == resumed.ProvisionedBusinessUnitId
                             && s.SetupType == "Role" && s.SetupCode == "SUPER_ADMIN"));

        // The skip is itself a privileged decision and is recorded as one.
        var audit = await verify.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(a => a.Action == ProvisioningAuditActions.StepReconciled);
        Assert.Contains(ProvisioningStepCodes.FoundingAdmin, audit.Metadata);
        Assert.Contains(execution.IdempotencyKey, audit.Metadata);
    }

    [Fact]
    public async Task A_resume_never_reissues_an_invitation_the_administrator_has_already_redeemed()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            "activated-co", "ada@activated.test", planId, activation: AdminActivationMethods.Invite));
        var executionId = execution.Id;
        var invitationId = execution.InvitationId!.Value;

        await using (var db = harness.Context())
        {
            // The customer has activated: they chose a password and are using the account.
            var invitation = await db.Set<TenantAdminInvitation>().SingleAsync(i => i.Id == invitationId);
            invitation.RedeemedAtUtc = DateTime.UtcNow.AddMinutes(-5);

            // And the invitation step is recorded as failed — the mail send blew up after the
            // transaction committed, an operator forced it, a node died between the two writes.
            var stuck = await db.Set<ProvisioningExecution>()
                .Include(e => e.Steps).SingleAsync(e => e.Id == executionId);
            stuck.Steps.Single(s => s.StepCode == ProvisioningStepCodes.Invitation).Status =
                ProvisioningStepStatus.Failed;
            stuck.State = ProvisioningExecutionState.Failed;
            stuck.FailedStep = ProvisioningStepCodes.Invitation;
            stuck.AttemptCount = 0;
            await db.SaveChangesAsync();
        }

        Assert.NotNull(await harness.Runner().RunAsync(executionId));

        await using var db2 = harness.Context();
        var invitations = await db2.Set<TenantAdminInvitation>().AsNoTracking()
            .Where(i => i.UserId == execution.FoundingUserId).ToListAsync();

        // THE outcome this probe exists for. A plain retry supersedes-then-issues, and its
        // supersede clause deliberately excludes redeemed invitations — so it would have minted a
        // second, live, single-use link that sets the password of an account already in use, and
        // mailed it. One invitation, still the redeemed one.
        Assert.Single(invitations);
        Assert.Equal(invitationId, invitations[0].Id);
        Assert.NotNull(invitations[0].RedeemedAtUtc);
        Assert.Null(invitations[0].RevokedAtUtc);

        var step = (await harness.ReloadAsync(executionId)).Steps
            .Single(s => s.StepCode == ProvisioningStepCodes.Invitation);
        Assert.Equal(ProvisioningStepStatus.Succeeded, step.Status);
        Assert.Contains("\"reconciled\":true", step.Detail);
    }

    // ---- 5. duplicate tenant prevention across a recovery -------------------------------------

    [Fact]
    public async Task A_recovered_execution_still_owns_its_address_and_a_rival_submit_is_refused()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("contested-co", "ada@contested.test", planId));
        var executionId = submitted.Execution!.Id;
        await StrandAsync(harness, executionId, silentFor: TimeSpan.FromMinutes(45));

        using (var scope = harness.Scope())
        {
            var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();
            Assert.Equal(ProvisioningRecoveryOutcome.Recovered,
                (await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
                    executionId, "Node lost; taking the execution back.", Actor, 7))).Outcome);
        }

        // A recovery releases OWNERSHIP, never the workspace address. The rows the early steps
        // committed still belong to this execution, and a fresh submit that claimed the same
        // address would strand them with nothing pointing at them.
        var rival = await harness.SubmitAsync(
            ProvisioningHarness.Request("contested-co", "other@contested.test", planId),
            idempotencyKey: "a-different-key");
        Assert.Equal(ProvisioningSubmitOutcome.Conflict, rival.Outcome);
        Assert.Contains("already in progress", rival.Error);

        // And the original key still replays to the original execution rather than starting a
        // second one — the recovery did not mint a new identity.
        var replay = await harness.SubmitAsync(
            ProvisioningHarness.Request("contested-co", "ada@contested.test", planId),
            idempotencyKey: submitted.Execution.IdempotencyKey);
        Assert.Equal(ProvisioningSubmitOutcome.Replayed, replay.Outcome);
        Assert.Equal(executionId, replay.Execution!.Id);

        await harness.Runner().RunAsync(executionId);
        await using var db = harness.Context();
        Assert.Equal(1, await db.Set<Tenant>().IgnoreQueryFilters()
            .CountAsync(t => t.Slug == "contested-co"));
    }

    // ---- 6. ownership residue -----------------------------------------------------------------

    [Fact]
    public async Task Lease_columns_left_on_a_failed_execution_are_cleared_rather_than_tolerated()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("residue-co", "ada@residue.test", planId));
        var executionId = submitted.Execution!.Id;

        await using (var db = harness.Context())
        {
            var execution = await db.Set<ProvisioningExecution>().SingleAsync(e => e.Id == executionId);
            execution.State = ProvisioningExecutionState.Failed;
            execution.FailedStep = ProvisioningStepCodes.Tenant;
            execution.FailureReason = "Something transient.";
            // Debris: nothing holds this execution, but an operator reading the row sees an owner.
            execution.LeaseOwner = "dead-node:1";
            execution.LeaseToken = Guid.NewGuid();
            execution.LeaseUntil = DateTime.UtcNow.AddMinutes(-90);
            await db.SaveChangesAsync();
        }

        using var scope = harness.Scope();
        var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();

        Assert.Equal(ProvisioningLeaseStaleness.OwnershipResidue,
            (await recovery.AssessAsync(executionId))!.Staleness);

        var result = await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
            executionId, "Clearing ownership debris from a crashed attempt.", Actor, 7));
        Assert.Equal(ProvisioningRecoveryOutcome.Recovered, result.Outcome);

        var cleared = await harness.ReloadAsync(executionId);
        Assert.Null(cleared.LeaseOwner);
        Assert.Null(cleared.LeaseToken);
        Assert.Null(cleared.LeaseUntil);
        // The FAILURE is not erased. Clearing debris is not the same as deciding the attempt was
        // fine, and an operator still has to retry it deliberately.
        Assert.Equal(ProvisioningExecutionState.Failed, cleared.State);
        Assert.Equal(ProvisioningStepCodes.Tenant, cleared.FailedStep);
    }

    [Fact]
    public async Task A_succeeded_execution_is_never_recovered()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(
            ProvisioningHarness.Request("finished-co", "ada@finished.test", planId));

        using var scope = harness.Scope();
        var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();

        Assert.Equal(ProvisioningLeaseStaleness.Terminal,
            (await recovery.AssessAsync(execution.Id))!.Staleness);
        Assert.Equal(ProvisioningRecoveryOutcome.Terminal,
            (await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
                execution.Id, "Trying to unstick a tenant.", Actor, 7))).Outcome);
    }

    // ---- 7. tenant status inconsistent with provisioning state --------------------------------

    [Fact]
    public async Task A_tenant_that_is_active_over_an_unfinished_provision_is_reported_not_repaired()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("mismatch-co", "ada@mismatch.test", planId));
        var executionId = submitted.Execution!.Id;
        await SeedRivalUserAsync(harness, "ada@mismatch.test");
        await harness.Runner().RunAsync(executionId);

        var failed = await harness.ReloadAsync(executionId);
        await using (var db = harness.Context())
        {
            // Somebody activated the tenant while its provision was still broken — the exact state
            // TenantStatus.Provisioning exists to prevent.
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
                .SingleAsync(t => t.Id == failed.TenantId);
            tenant.Status = TenantStatus.Active;
            await db.SaveChangesAsync();
        }

        await StrandAsync(harness, executionId, silentFor: TimeSpan.FromMinutes(25));

        using var scope = harness.Scope();
        var recovery = scope.ServiceProvider.GetRequiredService<IProvisioningLeaseRecovery>();
        var assessment = await recovery.AssessAsync(executionId);

        Assert.Contains(assessment!.Findings, finding => finding.Contains("is Active while"));

        // Reported, and only reported. Whether a tenant is live is an activation decision with its
        // own authority and its own audit; a lease recovery that quietly also decided a customer's
        // status would be the most dangerous thing in this module.
        var result = await recovery.RecoverAsync(new ProvisioningRecoveryCommand(
            executionId, "Node died; resuming the provision.", Actor, 7));
        Assert.Equal(ProvisioningRecoveryOutcome.Recovered, result.Outcome);

        await using var db2 = harness.Context();
        var untouched = await db2.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Id == failed.TenantId);
        Assert.Equal(TenantStatus.Active, untouched.Status);
    }

    // ---- 8. the staleness rule, as arithmetic --------------------------------------------------

    [Theory]
    // Running, lease live -> nobody takes it, however long the heartbeat has been quiet.
    [InlineData("Running", -60, 300, ProvisioningLeaseStaleness.Live)]
    // Running, lease lapsed, silence inside the grace -> a runner may reclaim it; a human may not.
    [InlineData("Running", -60, -1, ProvisioningLeaseStaleness.Cooling)]
    // Running, lease lapsed, silence beyond the grace -> abandoned.
    [InlineData("Running", -1200, -900, ProvisioningLeaseStaleness.Abandoned)]
    // Not running but still naming an owner -> debris.
    [InlineData("Failed", -1200, -900, ProvisioningLeaseStaleness.OwnershipResidue)]
    public void The_staleness_rule_is_lease_lapse_AND_silence_beyond_the_grace(
        string state, int heartbeatOffsetSeconds, int leaseOffsetSeconds,
        ProvisioningLeaseStaleness expected)
    {
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var execution = new ProvisioningExecution
        {
            Id = 1,
            State = Enum.Parse<ProvisioningExecutionState>(state),
            CreatedOn = now.AddHours(-2),
            StartedOn = now.AddHours(-2),
            LeaseOwner = "node:1",
            LeaseToken = Guid.NewGuid(),
            LeaseHeartbeatAt = now.AddSeconds(heartbeatOffsetSeconds),
            LeaseUntil = now.AddSeconds(leaseOffsetSeconds)
        };

        Assert.Equal(expected, ProvisioningLeaseRules.Evaluate(
            execution, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void The_grace_can_never_be_configured_below_the_lease_it_is_a_margin_over()
    {
        // Configuration that would let an operator declare a runner dead while its own lease is
        // still live is corrected at the point of decision, not merely rejected at startup —
        // IOptionsMonitor means the value can change between the two.
        var grace = ProvisioningLeaseRules.GraceFor(new ProvisioningOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(30),
            StaleLeaseGrace = TimeSpan.FromMinutes(2)
        });
        Assert.Equal(TimeSpan.FromMinutes(30), grace);

        var validation = new ProvisioningOptionsValidatorProbe().Validate(new ProvisioningOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(30),
            StaleLeaseGrace = TimeSpan.FromMinutes(2)
        });
        Assert.False(validation.Succeeded);
        Assert.Contains("StaleLeaseGrace must be at least LeaseDuration", validation.FailureMessage);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// The state a node that died mid-step leaves behind: still <c>Running</c>, still naming an
    /// owner, with a lease and a heartbeat that stopped moving.
    /// </summary>
    private static async Task StrandAsync(
        ProvisioningHarness harness, long executionId, TimeSpan silentFor,
        TimeSpan? leaseRemaining = null)
    {
        await using var db = harness.Context();
        var execution = await db.Set<ProvisioningExecution>().SingleAsync(e => e.Id == executionId);
        var now = DateTime.UtcNow;

        execution.State = ProvisioningExecutionState.Running;
        execution.LeaseOwner = "dead-node:1";
        execution.LeaseToken = Guid.NewGuid();
        execution.LeaseHeartbeatAt = now - silentFor;
        execution.LeaseUntil = leaseRemaining is { } remaining
            ? now + remaining
            : now - silentFor;
        execution.CompletedOn = null;
        execution.AttemptCount = Math.Max(execution.AttemptCount, 1);
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedRivalUserAsync(ProvisioningHarness harness, string email)
    {
        await using var db = harness.Context();
        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = $"RIVAL-{Guid.NewGuid():N}"[..20],
            BusinessUnitName = "Rival",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        db.Set<BusinessUnit>().Add(businessUnit);
        await db.SaveChangesAsync();

        var user = new User
        {
            FirstName = "Rival", LastName = "Holder", Email = email,
            PasswordHash = "x", ImageUrl = string.Empty, Buid = businessUnit.Id,
            IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task RemoveRivalUserAsync(ProvisioningHarness harness, long userId)
    {
        await using var db = harness.Context();
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync();
    }
}

/// <summary>
/// Reaches the internal options validator the host runs at startup, so the floor it enforces is
/// asserted rather than assumed.
/// </summary>
internal sealed class ProvisioningOptionsValidatorProbe
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(ProvisioningOptions options)
    {
        var validatorType = typeof(ProvisioningOptions).Assembly
            .GetType("ERP_RFQ_Automation.Platform.Provisioning.ProvisioningOptionsValidator")!;
        var validator = (Microsoft.Extensions.Options.IValidateOptions<ProvisioningOptions>)
            Activator.CreateInstance(validatorType)!;
        return validator.Validate(null, options);
    }
}
