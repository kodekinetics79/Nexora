using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Billing.Metering;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class UsageMeteringAndAccountingOutboxTests
{
    [Fact]
    public async Task Usage_is_idempotent_aggregated_and_corrected_by_an_immutable_adjustment()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var service = new UsageMeteringService(db);
        var occurred = new DateTime(2026, 8, 8, 12, 34, 45, DateTimeKind.Utc);
        var original = Request(Guid.NewGuid(), 91, "documents", 10, "document", occurred, "usage-1", allowance: 4, price: 2);

        var first = await service.RecordAsync(original);
        var replay = await service.RecordAsync(original);
        Assert.Equal(first.UsageEventId, replay.UsageEventId);
        Assert.Equal(6, first.OverageQuantity);
        Assert.Equal(12, first.RatedAmount);

        var correction = Request(Guid.NewGuid(), 91, "documents", -2, "document", occurred,
            "usage-adjustment-1", adjusts: first.UsageEventId, price: 2);
        var adjustment = await service.RecordAsync(correction);
        Assert.Equal(UsageEventKind.Adjustment, adjustment.Kind);
        Assert.Equal(-4, adjustment.RatedAmount);

        var bucket = Assert.Single(await service.ReadMinutesAsync(91, occurred.AddMinutes(-1), occurred.AddMinutes(1)));
        Assert.Equal(8, bucket.Quantity);
        Assert.Equal(2, bucket.EventCount);
        Assert.Equal(2, await db.Set<UsageEvent>().CountAsync());
        await Assert.ThrowsAsync<UsageMeteringException>(() => service.RecordAsync(original with { Quantity = 11 }));
    }

    [Theory]
    [InlineData("pages.processed", "page")]
    [InlineData("pages.ocr", "page")]
    [InlineData("storage.gb-hours", "gb-hour")]
    public async Task Uncertified_meters_are_persisted_for_visibility_but_blocked_from_rating(string meter, string unit)
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var value = await new UsageMeteringService(db).RecordAsync(Request(
            Guid.NewGuid(), 92, meter, 3, unit, DateTime.UtcNow.AddMinutes(-1), $"uncertified-{meter}", price: 10));
        Assert.Equal(UsageRatingStatus.BlockedUncertifiedMeter, value.RatingStatus);
        Assert.Null(value.RatedAmount);
    }

    [Fact]
    public async Task Finalization_and_outbox_insert_commit_together_then_acknowledgement_proves_export()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var tenant = new Tenant { Id = 401, Name = "Outbox buyer", LegalName = "Outbox Buyer LLC", Slug = "outbox-buyer", BillingContactEmail = "ap@outbox.test" };
        var card = new RateCard { Id = 402, Code = "outbox-card", Currency = "USD", EffectiveFromUtc = DateTime.UtcNow.AddYears(-1) };
        var statement = new BillingStatement
        {
            Id = 403, TenantId = tenant.Id, RateCardId = card.Id, Currency = "USD", TotalAmount = 25,
            Status = BillingStatementStatus.Final, PeriodStartUtc = DateTime.UtcNow.AddMonths(-1), PeriodEndUtc = DateTime.UtcNow,
            ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker", FinalizedAtUtc = DateTime.UtcNow, FinalizedBy = "checker",
            Lines = [new BillingStatementLine { MeterKey = "documents", Description = "Documents", MeteredQuantity = 5, BillableQuantity = 5, UnitPrice = 5, Amount = 25 }]
        };
        db.AddRange(tenant, card, statement);
        await db.SaveChangesAsync();
        var outbox = new AccountingOutboxService(db);
        var invoices = new SubscriptionInvoiceService(db, outbox);
        var invoice = await invoices.CreateDraftAsync(new CreateSubscriptionInvoice(403, 0, "zero-rated", "Nexora LLC", "NX-TAX"), "maker");
        await invoices.FinalizeAsync(invoice.Id, "checker");

        var message = Assert.Single(await db.Set<AccountingOutboxMessage>().AsNoTracking().ToListAsync());
        Assert.Equal(AccountingOutboxStatus.Pending, message.Status);
        Assert.Null(message.ExternalReference);
        var claimed = Assert.Single(await outbox.ClaimAsync("erp-worker"));
        await outbox.AcknowledgeAsync(claimed.Id, claimed.LeaseToken!.Value, "ERP-9001", new string('a', 64), "erp-worker");
        db.ChangeTracker.Clear();
        message = await db.Set<AccountingOutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(AccountingOutboxStatus.Acknowledged, message.Status);
        Assert.Equal(AccountingReconciliationStatus.Reconciled, message.ReconciliationStatus);
        Assert.Equal("ERP-9001", message.ExternalReference);
    }

    [Fact]
    public async Task Failed_delivery_becomes_poison_and_requires_governed_redrive()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var message = new AccountingOutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = 10, SubscriptionInvoiceId = 20,
            MessageType = "test", IdempotencyKey = "poison-1", PayloadJson = "{}",
            PayloadSha256 = new string('b', 64), Status = AccountingOutboxStatus.Pending,
            ReconciliationStatus = AccountingReconciliationStatus.NotSent,
            MaxAttempts = 1, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1), AvailableAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        // This service-only test intentionally omits SaveChanges FK enforcement by registering
        // a matching minimal finalized invoice graph.
        var tenant = new Tenant { Id = 10, Name = "T", LegalName = "T LLC", Slug = "poison-tenant", BillingContactEmail = "ap@t.test" };
        var card = new RateCard { Id = 11, Code = "poison-card", EffectiveFromUtc = DateTime.UtcNow.AddYears(-1) };
        var statement = new BillingStatement { Id = 12, TenantId = 10, RateCardId = 11, Currency = "USD", Status = BillingStatementStatus.Final, PeriodStartUtc = DateTime.UtcNow.AddMonths(-1), PeriodEndUtc = DateTime.UtcNow, ComputedAtUtc = DateTime.UtcNow, ComputedBy = "x" };
        var invoice = new SubscriptionInvoice { Id = 20, TenantId = 10, BillingStatementId = 12, InvoiceNumber = "NX-T", Status = SubscriptionInvoiceStatus.Finalized, SellerSnapshotJson = "{}", BuyerSnapshotJson = "{}", TaxTreatment = "none", SourceEvidenceJson = "{}", SourceEvidenceSha256 = new string('c', 64), CreatedBy = "x", CreatedAtUtc = DateTime.UtcNow };
        db.AddRange(tenant, card, statement, invoice, message);
        await db.SaveChangesAsync();
        var service = new AccountingOutboxService(db);
        var claimed = Assert.Single(await service.ClaimAsync("erp-worker"));
        await service.FailAsync(claimed.Id, claimed.LeaseToken!.Value, "ERP_TIMEOUT", TimeSpan.Zero);
        db.ChangeTracker.Clear();
        Assert.Equal(AccountingOutboxStatus.Poison, (await db.Set<AccountingOutboxMessage>().SingleAsync()).Status);
        await service.RedriveAsync(message.Id, "owner@example.test", "Connector credentials repaired");
        db.ChangeTracker.Clear();
        var redriven = await db.Set<AccountingOutboxMessage>().SingleAsync();
        Assert.Equal(AccountingOutboxStatus.Pending, redriven.Status);
        Assert.Equal(0, redriven.AttemptCount);
    }

    private static RecordUsageEvent Request(Guid id, long tenantId, string type, decimal quantity,
        string unit, DateTime occurred, string key, Guid? adjusts = null, decimal allowance = 0, decimal? price = null) =>
        new(id, tenantId, type, quantity, unit, occurred, "rfq", "RFQ-1", "extraction-worker",
            null, null, null, "corr-1", key, 0.25m, "USD", new string('a', 64), adjusts,
            price is null ? null : 10, price is null ? null : 11, price is null ? null : 3, allowance, price);
}
