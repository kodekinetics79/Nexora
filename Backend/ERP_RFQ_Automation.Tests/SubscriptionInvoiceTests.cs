using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class SubscriptionInvoiceTests
{
    [Fact]
    public async Task Final_statement_to_invoice_credit_payment_preserves_frozen_evidence_and_ar()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var tenant = new Tenant
        {
            Id = 71, Name = "Buyer", LegalName = "Buyer LLC", Slug = "buyer",
            Status = TenantStatus.Active, BillingContactEmail = "ap@buyer.example",
            PaymentTermsDays = 30
        };
        var card = new RateCard
        {
            Id = 81, Code = "standard", Currency = "USD", IsActive = true,
            EffectiveFromUtc = DateTime.UtcNow.AddYears(-1)
        };
        var statement = new BillingStatement
        {
            Id = 91, TenantId = tenant.Id, RateCardId = card.Id,
            PeriodStartUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = BillingStatementStatus.Final, Currency = "USD", TotalAmount = 100m,
            ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker@example.com",
            FinalizedAtUtc = DateTime.UtcNow, FinalizedBy = "owner@example.com",
            Lines = [new BillingStatementLine
            {
                MeterKey = BillingMeterKeys.Documents, Description = "Documents", MeteredQuantity = 10,
                BillableQuantity = 10, UnitPrice = 10, Amount = 100
            }]
        };
        context.AddRange(tenant, card, statement);
        await context.SaveChangesAsync();

        var service = new SubscriptionInvoiceService(context);
        var invoice = await service.CreateDraftAsync(
            new CreateSubscriptionInvoice(statement.Id, 15, "standard VAT", "Nexora LLC", "TAX-1"),
            "billing@example.com");
        var evidenceHash = invoice.SourceEvidenceSha256;
        Assert.Equal(115m, invoice.TotalAmount);
        Assert.Equal(64, evidenceHash.Length);

        await Assert.ThrowsAsync<BillingConflictException>(() =>
            service.FinalizeAsync(invoice.Id, "billing@example.com"));
        invoice = await service.FinalizeAsync(invoice.Id, "owner@example.com");
        Assert.StartsWith("NX-", invoice.InvoiceNumber);

        var firstCredit = await service.CreditAsync(
            invoice.Id, 15m, "Service level credit", "owner@example.com", "credit-1");
        var replayedCredit = await service.CreditAsync(
            invoice.Id, 15m, "Service level credit", "owner@example.com", "credit-1");
        Assert.Equal(firstCredit.Id, replayedCredit.Id);
        await Assert.ThrowsAsync<BillingConflictException>(() => service.CreditAsync(
            invoice.Id, 14m, "Different credit terms", "owner@example.com", "credit-1"));
        await service.RecordPaymentAsync(
            invoice.Id, 40m, "bank-ref-1", DateTime.UtcNow, "billing@example.com");

        context.ChangeTracker.Clear();
        invoice = await context.Set<SubscriptionInvoice>().AsNoTracking().SingleAsync();
        Assert.Equal(60m, invoice.TotalAmount - invoice.CreditedAmount - invoice.PaidAmount);
        Assert.Equal(SubscriptionInvoiceStatus.PartiallyPaid, invoice.Status);
        Assert.Equal(evidenceHash, invoice.SourceEvidenceSha256);
    }

    [Fact]
    public async Task Invoice_creation_is_idempotent_per_final_statement()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var tenant = new Tenant
        {
            Id = 1, Name = "Buyer", LegalName = "Buyer LLC", Slug = "buyer-2",
            BillingContactEmail = "ap@buyer.example"
        };
        var card = new RateCard { Id = 2, Code = "card-2", EffectiveFromUtc = DateTime.UtcNow.AddDays(-1) };
        var statement = new BillingStatement
        {
            Id = 3, TenantId = 1, RateCardId = 2, Currency = "USD", Status = BillingStatementStatus.Final,
            PeriodStartUtc = DateTime.UtcNow.Date.AddDays(-31), PeriodEndUtc = DateTime.UtcNow.Date,
            TotalAmount = 10, ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker",
            FinalizedAtUtc = DateTime.UtcNow, FinalizedBy = "checker",
            Lines = [new BillingStatementLine
            {
                MeterKey = BillingMeterKeys.Documents, Description = "Documents",
                MeteredQuantity = 1, BillableQuantity = 1, UnitPrice = 10, Amount = 10
            }]
        };
        context.AddRange(tenant, card, statement);
        await context.SaveChangesAsync();
        var service = new SubscriptionInvoiceService(context);
        var request = new CreateSubscriptionInvoice(3, 0, "zero rated", "Nexora", "TAX-1");

        var first = await service.CreateDraftAsync(request, "maker");
        var replay = await service.CreateDraftAsync(request, "maker");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await context.Set<SubscriptionInvoice>().CountAsync());

        await Assert.ThrowsAsync<BillingConflictException>(() => service.CreateDraftAsync(
            request with { TaxRatePercent = 7.5m }, "maker"));
    }

    [Fact]
    public async Task Invoice_creation_refuses_a_final_statement_whose_header_and_lines_do_not_reconcile()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        var tenant = new Tenant
        {
            Id = 101, Name = "Mismatch", LegalName = "Mismatch LLC", Slug = "mismatch",
            BillingContactEmail = "ap@mismatch.example"
        };
        var card = new RateCard { Id = 102, Code = "mismatch-card", EffectiveFromUtc = DateTime.UtcNow.AddDays(-1) };
        var statement = new BillingStatement
        {
            Id = 103, TenantId = tenant.Id, RateCardId = card.Id, Currency = "USD",
            Status = BillingStatementStatus.Final, TotalAmount = 100,
            PeriodStartUtc = DateTime.UtcNow.Date.AddDays(-31), PeriodEndUtc = DateTime.UtcNow.Date,
            ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker",
            FinalizedAtUtc = DateTime.UtcNow, FinalizedBy = "checker",
            Lines = [new BillingStatementLine
            {
                MeterKey = BillingMeterKeys.Documents, Description = "Documents",
                MeteredQuantity = 9, BillableQuantity = 9, UnitPrice = 10, Amount = 90
            }]
        };
        context.AddRange(tenant, card, statement);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BillingConflictException>(() =>
            new SubscriptionInvoiceService(context).CreateDraftAsync(
                new CreateSubscriptionInvoice(statement.Id, 0, "zero rated", "Nexora", "TAX-1"),
                "maker"));

        Assert.Contains("does not reconcile", error.Message);
        Assert.Empty(await context.Set<SubscriptionInvoice>().ToListAsync());
    }
}
