using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect SDET journey for the operational AR boundary. The portable invoice test
/// covers the same arithmetic, but cannot prove the PostgreSQL constraints, triggers and enum
/// conversions accept the complete posted-invoice path.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SubscriptionBillingJourneyPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Final_statement_to_invoice_credit_and_payment_reconciles_persisted_ar()
    {
        var suffix = Guid.NewGuid().ToString("N");
        long statementId;

        await using (var seed = database.ContextFor(null))
        {
            var tenant = new Tenant
            {
                Name = $"AR buyer {suffix}",
                LegalName = $"AR buyer {suffix} LLC",
                Slug = $"ar-buyer-{suffix}",
                Status = TenantStatus.Active,
                BillingContactEmail = $"ap-{suffix}@example.test",
                PaymentTermsDays = 30
            };
            var card = new RateCard
            {
                Code = $"ar-card-{suffix}",
                Currency = "USD",
                IsActive = true,
                EffectiveFromUtc = DateTime.UtcNow.AddYears(-1)
            };
            seed.AddRange(tenant, card);
            await seed.SaveChangesAsync();

            var statement = new BillingStatement
            {
                TenantId = tenant.Id,
                RateCardId = card.Id,
                PeriodStartUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                PeriodEndUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = BillingStatementStatus.Draft,
                ReadinessStatus = BillingReadinessStatus.Ready,
                ReadinessManifestJson = "{\"ready\":true}",
                ReadinessManifestSha256 = "b342fc286d0216cc212e0d7ba234894e2e7283ddf14f959adf0fe7fd5924308a",
                Currency = "USD",
                TotalAmount = 100m,
                ComputedAtUtc = DateTime.UtcNow,
                ComputedBy = "statement-maker@example.test",
                FinalizedAtUtc = DateTime.UtcNow,
                FinalizedBy = "statement-checker@example.test",
                Lines = [new BillingStatementLine
                {
                    MeterKey = BillingMeterKeys.Documents,
                    Description = "Documents",
                    MeteredQuantity = 10,
                    BillableQuantity = 10,
                    UnitPrice = 10,
                    Amount = 100
                }]
            };
            seed.Add(statement);
            await seed.SaveChangesAsync();
            statement.Status = BillingStatementStatus.Final;
            await seed.SaveChangesAsync();
            statementId = statement.Id;
        }

        long invoiceId;
        string evidenceHash;
        await using (var create = database.ContextFor(null))
        {
            var service = new SubscriptionInvoiceService(create);
            // Tax jurisdiction/rule evidence is independently certified by the final revenue-control
            // PG lane. This journey isolates append-only credit/payment AR reconciliation.
            var invoice = await service.CreateDraftAsync(
                new CreateSubscriptionInvoice(
                    statementId, 0m, "not taxable", "Nexora LLC", "NEXORA-TAX-1"),
                "invoice-maker@example.test");
            invoice = await service.FinalizeAsync(invoice.Id, "invoice-checker@example.test");
            invoiceId = invoice.Id;
            evidenceHash = invoice.SourceEvidenceSha256;
        }

        await using (var collect = database.ContextFor(null))
        {
            var service = new SubscriptionInvoiceService(collect);
            await service.CreditAsync(
                invoiceId, 15m, "Contractual service-level credit", "owner@example.test",
                $"credit-{suffix}");
            await service.RecordPaymentAsync(
                invoiceId, 40m, $"bank-{suffix}", DateTime.UtcNow, "collector@example.test");
        }

        await using var verify = database.ContextFor(null);
        var persisted = await verify.Set<SubscriptionInvoice>().AsNoTracking()
            .Include(invoice => invoice.Credits)
            .Include(invoice => invoice.Payments)
            .SingleAsync(invoice => invoice.Id == invoiceId);

        Assert.Equal(100m, persisted.TotalAmount);
        Assert.Equal(15m, persisted.CreditedAmount);
        Assert.Equal(40m, persisted.PaidAmount);
        Assert.Equal(45m, persisted.TotalAmount - persisted.CreditedAmount - persisted.PaidAmount);
        Assert.Equal(SubscriptionInvoiceStatus.PartiallyPaid, persisted.Status);
        Assert.Equal(evidenceHash, persisted.SourceEvidenceSha256);
        Assert.Single(persisted.Credits);
        Assert.Single(persisted.Payments);
    }
}
