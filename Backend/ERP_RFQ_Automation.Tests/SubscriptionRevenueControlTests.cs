using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class SubscriptionRevenueControlTests
{
    [Fact]
    public async Task Tax_determination_requires_one_approved_maker_checker_rule_and_freezes_evidence()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        await SeedActorsAsync(db);
        var service = new SubscriptionTaxService(db);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var command = new ProposeSubscriptionTaxRule("GB-VAT", "GB", "GBP", "standard VAT",
            20m, "UK VAT Act 1994; reviewed legal memorandum LEGAL-42", new string('a', 64), start, null);
        var proposed = await service.ProposeAsync(command, 101);
        await Assert.ThrowsAsync<BillingConflictException>(() => service.ApproveAsync(proposed.Id, 101));
        await Assert.ThrowsAsync<BillingConflictException>(() => service.DetermineAsync(
            Buyer("GB"), "GBP", "GB-VAT", start.AddDays(1)));

        await service.ApproveAsync(proposed.Id, 202);
        var result = await service.DetermineAsync(Buyer("GB"), "GBP", "GB-VAT", start.AddDays(1));

        Assert.Equal(20m, result.RatePercent);
        Assert.Equal(proposed.Id, result.RuleId);
        Assert.Equal(64, result.EvidenceSha256.Length);
        Assert.Contains("LEGAL-42", result.EvidenceJson);
    }

    [Fact]
    public async Task Tax_determination_fails_closed_for_unsupported_jurisdiction()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var error = await Assert.ThrowsAsync<BillingConflictException>(() =>
            new SubscriptionTaxService(db).DetermineAsync(Buyer("US"), "USD", "US-NY", DateTime.UtcNow));
        Assert.Contains("missing or ambiguous", error.Message);
    }

    [Fact]
    public async Task Writeoff_requires_distinct_checker_and_emits_one_reconcilable_outbox_message()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var invoice = await SeedInvoiceAsync(db, 100m, 40m);
        var service = new SubscriptionRevenueControlService(db, new AccountingOutboxService(db));
        var command = new ProposeRevenueAction(SubscriptionRevenueActionKind.WriteOff, 60m, "USD",
            "Insolvency evidence and approved collections exhaustion", new string('b', 64), null, "writeoff-1");
        var proposed = await service.ProposeAsync(invoice.Id, command, 101);
        await Assert.ThrowsAsync<BillingConflictException>(() => service.ApproveAsync(proposed.Id, 101));

        var completed = await service.ApproveAsync(proposed.Id, 202);
        db.ChangeTracker.Clear();
        var persisted = await db.Set<SubscriptionInvoice>().SingleAsync();
        var messages = await db.Set<AccountingOutboxMessage>().ToListAsync();
        Assert.Equal(SubscriptionRevenueActionStatus.Completed, completed.Status);
        Assert.Equal(60m, persisted.WrittenOffAmount);
        Assert.Single(messages);
        Assert.Equal(completed.Id, messages[0].SubscriptionRevenueActionId);
        Assert.Equal("subscription-invoice.writeoff", messages[0].MessageType);
    }

    [Fact]
    public async Task Refund_and_payment_reversal_are_bounded_and_idempotent()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        var invoice = await SeedInvoiceAsync(db, 100m, 100m);
        var service = new SubscriptionRevenueControlService(db, new AccountingOutboxService(db));
        var refund = new ProposeRevenueAction(SubscriptionRevenueActionKind.Refund, 25m, "USD",
            "Approved service credit returned to customer", new string('c', 64), "BANK-REFUND-1", "refund-1");
        var first = await service.ProposeAsync(invoice.Id, refund, 101);
        var replay = await service.ProposeAsync(invoice.Id, refund, 101);
        Assert.Equal(first.Id, replay.Id);
        await Assert.ThrowsAsync<BillingConflictException>(() => service.ProposeAsync(invoice.Id,
            refund with { Reason = "Different refund evidence under the same key" }, 101));
        await service.ApproveAsync(first.Id, 202);
        await Assert.ThrowsAsync<BillingConflictException>(() => service.ProposeAsync(invoice.Id,
            refund with { IdempotencyKey = "refund-too-large", Amount = 76m }, 101));

        var reversal = await service.ProposeAsync(invoice.Id, new(SubscriptionRevenueActionKind.PaymentReversal,
            75m, "USD", "Bank confirmed original payment was reversed", new string('d', 64),
            "BANK-REVERSAL-1", "reversal-1"), 101);
        await service.ApproveAsync(reversal.Id, 202);
        db.ChangeTracker.Clear();
        invoice = await db.Set<SubscriptionInvoice>().SingleAsync();
        Assert.Equal(25m, invoice.RefundedAmount);
        Assert.Equal(75m, invoice.ReversedPaymentAmount);
        Assert.Equal(2, await db.Set<AccountingOutboxMessage>().CountAsync());
    }

    [Fact]
    public async Task Void_requires_checker_while_dunning_is_automated_and_daily_idempotent()
    {
        using (var database = new TestDb())
        await using (var db = database.ContextFor(null))
        {
            var invoice = await SeedInvoiceAsync(db, 100m, 0m);
            invoice.Status = SubscriptionInvoiceStatus.Finalized;
            await db.SaveChangesAsync();
            var service = new SubscriptionRevenueControlService(db, new AccountingOutboxService(db));
            var proposed = await service.ProposeAsync(invoice.Id, new(SubscriptionRevenueActionKind.Void,
                100, "USD", "Contract was rescinded before any settlement", new string('f', 64), null, "void-1"), 101);
            await Assert.ThrowsAsync<BillingConflictException>(() => service.ApproveAsync(proposed.Id, 101));
            await service.ApproveAsync(proposed.Id, 202);
            Assert.Equal(SubscriptionInvoiceStatus.Void, invoice.Status);
        }

        using (var database = new TestDb())
        await using (var db = database.ContextFor(null))
        {
            var invoice = await SeedInvoiceAsync(db, 100m, 0m);
            invoice.Status = SubscriptionInvoiceStatus.Finalized;
            await db.SaveChangesAsync();
            var service = new SubscriptionRevenueControlService(db, new AccountingOutboxService(db));
            var command = new ProposeRevenueAction(SubscriptionRevenueActionKind.Dunning, 0, "USD",
                "Automated overdue receivable dunning occurrence", new string('1', 64), null, "dunning-1");
            var first = await service.ProposeAsync(invoice.Id, command, 0);
            var replay = await service.ProposeAsync(invoice.Id, command, 0);
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(SubscriptionRevenueActionStatus.Completed, first.Status);
            Assert.Null(first.ProposedByPlatformUserId);
            Assert.Null(first.ApprovedByPlatformUserId);
            Assert.Single(await db.Set<AccountingOutboxMessage>().ToListAsync());
        }
    }

    private static Tenant Buyer(string country) => new()
    {
        Id = 1, Name = "Buyer", LegalName = "Buyer Ltd", Slug = "buyer-tax",
        BillingContactEmail = "ap@buyer.test", CountryCode = country
    };

    private static async Task<SubscriptionInvoice> SeedInvoiceAsync(
        ERP_RFQ_Automation.Models.ErpRfqAutomationContext db, decimal total, decimal paid, decimal credited = 0)
    {
        await SeedActorsAsync(db);
        var tenant = Buyer("US");
        var card = new RateCard { Id = 2, Code = $"card-{Guid.NewGuid():N}", Currency = "USD", EffectiveFromUtc = DateTime.UtcNow.AddYears(-1) };
        var statement = new BillingStatement { Id = 3, TenantId = 1, RateCardId = 2, Currency = "USD",
            Status = BillingStatementStatus.Final, PeriodStartUtc = DateTime.UtcNow.AddMonths(-2),
            PeriodEndUtc = DateTime.UtcNow.AddMonths(-1), ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker" };
        var invoice = new SubscriptionInvoice
        {
            Id = 4, TenantId = 1, BillingStatementId = 3, InvoiceNumber = "NX-TEST-4",
            Status = paid >= total ? SubscriptionInvoiceStatus.Paid : SubscriptionInvoiceStatus.PartiallyPaid,
            Currency = "USD", Subtotal = total, TotalAmount = total, PaidAmount = paid, CreditedAmount = credited,
            IssuedAtUtc = DateTime.UtcNow.AddMonths(-1), DueAtUtc = DateTime.UtcNow.AddDays(-1),
            SellerSnapshotJson = "{}", BuyerSnapshotJson = "{}", TaxTreatment = "none",
            SourceEvidenceJson = "{}", SourceEvidenceSha256 = new string('e', 64), CreatedBy = "maker", CreatedAtUtc = DateTime.UtcNow
        };
        db.AddRange(tenant, card, statement, invoice); await db.SaveChangesAsync(); return invoice;
    }

    private static async Task SeedActorsAsync(ERP_RFQ_Automation.Models.ErpRfqAutomationContext db)
    {
        if (await db.Set<PlatformUser>().AnyAsync()) return;
        db.AddRange(
            new PlatformUser { Id = 101, Email = "maker@nexora.test", PasswordHash = "test", PlatformRole = PlatformRole.Owner },
            new PlatformUser { Id = 202, Email = "checker@nexora.test", PasswordHash = "test", PlatformRole = PlatformRole.Owner });
        await db.SaveChangesAsync();
    }
}
