using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// AA-02. The three tenant-administration gaps the product owner named, pinned by the property
/// that would break if the wiring were removed rather than by the value round-tripping.
///
/// <para><b>Why each of these needed a test at all.</b> Two of the three are wiring-contract
/// failure #5 — a setting with no way to set it — on columns that already had live readers, which
/// is precisely the class of defect no existing test could see: every one of them passed, because
/// each layer that existed worked. The third is a control that did not exist.</para>
/// </summary>
public sealed class PlatformTenantEditAndApprovalTests
{
    private const string GoodReason = "Customer moved their accounts-payable mailbox on 2026-08-01.";

    // ============================================================ 1. invoicing details are settable

    [Fact]
    public async Task The_invoice_recipient_can_be_corrected_after_provisioning_and_is_audited()
    {
        // The defect: BillingContactEmail was written once, at provisioning, and never again —
        // while SubscriptionInvoiceService REFUSES TO ISSUE without it, computes the due date from
        // PaymentTermsDays, and freezes the whole block into each invoice's buyer snapshot. A
        // customer who changed their AP mailbox could not be corrected except by direct SQL.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "ap-moved");
        await using var context = db.ContextFor(null);

        var result = await BillingController(context).SetTenantAccountContact(tenantId,
            new SetTenantAccountContactRequest(
                BillingContactName: "Accounts Payable",
                BillingContactEmail: "ap@buyer.example",
                BillingAddress: "Finance Department, Floor 3",
                PurchaseOrderReference: "PO-2026-0912",
                PaymentTermsDays: 45,
                AccountOwnerEmail: "csm@nexora.example",
                ContractStartOn: new DateTime(2026, 1, 1),
                ContractEndOn: new DateTime(2027, 1, 1),
                Reason: GoodReason),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        await using var verify = db.ContextFor(null);
        var tenant = await verify.Set<Tenant>().SingleAsync(t => t.Id == tenantId);
        Assert.Equal("ap@buyer.example", tenant.BillingContactEmail);
        Assert.Equal(45, tenant.PaymentTermsDays);
        Assert.Equal("PO-2026-0912", tenant.PurchaseOrderReference);
        Assert.Equal("Finance Department, Floor 3", tenant.BillingAddress);

        // Attributable, with the before AND the after: "who redirected this customer's invoice"
        // is the question this endpoint exists to be able to answer.
        var audit = await verify.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == "billing.tenant.account-contact");
        Assert.Equal(tenantId, audit.ActAsTenantId);
        Assert.Equal(7, audit.ActorPlatformUserId);
        Assert.Contains(GoodReason, audit.Metadata);
        Assert.Contains("ap@buyer.example", audit.Metadata);
        Assert.Contains("before", audit.Metadata);
    }

