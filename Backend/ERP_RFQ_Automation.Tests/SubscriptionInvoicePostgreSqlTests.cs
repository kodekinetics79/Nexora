using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SubscriptionInvoicePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_invoice_creation_is_idempotent_and_posted_evidence_is_database_immutable()
    {
        var suffix = Guid.NewGuid().ToString("N");
        long statementId;
        await using (var seed = database.ContextFor(null))
        {
            var tenant = new Tenant
            {
                Name = $"Invoice buyer {suffix}", LegalName = $"Invoice buyer {suffix} LLC",
                Slug = $"invoice-buyer-{suffix}", Status = TenantStatus.Active,
                BillingContactEmail = $"ap-{suffix}@example.test", PaymentTermsDays = 30
            };
            var card = new RateCard
            {
                Code = $"invoice-card-{suffix}", Currency = "USD", IsActive = true,
                EffectiveFromUtc = DateTime.UtcNow.AddYears(-1)
            };
            seed.AddRange(tenant, card);
            await seed.SaveChangesAsync();
            var statement = new BillingStatement
            {
                TenantId = tenant.Id, RateCardId = card.Id,
                PeriodStartUtc = new DateTime(2026, 7, 1), PeriodEndUtc = new DateTime(2026, 8, 1),
                Status = BillingStatementStatus.Draft, Currency = "USD", TotalAmount = 100m,
                ReadinessStatus = BillingReadinessStatus.Ready,
                ReadinessManifestJson = "{\"ready\":true}",
                ReadinessManifestSha256 = "b342fc286d0216cc212e0d7ba234894e2e7283ddf14f959adf0fe7fd5924308a",
                ComputedAtUtc = DateTime.UtcNow, ComputedBy = "maker@example.test",
                FinalizedAtUtc = DateTime.UtcNow, FinalizedBy = "checker@example.test",
                Lines = [new BillingStatementLine
                {
                    MeterKey = BillingMeterKeys.Documents, Description = "Documents",
                    MeteredQuantity = 10, BillableQuantity = 10, UnitPrice = 10, Amount = 100
                }]
            };
            seed.Add(statement);
            await seed.SaveChangesAsync();
            statement.Status = BillingStatementStatus.Final;
            await seed.SaveChangesAsync();
            statementId = statement.Id;
        }

        // This test isolates invoice concurrency/immutability. Taxable invoices now require a
        // separately approved jurisdiction rule and are covered by the revenue-control PG lane.
        var request = new CreateSubscriptionInvoice(statementId, 0m, "not taxable", "Nexora LLC", "TAX-1");
        var first = Create(request);
        var second = Create(request);
        var invoices = await Task.WhenAll(first, second);
        Assert.Equal(invoices[0], invoices[1]);

        await using var verification = database.ContextFor(null);
        var persisted = await verification.Set<SubscriptionInvoice>().AsNoTracking()
            .SingleAsync(invoice => invoice.BillingStatementId == statementId);
        Assert.Equal(64, persisted.SourceEvidenceSha256.Length);
        var finalized = await Task.WhenAll(
            Finalize(persisted.Id), Finalize(persisted.Id));
        Assert.Equal(finalized[0], finalized[1]);

        var creditKey = $"credit-{suffix}";
        var credits = await Task.WhenAll(
            Credit(persisted.Id, creditKey), Credit(persisted.Id, creditKey));
        Assert.Equal(credits[0], credits[1]);

        var receivedAtUtc = DateTime.UtcNow;
        var paymentReference = $"payment-{suffix}";
        var payments = await Task.WhenAll(
            Pay(persisted.Id, paymentReference, receivedAtUtc),
            Pay(persisted.Id, paymentReference, receivedAtUtc));
        Assert.Equal(payments[0], payments[1]);
        verification.ChangeTracker.Clear();
        Assert.Single(await verification.Set<SubscriptionCreditNote>()
            .Where(value => value.SubscriptionInvoiceId == persisted.Id).ToListAsync());
        Assert.Single(await verification.Set<SubscriptionPayment>()
            .Where(value => value.SubscriptionInvoiceId == persisted.Id).ToListAsync());

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE platform."SubscriptionInvoices"
            SET "SourceEvidenceSha256" = repeat('0', 64)
            WHERE "Id" = @id
            """;
        command.Parameters.AddWithValue("id", persisted.Id);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);

        await using var statusCommand = connection.CreateCommand();
        statusCommand.CommandText = """
            UPDATE platform."SubscriptionInvoices" SET "Status" = 'Draft' WHERE "Id" = @id
            """;
        statusCommand.Parameters.AddWithValue("id", persisted.Id);
        var statusException = await Assert.ThrowsAsync<PostgresException>(() => statusCommand.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, statusException.SqlState);

        await using var amountCommand = connection.CreateCommand();
        amountCommand.CommandText = """
            UPDATE platform."SubscriptionInvoices"
            SET "PaidAmount" = "TotalAmount" + 1
            WHERE "Id" = @id
            """;
        amountCommand.Parameters.AddWithValue("id", persisted.Id);
        await Assert.ThrowsAsync<PostgresException>(() => amountCommand.ExecuteNonQueryAsync());
    }

    private async Task<long> Create(CreateSubscriptionInvoice request)
    {
        await using var context = database.ContextFor(null);
        return (await new SubscriptionInvoiceService(context)
            .CreateDraftAsync(request, "billing-maker@example.test")).Id;
    }

    private async Task<long> Finalize(long invoiceId)
    {
        await using var context = database.ContextFor(null);
        return (await new SubscriptionInvoiceService(context)
            .FinalizeAsync(invoiceId, "independent-owner@example.test")).Id;
    }

    private async Task<long> Credit(long invoiceId, string key)
    {
        await using var context = database.ContextFor(null);
        return (await new SubscriptionInvoiceService(context)
            .CreditAsync(invoiceId, 10m, "Concurrent service credit", "owner@example.test", key)).Id;
    }

    private async Task<long> Pay(long invoiceId, string reference, DateTime receivedAtUtc)
    {
        await using var context = database.ContextFor(null);
        return (await new SubscriptionInvoiceService(context)
            .RecordPaymentAsync(invoiceId, 10m, reference, receivedAtUtc, "collector@example.test")).Id;
    }
}
