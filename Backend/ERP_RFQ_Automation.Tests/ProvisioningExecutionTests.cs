using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Durable provisioning: the execution journal, step-level failure and repair, compensation, and
/// cancellation.
///
/// <para><b>The state this pins.</b> Provisioning was one HTTP request wrapping one transaction
/// that did seven things. A failure anywhere rolled all seven back and surfaced as a bare 500, so
/// nobody could say which step failed or why; a timeout left the operator unable to tell a slow
/// success from a silent failure; and there was no way to retry the one step that broke. Each
/// test below asserts one of the properties that replaced that.</para>
/// </summary>
public sealed class ProvisioningExecutionTests
{
    [Fact]
    public async Task A_successful_run_journals_every_step_and_builds_the_whole_workspace()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(
            ProvisioningHarness.Request("northwind-trading", "ada@northwind.test", planId));

        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);
        Assert.Null(execution.FailedStep);
        Assert.NotNull(execution.CompletedOn);

        // Every step has a terminal verdict, and every one of them is recorded — not inferred
        // from the absence of an error.
        Assert.Equal(ProvisioningStepCodes.Ordered.Count, execution.Steps.Count);
        foreach (var step in execution.Steps)
        {
            Assert.Equal(ProvisioningStepStatus.Succeeded, step.Status);
            Assert.Equal(1, step.AttemptCount);
            Assert.NotNull(step.StartedOn);
            Assert.NotNull(step.CompletedOn);
            Assert.NotNull(step.DurationMs);
            Assert.Null(step.FailureReason);
        }

        await using var db = harness.Context();

        // The tenant leaves Provisioning only when the LAST step is done, which is what makes a
        // half-provisioned tenant read as half-provisioned instead of showing a green Active
        // badge over an empty workspace.
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Id == execution.TenantId);
        Assert.Equal(TenantStatus.Provisioning, tenant.Status); // activation authority is a separate gate
        Assert.Equal(execution.ProvisionedBusinessUnitId, tenant.PrimaryBusinessUnitId);

        var businessUnit = await db.Set<BusinessUnit>()
            .SingleAsync(b => b.Id == execution.ProvisionedBusinessUnitId);
        Assert.Equal("NORTHWIND-TRADING", businessUnit.BusinessUnitCode);

        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == execution.FoundingUserId);
        Assert.Equal(execution.ProvisionedBusinessUnitId, admin.Buid);
        Assert.Equal(execution.FoundingRoleId, admin.RoleId);
        // Invited administrators are dormant until they redeem, so they never occupy a billable
        // seat while the invitation is in flight.
        Assert.False(admin.IsActive);

        var role = await db.SetupMasters.IgnoreQueryFilters()
            .SingleAsync(s => s.SetupId == execution.FoundingRoleId);
        Assert.Equal("SUPER_ADMIN", role.SetupCode);
        Assert.Equal(ERP_RFQ_Automation.Authorization.RoleRanks.Owner, role.RoleRank);

        // The evidence a green tick is backed by real rows: the workspace baseline actually ran.
        Assert.True(await db.Set<Currency>().IgnoreQueryFilters()
            .AnyAsync(c => c.BusinessUnitId == execution.ProvisionedBusinessUnitId));
        Assert.True(await db.Set<SetUom>().IgnoreQueryFilters()
            .AnyAsync(u => u.BusinessUnitId == execution.ProvisionedBusinessUnitId));
        Assert.True(await db.Set<QuoteConfiguration>().IgnoreQueryFilters()
            .AnyAsync(q => q.BusinessUnitId == execution.ProvisionedBusinessUnitId));
        Assert.True(await db.Set<TenantAdminInvitation>()
            .AnyAsync(i => i.Id == execution.InvitationId && i.RevokedAtUtc == null));

        // The step journal carries what the seeder produced, which is what lets the compatibility
        // response quote the counts back to the operator after the fact.
        var baseline = ProvisioningProjection.ReadBaseline(execution);
        Assert.Equal("USD", baseline.BaseCurrencyCode);
        Assert.True(baseline.UnitsOfMeasureCreated > 0);
        Assert.True(baseline.RolesCreated > 0);
        Assert.True(baseline.QuoteConfigurationCreated);
    }

    [Fact]
    public async Task The_password_path_records_the_invitation_step_as_skipped_rather_than_pending()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            "callcentre-ltd", "ops@callcentre.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!"));

        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);

        // A blank row and a deliberate skip look identical to an operator unless the server says
        // which one it is.
        var invitation = execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.Invitation);
        Assert.Equal(ProvisioningStepStatus.Skipped, invitation.Status);
        Assert.Null(execution.InvitationId);

        await using var db = harness.Context();
        var admin = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == execution.FoundingUserId);
        Assert.True(admin.IsActive);
        Assert.True(BCrypt.Net.BCrypt.Verify("Correct-Horse-9!", admin.PasswordHash));

        // The progress bar must reach the end on this path too, or every password-activation
        // tenant sits at seven-eighths forever.
        var dto = ProvisioningProjection.ToDto(execution);
        Assert.Equal(dto.TotalStepCount, dto.CompletedStepCount);
    }

    [Fact]
    public async Task A_failed_step_is_named_persisted_and_survives_its_own_rollback()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("halfway-house", "clash@halfway.test", planId));
        var executionId = submitted.Execution!.Id;

        // The address is free at submit and taken by the time the step runs — the exact race the
        // old single-transaction design could only report as "Provisioning failed."
        await SeedRivalUserAsync(harness, "clash@halfway.test");

        var outcome = await harness.Runner().RunAsync(executionId);
        var execution = outcome!.Execution;

        Assert.Equal(ProvisioningExecutionState.Failed, execution.State);
        Assert.Equal(ProvisioningStepCodes.FoundingAdmin, execution.FailedStep);
        Assert.Contains("already exists on another tenant", execution.FailureReason);

        // Terminal: retrying cannot make the address free again, so the console must offer
        // "edit and resubmit" rather than a retry button guaranteed to fail.
        Assert.True(execution.FailureIsTerminal);

        // THE property the separate journal connection buys: the failure record was written by a
        // different transaction from the one that rolled back, so it is still here.
        var failedStep = execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.FoundingAdmin);
        Assert.Equal(ProvisioningStepStatus.Failed, failedStep.Status);
        Assert.Equal("email-taken", failedStep.FailureCode);
        Assert.Equal(1, failedStep.AttemptCount);
        Assert.NotNull(failedStep.FailureReason);

        // Partial progress is REAL and kept. Everything before the failure committed, and the
        // steps after it never started.
        foreach (var code in new[]
                 {
                     ProvisioningStepCodes.Tenant, ProvisioningStepCodes.BusinessUnit,
                     ProvisioningStepCodes.LifecycleStatuses, ProvisioningStepCodes.AiPolicy,
                     ProvisioningStepCodes.FoundingRole
                 })
            Assert.Equal(ProvisioningStepStatus.Succeeded,
                execution.Steps.Single(s => s.StepCode == code).Status);

        Assert.Equal(ProvisioningStepStatus.Pending,
            execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.BaselineSeed).Status);

        await using var db = harness.Context();
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Id == execution.TenantId);
        // Still Provisioning, so the tenants screen tells the truth about it.
        Assert.Equal(TenantStatus.Provisioning, tenant.Status);
        // And still navigable: the AI-policy screen and impersonation both resolve through this.
        Assert.Equal(execution.ProvisionedBusinessUnitId, tenant.PrimaryBusinessUnitId);
    }

    [Fact]
    public async Task A_retry_resumes_where_it_stopped_and_duplicates_nothing()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("resumable-co", "ada@resumable.test", planId));
        var executionId = submitted.Execution!.Id;

        // Fail at the baseline seed by removing the business unit the seeder targets... which is
        // not reachable. Instead: fail at the founding admin, then clear the cause and retry.
        // Same shape as the operational case an engineer actually hits — an address freed up, a
        // grant added, a plan reactivated.
        var rivalId = await SeedRivalUserAsync(harness, "ada@resumable.test");
        await harness.Runner().RunAsync(executionId);
        Assert.Equal(ProvisioningExecutionState.Failed, (await harness.ReloadAsync(executionId)).State);

        var before = await harness.ReloadAsync(executionId);
        var businessUnitIdAfterFailure = before.ProvisionedBusinessUnitId;
        var lifecycleStatusesAfterFailure =
            await CountLifecycleStatusesAsync(harness, before.ProvisionedBusinessUnitId!.Value);
        Assert.True(lifecycleStatusesAfterFailure > 0);

        await RemoveRivalUserAsync(harness, rivalId);

        using (var scope = harness.Scope())
        {
            var service = scope.ServiceProvider
                .GetRequiredService<ITenantProvisioningService>();
            var retry = await service.RetryAsync(executionId, stepCode: null, "owner@nexora.app");
            Assert.Equal(ProvisioningCommandOutcome.Accepted, retry.Outcome);
        }

        var outcome = await harness.Runner().RunAsync(executionId);
        var execution = outcome!.Execution;

        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);

        // The steps that had already committed were not re-run: their attempt count is still 1.
        Assert.Equal(1, execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.Tenant).AttemptCount);
        Assert.Equal(1, execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.BusinessUnit).AttemptCount);
        // The one that failed was, and its second attempt is recorded.
        Assert.Equal(2, execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.FoundingAdmin).AttemptCount);

        // Nothing an earlier step wrote was duplicated by the resume: the same business unit, and
        // exactly the same lifecycle statuses it had before the retry.
        Assert.Equal(businessUnitIdAfterFailure, execution.ProvisionedBusinessUnitId);
        Assert.Equal(lifecycleStatusesAfterFailure,
            await CountLifecycleStatusesAsync(harness, execution.ProvisionedBusinessUnitId!.Value));

        // The general form of the same claim: not one (type, code) pair in this tenant was written
        // twice. A re-run that duplicated a status would give every dropdown a double entry, which
        // is the visible symptom nobody would connect back to a retry weeks later.
        await AssertNoDuplicateSetupMastersAsync(harness, execution.ProvisionedBusinessUnitId!.Value);

        await using var db = harness.Context();
        Assert.Equal(1, await db.Set<BusinessUnit>()
            .CountAsync(b => b.BusinessUnitCode == "RESUMABLE-CO"));
        Assert.Equal(1, await db.SetupMasters.IgnoreQueryFilters()
            .CountAsync(s => s.BusinessUnitId == execution.ProvisionedBusinessUnitId
                             && s.SetupType == "Role" && s.SetupCode == "SUPER_ADMIN"));
        Assert.Equal(1, await db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Email == "ada@resumable.test"));
    }

    [Fact]
    public async Task Retrying_the_invitation_step_supersedes_the_old_link_instead_of_adding_a_second()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(
            ProvisioningHarness.Request("reissue-corp", "ada@reissue.test", planId));
        var firstInvitationId = execution.InvitationId;
        Assert.NotNull(firstInvitationId);

        // Rewinding ONE step of a completed provision is a repair, and it is what an operator does
        // when the customer says the email never arrived. Rewinding the whole execution of a live
        // tenant is meaningless and is refused — that asymmetry is the reason single-step retry
        // exists at all.
        using (var wholeExecutionScope = harness.Scope())
        {
            var service = wholeExecutionScope.ServiceProvider
                .GetRequiredService<ITenantProvisioningService>();
            var refused = await service.RetryAsync(execution.Id, stepCode: null, "owner@nexora.app");
            Assert.Equal(ProvisioningCommandOutcome.InvalidState, refused.Outcome);
            Assert.Contains("Name a single step", refused.Error);
        }

        // The invitation is the step that cannot simply be run again: a second IssueAsync would
        // leave TWO live activation links, either of which sets the administrator's password.
        using (var scope = harness.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            var retry = await service.RetryAsync(
                execution.Id, ProvisioningStepCodes.Invitation, "owner@nexora.app");
            Assert.Equal(ProvisioningCommandOutcome.Accepted, retry.Outcome);
        }

        var reran = (await harness.Runner().RunAsync(execution.Id))!.Execution;
        Assert.Equal(ProvisioningExecutionState.Succeeded, reran.State);
        Assert.NotEqual(firstInvitationId, reran.InvitationId);

        await using var db = harness.Context();
        var invitations = await db.Set<TenantAdminInvitation>()
            .Where(i => i.UserId == execution.FoundingUserId)
            .OrderBy(i => i.Id)
            .ToListAsync();

        Assert.Equal(2, invitations.Count);

        // Exactly one live link. The compensation is the revocation, and it is the only reason
        // pressing retry here is safe.
        Assert.Single(invitations, i => i.IsLiveAt(DateTime.UtcNow));
        var superseded = invitations.Single(i => i.Id == firstInvitationId);
        Assert.NotNull(superseded.RevokedAtUtc);
        Assert.Contains("Superseded", superseded.RevocationReason!);
    }

    [Fact]
    public async Task Cancelling_stops_the_attempt_marks_the_unrun_steps_and_keeps_what_committed()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("abandoned-ltd", "ada@abandoned.test", planId));
        var executionId = submitted.Execution!.Id;

        await SeedRivalUserAsync(harness, "ada@abandoned.test");
        await harness.Runner().RunAsync(executionId);

        ProvisioningCommandResult cancelled;
        using (var scope = harness.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
            cancelled = await service.CancelAsync(
                executionId, "Customer withdrew before go-live", "owner@nexora.app");
        }

        Assert.Equal(ProvisioningCommandOutcome.Accepted, cancelled.Outcome);

        var execution = await harness.ReloadAsync(executionId);
        Assert.Equal(ProvisioningExecutionState.Cancelled, execution.State);
        Assert.Equal("owner@nexora.app", execution.CancelledBy);
        Assert.Equal("Customer withdrew before go-live", execution.CancellationReason);

        // Committed steps keep their verdict — cancellation does not undo work and must not
        // claim to. Unrun steps become Cancelled so the console stops looking like a live queue.
        Assert.Equal(ProvisioningStepStatus.Succeeded,
            execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.Tenant).Status);
        Assert.Equal(ProvisioningStepStatus.Cancelled,
            execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.FoundingAdmin).Status);
        Assert.Equal(ProvisioningStepStatus.Cancelled,
            execution.Steps.Single(s => s.StepCode == ProvisioningStepCodes.BaselineSeed).Status);

        // The rows are still there, and the tenant is still honestly Provisioning.
        await using var db = harness.Context();
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Id == execution.TenantId);
        Assert.Equal(TenantStatus.Provisioning, tenant.Status);

        // A cancelled execution never runs again, no matter who asks.
        Assert.Null(await harness.Runner().RunAsync(executionId));
        Assert.Equal(ProvisioningExecutionState.Cancelled,
            (await harness.ReloadAsync(executionId)).State);
    }

    [Fact]
    public async Task A_second_runner_cannot_claim_an_execution_another_runner_holds()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("leased-inc", "ada@leased.test", planId));
        var executionId = submitted.Execution!.Id;

        // Simulate a runner that took the lease and has not finished: two runners on one
        // execution would both write the same tenant rows, and the steps are idempotent against
        // themselves but not against a rival running at the same instant.
        await using (var db = harness.Context())
        {
            var execution = await db.Set<ProvisioningExecution>().SingleAsync(e => e.Id == executionId);
            execution.State = ProvisioningExecutionState.Running;
            execution.LeaseOwner = "other-node:1";
            execution.LeaseToken = Guid.NewGuid();
            execution.LeaseUntil = DateTime.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync();
        }

        Assert.Null(await harness.Runner().RunAsync(executionId));

        // An EXPIRED lease is abandoned work and must be reclaimable, or a process that died
        // mid-provision would park the tenant forever.
        await using (var db = harness.Context())
        {
            var execution = await db.Set<ProvisioningExecution>().SingleAsync(e => e.Id == executionId);
            execution.LeaseUntil = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var outcome = await harness.Runner().RunAsync(executionId);
        Assert.NotNull(outcome);
        Assert.Equal(ProvisioningExecutionState.Succeeded, outcome!.Execution.State);
    }

    [Fact]
    public async Task The_worker_sweep_picks_up_queued_work_without_being_told_which()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        await harness.SubmitAsync(ProvisioningHarness.Request("sweep-one", "one@sweep.test", planId));
        await harness.SubmitAsync(ProvisioningHarness.Request("sweep-two", "two@sweep.test", planId));

        var ran = await harness.Runner().RunAvailableAsync(batchSize: 10);
        Assert.Equal(2, ran);

        await using var db = harness.Context();
        var states = await db.Set<ProvisioningExecution>().AsNoTracking()
            .Select(e => e.State).ToListAsync();
        Assert.All(states, state => Assert.Equal(ProvisioningExecutionState.Succeeded, state));

        // Nothing left to do: a second sweep must not re-run finished work.
        Assert.Equal(0, await harness.Runner().RunAvailableAsync(batchSize: 10));
    }

    [Fact]
    public async Task A_successful_run_audits_the_provision_against_the_operator_who_submitted_it()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var execution = await harness.ProvisionAsync(
            ProvisioningHarness.Request("audited-co", "ada@audited.test", planId));

        await using var db = harness.Context();
        var audit = await db.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(a => a.Action == "tenant.provision");

        // The runner has no HttpContext and no principal, so an unattributed row would be the
        // easy outcome. It is attributed to the human who asked, which is what the audit log is
        // for — and it commits with the tenant's transition to Active, not beside it.
        Assert.Equal(7, audit.ActorPlatformUserId);
        Assert.Equal(execution.TenantId, audit.ActAsTenantId);
        Assert.Equal(PlatformAuditResults.Success, audit.Result);
        Assert.Contains(execution.CorrelationId, audit.Metadata);
        // Never the credential, never the token.
        Assert.DoesNotContain("PasswordHash", audit.Metadata);
        Assert.DoesNotContain("Token", audit.Metadata);
    }

    [Fact]
    public async Task A_failed_run_audits_the_failure_rather_than_leaving_a_gap()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        var submitted = await harness.SubmitAsync(
            ProvisioningHarness.Request("doomed-ltd", "ada@doomed.test", planId));
        await SeedRivalUserAsync(harness, "ada@doomed.test");
        await harness.Runner().RunAsync(submitted.Execution!.Id);

        await using var db = harness.Context();
        var audit = await db.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(a => a.Action == "tenant.provision");

        Assert.Equal(PlatformAuditResults.Failure, audit.Result);
        Assert.Contains(ProvisioningStepCodes.FoundingAdmin, audit.Metadata);
    }

    // ---- drafts -----------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_round_trips_refuses_a_credential_and_refuses_a_stale_save()
    {
        using var harness = new ProvisioningHarness();
        var planId = await harness.PlanAsync();

        using var scope = harness.Scope();
        var drafts = scope.ServiceProvider.GetRequiredService<IProvisioningDraftService>();

        var partial = new ProvisionTenantRequest
        {
            Name = "Partly Filled Ltd",
            AdminEmail = "later@partly.test",
            AdminFirstName = "To",
            AdminLastName = "Do",
            PlanId = planId
        };

        var created = await drafts.CreateAsync(
            new SaveProvisioningDraftRequest { Payload = partial }, "owner@nexora.app", 7);
        Assert.Equal(ProvisioningDraftOutcome.Saved, created.Outcome);
        Assert.Equal("Partly Filled Ltd", created.Draft!.Name);

        var loaded = await drafts.GetAsync(created.Draft.Id, "owner@nexora.app");
        Assert.NotNull(loaded);
        var rehydrated = ProvisioningRequestCanonicalizer.Rehydrate(loaded!.Payload);
        Assert.Equal("Partly Filled Ltd", rehydrated.Name);
        Assert.Equal(planId, rehydrated.PlanId);

        // Another operator's draft is a 404, not a 403: confirming the id exists would turn the
        // id space into a directory of who is being onboarded.
        Assert.Null(await drafts.GetAsync(created.Draft.Id, "someone-else@nexora.app"));

        // Refused, not silently stripped. A caller who sent a credential believes it was saved.
        var withPassword = await drafts.UpdateAsync(created.Draft.Id,
            new SaveProvisioningDraftRequest
            {
                Payload = new ProvisionTenantRequest
                {
                    Name = "Partly Filled Ltd", AdminEmail = "later@partly.test",
                    AdminFirstName = "To", AdminLastName = "Do",
                    AdminPassword = "Should-Never-Persist-1!"
                },
                Version = created.Draft.Version
            }, "owner@nexora.app");
        Assert.Equal(ProvisioningDraftOutcome.Rejected, withPassword.Outcome);
        Assert.Contains("adminPassword cannot be saved", withPassword.Error);

        partial.City = "Riyadh";
        var updated = await drafts.UpdateAsync(created.Draft.Id,
            new SaveProvisioningDraftRequest { Payload = partial, Version = created.Draft.Version },
            "owner@nexora.app");
        Assert.Equal(ProvisioningDraftOutcome.Saved, updated.Outcome);

        // Two tabs open on one draft must not silently overwrite each other.
        var stale = await drafts.UpdateAsync(created.Draft.Id,
            new SaveProvisioningDraftRequest { Payload = partial, Version = 1 }, "owner@nexora.app");
        Assert.Equal(ProvisioningDraftOutcome.VersionConflict, stale.Outcome);

        Assert.Single(await drafts.ListAsync("owner@nexora.app", includeSubmitted: false));
        Assert.Empty(await drafts.ListAsync("someone-else@nexora.app", includeSubmitted: false));
        Assert.True(await drafts.DeleteAsync(created.Draft.Id, "owner@nexora.app"));
        Assert.False(await drafts.DeleteAsync(created.Draft.Id, "owner@nexora.app"));
    }

    [Fact]
    public async Task A_draft_can_be_saved_after_only_the_first_company_field()
    {
        using var harness = new ProvisioningHarness();
        using var scope = harness.Scope();
        var drafts = scope.ServiceProvider.GetRequiredService<IProvisioningDraftService>();

        var created = await drafts.CreateAsync(new SaveProvisioningDraftRequest
        {
            Payload = new ProvisionTenantRequest { Name = "First field only" }
        }, "owner@nexora.app", 7);

        Assert.Equal(ProvisioningDraftOutcome.Saved, created.Outcome);
        var loaded = await drafts.GetAsync(created.Draft!.Id, "owner@nexora.app");
        var payload = ProvisioningRequestCanonicalizer.Rehydrate(loaded!.Payload);
        Assert.Equal("First field only", payload.Name);
        Assert.Null(payload.AdminEmail);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// A user on somebody else's tenant holding the address this execution wants. Users.Email is
    /// globally unique, so this is the most common real cause of a provisioning failure.
    /// </summary>
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

    /// <summary>
    /// The rows the lifecycle-statuses step owns, and only those. Roles and discount types are
    /// excluded because DIFFERENT steps create them — the founding role, and the baseline seeder's
    /// starter roles and discount types — so a naive count of every <c>SetupMaster</c> would report
    /// those steps doing their job as the lifecycle step duplicating.
    /// </summary>
    private static async Task<int> CountLifecycleStatusesAsync(
        ProvisioningHarness harness, long businessUnitId)
    {
        var lifecycleTypes = LifecycleStatusCatalog
            .CreateFor(new BusinessUnit { Id = businessUnitId }, "probe")
            .Select(status => status.SetupType)
            .Distinct()
            .ToList();

        await using var db = harness.Context();
        return await db.SetupMasters.IgnoreQueryFilters()
            .CountAsync(s => s.BusinessUnitId == businessUnitId && lifecycleTypes.Contains(s.SetupType));
    }

    private static async Task AssertNoDuplicateSetupMastersAsync(
        ProvisioningHarness harness, long businessUnitId)
    {
        await using var db = harness.Context();
        var duplicates = await db.SetupMasters.IgnoreQueryFilters()
            .Where(s => s.BusinessUnitId == businessUnitId)
            .GroupBy(s => new { s.SetupType, s.SetupCode })
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.SetupType + " " + group.Key.SetupCode)
            .ToListAsync();

        Assert.Empty(duplicates);
    }
}