    [Fact]
    public async Task The_new_details_reach_the_read_surface_the_console_renders()
    {
        // Wiring, not storage. A value that is saved and never returned is the same defect in the
        // other direction — the operator corrects the address and the screen keeps showing the old
        // one, so they correct it again.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "read-back");
        await using var context = db.ContextFor(null);
        var controller = BillingController(context);

        await controller.SetTenantAccountContact(tenantId, Valid(BillingAddress: "Gate 4, Industrial City"),
            CancellationToken.None);

        var profile = Assert.IsType<TenantBillingProfileDto>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetTenantBillingProfile(tenantId, CancellationToken.None)).Result).Value);

        Assert.Equal("Gate 4, Industrial City", profile.BillingAddress);
        Assert.Equal("ap@buyer.example", profile.BillingContactEmail);
    }

    [Fact]
    public async Task Clearing_the_invoice_recipient_on_a_billable_tenant_is_refused()
    {
        // Wiring-contract failure #8 in reverse: null is a VALUE, and this one is the value that
        // stops the customer being invoiced. Worse, the offboarding readiness gate requires a
        // finalized invoice, so clearing it also strands the tenant — it could be neither billed
        // nor ended.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "cleared");
        await using var context = db.ContextFor(null);

        var refusal = Assert.IsType<BadRequestObjectResult>(
            (await BillingController(context).SetTenantAccountContact(tenantId,
                Valid(BillingContactEmail: null), CancellationToken.None)).Result);

        Assert.Contains("offboard", refusal.Value!.ToString(), StringComparison.OrdinalIgnoreCase);

        await using var verify = db.ContextFor(null);
        // Refused, and nothing else in the block was written either — a partially applied
        // rejection would leave the tenant in a shape the caller was told did not happen.
        var tenant = await verify.Set<Tenant>().SingleAsync(t => t.Id == tenantId);
        Assert.Null(tenant.PurchaseOrderReference);
        Assert.Empty(await verify.Set<PlatformAuditLog>()
            .Where(a => a.Action == "billing.tenant.account-contact").ToListAsync());
    }

    [Theory]
    // Rejects the wrong values, not merely the impossible ones. -1 back-dates the due date before
    // the issue date; 3650 produces an invoice due in ten years, which drops out of every
    // collections view without ever looking overdue.
    [InlineData(-1)]
    [InlineData(3650)]
    public async Task Payment_terms_outside_the_range_the_due_date_can_carry_are_refused(int days)
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, $"terms-{days}");
        await using var context = db.ContextFor(null);

        Assert.IsType<BadRequestObjectResult>(
            (await BillingController(context).SetTenantAccountContact(tenantId,
                Valid(PaymentTermsDays: days), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Zero_payment_terms_is_accepted_because_due_on_receipt_is_a_real_term()
    {
        // The other half of the rule above. A range check that also refuses a legitimate value is
        // not a stricter control, it is a different defect.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "due-on-receipt");
        await using var context = db.ContextFor(null);

        Assert.IsType<OkObjectResult>(
            (await BillingController(context).SetTenantAccountContact(tenantId,
                Valid(PaymentTermsDays: 0), CancellationToken.None)).Result);

        await using var verify = db.ContextFor(null);
        Assert.Equal(0, (await verify.Set<Tenant>().SingleAsync(t => t.Id == tenantId)).PaymentTermsDays);
    }

    [Fact]
    public async Task A_short_reason_is_refused_so_a_redirected_invoice_stays_explainable()
    {
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "thin-reason");
        await using var context = db.ContextFor(null);

        Assert.IsType<BadRequestObjectResult>(
            (await BillingController(context).SetTenantAccountContact(tenantId,
                Valid(Reason: "fix"), CancellationToken.None)).Result);
    }

    [Fact]
    public void Invoicing_details_sit_behind_the_billing_policy_and_not_the_support_one()
    {
        // Sec9 separation of duties. Where a customer's invoice is SENT decides who receives the
        // demand for money, so it belongs with what they are charged and not with the operational
        // verbs a support engineer holds.
        var controller = typeof(PlatformBillingController);
        Assert.NotNull(controller.GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy == PlatformPolicies.Billing));

        var action = controller.GetMethod(nameof(PlatformBillingController.SetTenantAccountContact));
        Assert.NotNull(action);
        // No action-level attribute may widen the class-level Billing gate.
        Assert.All(action!.GetCustomAttributes<AuthorizeAttribute>(),
            gate => Assert.Contains(gate.Policy, new[] { PlatformPolicies.Billing, PlatformPolicies.Owner }));
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    // ================================================================ 2. the contractual region

    [Fact]
    public async Task A_mistyped_data_region_can_be_corrected_when_no_asset_contradicts_it()
    {
        // Before this endpoint the column was write-once at provisioning, and two controls read it:
        // an asset cannot be registered in a different region, and the data.residency-isolation
        // activation control compares the verified database asset against it. A region typed wrong
        // at provisioning therefore produced a tenant that could never be activated and against
        // which no asset could ever be registered.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "region-typo", dataRegion: "me-cetnral-1");
        await using var context = db.ContextFor(null);

        var result = await TenantsControllerFor(context).UpdateDataRegion(tenantId,
            new UpdateTenantDataRegionRequest
            {
                DataRegion = "me-central-1",
                Reason = "Region was transposed at provisioning; contract says me-central-1.",
            }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        await using var verify = db.ContextFor(null);
        Assert.Equal("me-central-1", (await verify.Set<Tenant>().SingleAsync(t => t.Id == tenantId)).DataRegion);
        var audit = await verify.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == "tenant.data-region.update");
        Assert.Contains("me-cetnral-1", audit.Metadata);
        Assert.Contains("me-central-1", audit.Metadata);
    }

    [Fact]
    public async Task A_region_that_disagrees_with_a_registered_asset_is_refused_and_the_asset_is_named()
    {
        // The control this endpoint must not become a way around. The registered assets are the
        // evidence of where the data physically is; this column is only a claim about them. If the
        // claim could be edited freely, an operator could satisfy a residency control by rewriting
        // the assertion instead of moving the data — which is the same shape of defect as a delete
        // path that walks around the retention rules.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "region-evidence", dataRegion: "me-central-1");
        await SeedDataAsset(db, tenantId, region: "me-central-1");
        await using var context = db.ContextFor(null);

        var refusal = Assert.IsType<ConflictObjectResult>(
            (await TenantsControllerFor(context).UpdateDataRegion(tenantId,
                new UpdateTenantDataRegionRequest
                {
                    DataRegion = "eu-west-1",
                    Reason = "Attempting to restate residency without moving the data.",
                }, CancellationToken.None)).Result);

        var message = refusal.Value!.ToString()!;
        Assert.Contains("postgresql", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("me-central-1", message);

        await using var verify = db.ContextFor(null);
        Assert.Equal("me-central-1", (await verify.Set<Tenant>().SingleAsync(t => t.Id == tenantId)).DataRegion);
        Assert.Empty(await verify.Set<PlatformAuditLog>()
            .Where(a => a.Action == "tenant.data-region.update").ToListAsync());
    }

    [Fact]
    public async Task The_region_cannot_be_cleared_once_the_tenant_has_left_provisioning()
    {
        // Clearing it withdraws a control that has already passed. The activation policy reads
        // PRESENCE, so an empty region does not fail loudly — it quietly stops being checked.
        using var db = new TestDb();
        var tenantId = await SeedTenant(db, "region-cleared", dataRegion: "me-central-1",
            status: TenantStatus.Active);
        await using var context = db.ContextFor(null);

        Assert.IsType<BadRequestObjectResult>(
            (await TenantsControllerFor(context).UpdateDataRegion(tenantId,
                new UpdateTenantDataRegionRequest
                {
                    DataRegion = null,
                    Reason = "Clearing the recorded residency commitment entirely.",
                }, CancellationToken.None)).Result);
    }

    [Fact]
    public void The_region_endpoint_is_owner_only_while_the_profile_form_is_not()
    {
        // The reason these are two endpoints rather than one form. The profile is Owner or
        // SupportAdmin and describes the customer; the region asserts where their data lives and
        // gates their activation, so it must not be editable by whoever is correcting a postcode.
        var controller = typeof(TenantsController);

        var region = controller.GetMethod(nameof(TenantsController.UpdateDataRegion))!;
        Assert.Contains(region.GetCustomAttributes<AuthorizeAttribute>(),
            gate => gate.Policy == PlatformPolicies.Owner);

        var profile = controller.GetMethod(nameof(TenantsController.UpdateProfile))!;
        Assert.Contains(profile.GetCustomAttributes<AuthorizeAttribute>(),
            gate => gate.Policy == PlatformPolicies.TenantAdmin);
    }

    // ================================================== 3. a purge needs a second platform Owner

    [Fact]
    public async Task The_operator_who_scheduled_a_deletion_cannot_also_carry_it_out()
    {
        // The one act in this system no later reviewer can correct, and it was the only privileged
        // destructive operation where the maker could also be the checker. Billing statement
        // finalize, invoice finalize, tax-rule approval, revenue actions, FX rates and — in this
        // same module — legal-hold RELEASE all already require an independent second person.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "same-hand", TenantStatus.Archived, 9_101);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var maker = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, maker, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                maker, null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("second platform Owner", refusal.Message);

        // Refused BEFORE intent was recorded. A separation-of-duties check that ran after the
        // claim would leave a PurgeStarted event for a destruction that was never authorised.
        await using var verify = db.ContextFor(null);
        var record = await verify.Set<TenantOffboarding>().SingleAsync();
        Assert.Null(record.PurgeStartedOn);
        Assert.DoesNotContain(
            await verify.Set<TenantLifecycleEvent>().Select(e => e.Action).ToListAsync(),
            action => action == TenantLifecycleActions.PurgeStarted);
    }

    [Fact]
    public async Task A_different_owner_gets_past_the_separation_of_duties_gate()
    {
        // The control. Without this, "refuse every purge" would satisfy the test above and the
        // second-approver rule would be an outage rather than a boundary. The purge then fails at
        // the destructive step for want of an owner connection, which is what proves it got past.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "second-hand", TenantStatus.Archived, 9_102);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        var thrown = await Record.ExceptionAsync(() => service.PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
            TenantLifecycleHarness.SecondApprover(), null, CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.IsNotType<TenantOffboardingRefusedException>(thrown);
    }

    [Fact]
    public async Task Rescheduling_under_a_new_operator_moves_who_the_second_approver_must_be()
    {
        // The maker is read from the lifecycle event for the LIVE schedule, not from the first one
        // ever written. A tenant that is scheduled, cancelled and scheduled again by somebody else
        // has a new maker, and the original scheduler becomes an eligible checker.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "reschedule-approver", TenantStatus.Archived, 9_103);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var first = TenantLifecycleHarness.Operator();
        var second = TenantLifecycleHarness.SecondApprover();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, first, null, CancellationToken.None);
        await service.CancelDeletionAsync(tenant.Id,
            new CancelTenantDeletionRequest { Reason = "Held pending review." }, first, null,
            CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = "Second offboarding; renewal lapsed again." },
            second, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        // The SECOND operator scheduled it, so they are now the one who may not run it …
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                second, null, CancellationToken.None));
        Assert.Contains("second platform Owner", refusal.Message);

        // … and the first, who no longer owns the live decision, may.
        var thrown = await Record.ExceptionAsync(() => service.PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
            first, null, CancellationToken.None));
        Assert.NotNull(thrown);
        Assert.IsNotType<TenantOffboardingRefusedException>(thrown);
    }

    [Fact]
    public async Task The_console_is_told_who_scheduled_it_so_it_stops_offering_a_button_that_will_fail()
    {
        // A control the operator only discovers at the confirmation dialog is a control that wastes
        // their time. The status read resolves the verdict about the PERSON asking, the same way it
        // already resolves canPurge about the tenant.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "status-approver", TenantStatus.Archived, 9_104);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var maker = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, maker, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        var asMaker = await service.GetStatusAsync(tenant.Id, CancellationToken.None, maker);
        Assert.True(asMaker.PurgeRequiresDifferentApprover);
        Assert.Equal("operator@example.test", asMaker.DeletionApprovedBy);
        // canPurge stays TRUE: the tenant is purgeable, just not by this person. Folding the two
        // together would have the console blame a retention clock that has already run out.
        Assert.True(asMaker.CanPurge);

        var asChecker = await service.GetStatusAsync(
            tenant.Id, CancellationToken.None, TenantLifecycleHarness.SecondApprover());
        Assert.False(asChecker.PurgeRequiresDifferentApprover);
        Assert.Equal("operator@example.test", asChecker.DeletionApprovedBy);
    }

    [Fact]
    public async Task An_unattributable_scheduling_decision_refuses_the_purge_rather_than_waiving_the_rule()
    {
        // Fail closed, the same conclusion CurrencyController reaches about an exchange rate whose
        // maker is "System": segregation of duties that cannot be VERIFIED has not been observed.
        // The remedy is non-destructive and is named in the refusal.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "unattributable", TenantStatus.Archived, 9_105);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        // A pre-existing schedule from before actor ids were recorded.
        await using (var strip = db.ContextFor(null))
        {
            var scheduled = await strip.Set<TenantLifecycleEvent>()
                .SingleAsync(e => e.Action == TenantLifecycleActions.ScheduleDeletion);
            scheduled.ActorPlatformUserId = 0;
            await strip.SaveChangesAsync();
        }

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                TenantLifecycleHarness.SecondApprover(), null, CancellationToken.None));

        Assert.Contains("attributable", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schedule it again", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Finalizing_a_destruction_that_already_committed_is_not_blocked_by_the_approver_rule()
    {
        // The rule governs the DECISION to destroy, not the bookkeeping that follows one. A
        // previous attempt can commit the destructive owner transaction and die before writing the
        // completion — the module calls that state tolerable precisely because re-running heals it.
        // Applying separation of duties there would strand the tenant in it: the rows are already
        // gone, and the only act left is writing down that they are gone.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "recover-finalize", TenantStatus.Archived, 9_107);
        await using var context = db.ContextFor(null);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        var service = TenantLifecycleHarness.Service(context, timeProvider: clock);
        var maker = TenantLifecycleHarness.Operator();

        await service.ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = GoodReason }, maker, null, CancellationToken.None);
        TenantLifecycleHarness.ElapseRetentionWindow(clock);

        // A committed destructive transaction whose completion was never written.
        var attempt = Guid.NewGuid();
        await using (var crashed = db.ContextFor(null))
        {
            var record = await crashed.Set<TenantOffboarding>().SingleAsync();
            record.PurgeStartedOn = clock.GetUtcNow().UtcDateTime;
            record.PurgeAttemptId = attempt;
            record.PurgeExecutedOn = clock.GetUtcNow().UtcDateTime;
            record.PurgeExecutedRowCount = 128;
            record.PurgeExecutionDetail = "[]";
            record.PurgeReason = GoodReason;
            await crashed.SaveChangesAsync();
        }

        // The retry arrives on a FRESH context, as it does in production: the process that
        // committed the deletes is gone, and this is a new request picking the record back up.
        await using var recovery = db.ContextFor(null);

        // The same operator who scheduled it finishes the bookkeeping, and is not refused.
        var result = await TenantLifecycleHarness.Service(recovery, timeProvider: clock)
            .PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = GoodReason, Confirmation = tenant.Name },
                maker, null, CancellationToken.None);

        Assert.Equal(128, result.RowsDeleted);

        await using var verify = db.ContextFor(null);
        Assert.Equal(TenantOffboardingStage.Purged,
            (await verify.Set<TenantOffboarding>().SingleAsync()).Stage);
    }

    // ============================================ 4. the operator is told what was kept, and why

    [Fact]
    public void Every_preserved_table_carries_the_reason_it_survives()
    {
        // "Preserved: a, b, c" answers what and not why, and "why" is the half an operator has to
        // repeat to a customer asking what we still hold on them. The reasons are copied from
        // PlatformTenantDataMap rather than restated, so the sentence shown at the confirmation
        // dialog is the one the next engineer reads when they classify a new table.
        var detail = TenantPurgeExecutor.PreservedWithReasons;

        Assert.NotEmpty(detail);
        Assert.All(detail, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));

        // Exactly the same set as the list the purge actually skips — a description that can drift
        // from the behaviour it describes is worse than none.
        Assert.Equal(
            TenantPurgeExecutor.PreservedTables.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            detail.Select(x => x.Table).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task The_purge_disclosures_reconcile_this_path_with_the_statutory_retention_rule()
    {
        // EvidenceRetentionEligibility hard-codes invoices, purchase orders, customer orders,
        // supplier confirmations and delivery documents as records a tenant MAY NOT choose to
        // delete. A purge destroys them, and the two rules only look contradictory: that one
        // governs a live tenant's own housekeeping, this one is the end of the relationship, and
        // the obligation moves with the data — which is why the readiness gate refuses a purge
        // until a fingerprinted export proves the records were handed back.
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "statutory-disclosure", TenantStatus.Archived, 9_106);
        await using var context = db.ContextFor(null);

        var status = await TenantLifecycleHarness.Service(context)
            .GetStatusAsync(tenant.Id, CancellationToken.None);

        Assert.Contains(TenantOffboardingDisclosure.StatutoryRecordsMoveWithTheCustomer, status.Disclosures);
        Assert.Contains("statutory", TenantOffboardingDisclosure.StatutoryRecordsMoveWithTheCustomer,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("export", TenantOffboardingDisclosure.StatutoryRecordsMoveWithTheCustomer,
            StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================================ test scaffolding

    private static SetTenantAccountContactRequest Valid(
        string? BillingContactName = "Accounts Payable",
        string? BillingContactEmail = "ap@buyer.example",
        string? BillingAddress = null,
        string? PurchaseOrderReference = null,
        int? PaymentTermsDays = 30,
        string? AccountOwnerEmail = null,
        DateTime? ContractStartOn = null,
        DateTime? ContractEndOn = null,
        string? Reason = GoodReason) =>
        new(BillingContactName, BillingContactEmail, BillingAddress, PurchaseOrderReference,
            PaymentTermsDays, AccountOwnerEmail, ContractStartOn, ContractEndOn, Reason);

    private static PlatformBillingController BillingController(ErpRfqAutomationContext context) =>
        new(context,
            new BillingStatementService(context, NullLogger<BillingStatementService>.Instance),
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformBillingController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = PlatformActor() }
            }
        };

    private static TenantsController TenantsControllerFor(ErpRfqAutomationContext context)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TenantsController(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context),
            ProvisioningFixture.Invitations(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = PlatformActor() }
            }
        };
    }

    private static ClaimsPrincipal PlatformActor(long id = 7, string email = "operator@example.test") =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", id.ToString()),
            new Claim("email", email),
            new Claim("platformRole", "Owner")
        ], "Platform"));

    private static async Task<long> SeedTenant(
        TestDb db, string slug, string? dataRegion = null, TenantStatus status = TenantStatus.Active)
    {
        await using var seed = db.ContextFor(null);
        var tenant = new Tenant
        {
            Name = "Edit Tenant",
            Slug = slug,
            Status = status,
            BillingMode = TenantBillingMode.Billable,
            DataRegion = dataRegion,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        };
        seed.Set<Tenant>().Add(tenant);
        await seed.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task SeedDataAsset(TestDb db, long tenantId, string region)
    {
        await using var seed = db.ContextFor(null);
        seed.Set<TenantDataAsset>().Add(new TenantDataAsset
        {
            TenantId = tenantId,
            LogicalKey = "postgresql.primary",
            AssetType = TenantDataAssetTypes.PostgreSqlTenantScope,
            OpaqueProviderReference = "opaque-reference",
            Region = region,
            Classification = TenantDataAssetClassifications.CustomerData,
            Disposition = TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
            BackupPolicyReference = "backup-policy",
            BackupPolicyVersion = 1,
            Status = TenantDataAssetStatuses.Verified,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = "test"
        });
        await seed.SaveChangesAsync();
    }
}
