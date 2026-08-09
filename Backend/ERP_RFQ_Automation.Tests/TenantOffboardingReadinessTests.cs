using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class TenantOffboardingReadinessTests
{
    private const string Reason = "Contract terminated after governed financial close and customer export.";
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task Missing_final_reconciliation_blocks_schedule_without_creating_offboarding_intent()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, "missing-close", TenantStatus.Archived, 91_001);
        await using var context = db.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(context,
                    readiness: new TenantOffboardingReadinessService(context))
                .ScheduleDeletionAsync(tenant.Id,
                    new ScheduleTenantDeletionRequest { Reason = Reason },
                    TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Contains(TenantOffboardingReadinessCodes.FinalBillingMissing, refusal.Message);
        await using var verify = db.ContextFor(null);
        Assert.Empty(await verify.Set<TenantOffboarding>().ToListAsync());
        Assert.Empty(await verify.Set<TenantLifecycleEvent>().ToListAsync());
    }

    [Fact]
    public async Task Complete_existing_financial_accounting_and_export_evidence_allows_schedule()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await SeedReadyTenantAsync(db, "ready-close", 91_002);
        await using var context = db.ContextFor(null);
        var readiness = new TenantOffboardingReadinessService(context);

        var verdict = await readiness.AssessAsync(tenant, TenantOffboardingReadinessPhase.Schedule);
        Assert.True(verdict.Ready);

        var status = await TenantLifecycleHarness.Service(context, readiness: readiness)
            .ScheduleDeletionAsync(tenant.Id,
                new ScheduleTenantDeletionRequest { Reason = Reason },
                TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        Assert.Equal(nameof(TenantOffboardingStage.PendingDeletion), status.Stage);
    }

    [Fact]
    public async Task Reopened_accounts_receivable_blocks_purge_before_intent_is_recorded()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await SeedReadyTenantAsync(db, "reopened-ar", 91_003);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        await using (var schedule = db.ContextFor(null))
            await TenantLifecycleHarness.Service(schedule, timeProvider: clock,
                    readiness: new TenantOffboardingReadinessService(schedule))
                .ScheduleDeletionAsync(tenant.Id,
                    new ScheduleTenantDeletionRequest { Reason = Reason },
                    TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        TenantLifecycleHarness.ElapseRetentionWindow(clock);
        await using (var reopen = db.ContextFor(null))
        {
            var invoice = await reopen.Set<SubscriptionInvoice>().SingleAsync(x => x.TenantId == tenant.Id);
            invoice.Status = SubscriptionInvoiceStatus.PartiallyPaid;
            invoice.PaidAmount = 20m;
            await reopen.SaveChangesAsync();
        }

        await using var purge = db.ContextFor(null);
        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            TenantLifecycleHarness.Service(purge, timeProvider: clock,
                    readiness: new TenantOffboardingReadinessService(purge))
                .PurgeAsync(tenant.Id,
                    new ConfirmTenantDestructionRequest { Reason = Reason, Confirmation = tenant.Name },
                    TenantLifecycleHarness.Operator(), null, CancellationToken.None));

        Assert.Contains(TenantOffboardingReadinessCodes.AccountsReceivableOpen, refusal.Message);
        await using var verify = db.ContextFor(null);
        var record = await verify.Set<TenantOffboarding>().SingleAsync(x => x.TenantId == tenant.Id);
        Assert.Null(record.PurgeStartedOn);
        Assert.DoesNotContain(await verify.Set<TenantLifecycleEvent>().ToListAsync(),
            x => x.Action == TenantLifecycleActions.PurgeStarted);
    }

    [Fact]
    public async Task Purge_readiness_requires_persisted_erasure_proof_but_schedule_does_not()
    {
        using var db = new TenantLifecycleTestDb();
        var tenant = await SeedReadyTenantAsync(db, "erasure-proof", 91_004);
        var clock = new TenantLifecycleHarness.MutableTimeProvider();
        await using (var schedule = db.ContextFor(null))
            await TenantLifecycleHarness.Service(schedule, timeProvider: clock,
                    readiness: new TenantOffboardingReadinessService(schedule))
                .ScheduleDeletionAsync(tenant.Id,
                    new ScheduleTenantDeletionRequest { Reason = Reason },
                    TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        TenantLifecycleHarness.ElapseRetentionWindow(clock);
        await using (var blocked = db.ContextFor(null))
        {
            var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
                TenantLifecycleHarness.Service(blocked, timeProvider: clock,
                        readiness: new TenantOffboardingReadinessService(blocked))
                    .PurgeAsync(tenant.Id,
                        new ConfirmTenantDestructionRequest { Reason = Reason, Confirmation = tenant.Name },
                        TenantLifecycleHarness.Operator(), null, CancellationToken.None));
            Assert.Contains(TenantOffboardingReadinessCodes.PersonalDataErasureMissing, refusal.Message);
        }

        await using (var erase = db.ContextFor(null))
            await TenantLifecycleHarness.Service(erase, timeProvider: clock)
                .ErasePersonalDataAsync(tenant.Id,
                    new ConfirmTenantDestructionRequest { Reason = Reason, Confirmation = tenant.Name },
                    TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        await using var ready = db.ContextFor(null);
        var persisted = await ready.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenant.Id);
        var verdict = await new TenantOffboardingReadinessService(ready)
            .AssessAsync(persisted, TenantOffboardingReadinessPhase.Purge);
        Assert.True(verdict.Ready);
        Assert.NotNull((await ready.Set<TenantOffboarding>().SingleAsync()).PersonalDataErasedOn);
    }

    private static async Task<Tenant> SeedReadyTenantAsync(
        TenantLifecycleTestDb db, string slug, long businessUnitId)
    {
        var tenant = await TenantLifecycleHarness.SeedTenantAsync(
            db, slug, TenantStatus.Archived, businessUnitId);
        var now = DateTime.UtcNow;
        await using var context = db.ContextFor(null);
        var trackedTenant = await context.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenant.Id);
        trackedTenant.ModifiedOn = now.AddDays(-2);

        var rateCard = new RateCard
        {
            Code = $"offboarding-{slug}", Currency = "USD", IsActive = false,
            EffectiveFromUtc = now.AddYears(-1), EffectiveToUtc = now,
            CreatedOn = now.AddYears(-1), CreatedBy = "tests"
        };
        context.Set<RateCard>().Add(rateCard);
        await context.SaveChangesAsync();

        var statement = new BillingStatement
        {
            TenantId = tenant.Id, RateCardId = rateCard.Id,
            PeriodStartUtc = now.AddMonths(-1), PeriodEndUtc = now.AddDays(-1),
            Currency = "USD", Status = BillingStatementStatus.Final, TotalAmount = 100m,
            ReadinessStatus = BillingReadinessStatus.Ready,
            ReadinessManifestJson = "{}", ReadinessManifestSha256 = Hash,
            ComputedAtUtc = now.AddDays(-1), ComputedBy = "tests",
            FinalizedAtUtc = now.AddHours(-20), FinalizedBy = "tests"
        };
        context.Set<BillingStatement>().Add(statement);
        await context.SaveChangesAsync();

        var invoice = new SubscriptionInvoice
        {
            TenantId = tenant.Id, BillingStatementId = statement.Id,
            InvoiceNumber = $"INV-OFF-{tenant.Id}", Status = SubscriptionInvoiceStatus.Paid,
            Currency = "USD", Subtotal = 100m, TotalAmount = 100m, PaidAmount = 100m,
            IssuedAtUtc = now.AddHours(-20), DueAtUtc = now.AddHours(-19),
            SellerSnapshotJson = "{}", BuyerSnapshotJson = "{}", TaxTreatment = "none",
            SourceEvidenceJson = "{}", SourceEvidenceSha256 = Hash,
            CreatedBy = "tests", CreatedAtUtc = now.AddHours(-20),
            FinalizedBy = "tests", FinalizedAtUtc = now.AddHours(-19)
        };
        context.Set<SubscriptionInvoice>().Add(invoice);
        await context.SaveChangesAsync();

        context.Set<AccountingOutboxMessage>().Add(new AccountingOutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, SubscriptionInvoiceId = invoice.Id,
            MessageType = "invoice.finalized", IdempotencyKey = $"offboarding-{tenant.Id}",
            PayloadJson = "{}", PayloadSha256 = Hash,
            Status = AccountingOutboxStatus.Acknowledged,
            ReconciliationStatus = AccountingReconciliationStatus.Reconciled,
            CreatedAtUtc = now.AddHours(-19), AvailableAtUtc = now.AddHours(-19),
            ExternalReceiptSha256 = Hash, AcknowledgedAtUtc = now.AddHours(-18),
            AcknowledgedBy = "tests"
        });
        context.Set<TenantExportReceipt>().Add(new TenantExportReceipt
        {
            TenantId = tenant.Id, TenantSlug = tenant.Slug,
            RequestedOn = now.AddHours(-17), CompletedOn = now.AddHours(-17),
            RequestedBy = "tests", ActorPlatformUserId = 7, Sections = "[]",
            TotalRows = 1, SizeBytes = 100, ContentSha256 = Hash, Format = "json"
        });
        await context.SaveChangesAsync();
        return trackedTenant;
    }
}
