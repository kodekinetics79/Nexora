using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The dialect half of "finance can invoice an order the customer accepted through a Client PO"
/// (docs/audit/SCENARIOS-QUOTE-TO-CASH-2026-09-05.md, F3). The service rule lives in
/// <c>IsInvoiceEligibleOrderAsync</c>; the SAME rule lives again in the PostgreSQL issue trigger
/// <c>nexora_receivable_issued_immutable()</c>, which SQLite never runs. Once the service admitted an
/// award-backed DRAFT order the trigger still refused it at issue with 23514 "the source order is not
/// eligible for invoicing" — surfaced to finance as "The request conflicts with a concurrent or
/// existing financial record. Reload and try again." Migration 20260905120000 adds the clause.
///
/// <para>The order is raised the way production raises it — Client PO → award → confirm → convert —
/// so the row carries exactly what <c>ConvertToOrder</c> writes (DRAFT, CUSTOMER_AWARD). Verified by
/// reverting: with that migration file removed this test fails on the trigger's 23514; with it the
/// draft is numbered.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class InvoiceEligibilityPostgreSqlTests(PostgreSqlTestDatabase database)
{
    // The award fixture's graph (CustomerAwardTestFixture.SeedGraph) under the same tenant the
    // award tests use; every id below is that fixture's.
    private const long BusinessUnitId = 889_001;
    private const long CustomerId = 880_002;
    private const long CurrencyId = 880_003;
    private const long QuoteId = 880_011;
    private const long QuoteItemId = 880_012;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_order_raised_from_a_confirmed_client_PO_is_invoiced_and_issued_on_PostgreSQL()
    {
        long commercialCaseId;
        await using (var seed = database.ContextFor(null))
        {
            CustomerAwardTestFixture.SeedGraph(seed, BusinessUnitId, twoQuoteLines: true);
            commercialCaseId = await seed.Leads.IgnoreQueryFilters()
                .Where(x => x.Id == 880_009).Select(x => x.CommercialCaseId).SingleAsync();
        }

        long orderId = 0;
        try
        {
            await using (var context = database.ContextFor(BusinessUnitId))
            {
                var awards = new CustomerAwardApplicationService(context);
                var purchaseOrder = await awards.CreatePurchaseOrderAsync(BusinessUnitId, "pg-inv-po", "pg-inv-po",
                    new CreateCustomerPurchaseOrderCommand(QuoteId, commercialCaseId, CustomerId, CurrencyId, "INVOICE PO 2026 / 9",
                        DateTime.UtcNow.Date, DateTime.UtcNow.Date, 0,
                        [new("1", 880_004, "Awarded widget", 4m, null, 100m, 400m)]), "tests");
                var award = await awards.CreateAwardAsync(BusinessUnitId, "pg-inv-award", "pg-inv-award",
                    new CreateCustomerAwardCommand(purchaseOrder.Id, QuoteId, 0, purchaseOrder.Version, 1,
                        [new(purchaseOrder.Lines.Single().Id, QuoteItemId, 4m)]), "tests");
                var confirmed = await awards.ConfirmAwardAsync(BusinessUnitId, award.Id, "pg-inv-confirm", "pg-inv-confirm",
                    new(award.Version), "tests");
                var order = await awards.ConvertToOrderAsync(BusinessUnitId, confirmed.Id, "pg-inv-convert", "pg-inv-convert",
                    new(confirmed.Version), "tests");
                orderId = order.Id;
                Assert.Equal("DRAFT", order.Status);
            }

            await using (var context = database.ContextFor(BusinessUnitId))
            {
                var finance = new CommercialFinanceApplicationService(context);
                var draft = await finance.CreateInvoiceAsync(BusinessUnitId, orderId, "pg-inv-draft",
                    new CreateInvoiceRequest(null, null, null), "finance@nexora.invalid");
                Assert.Equal(ReceivableDocumentStatuses.Draft, draft.Status);
                Assert.Equal(CurrencyId, draft.CurrencyId);

                var issued = await finance.IssueAsync(BusinessUnitId, draft.Id,
                    new IssueDocumentRequest(draft.Version), "finance@nexora.invalid");

                Assert.Equal(ReceivableDocumentStatuses.Issued, issued.Status);
                Assert.Matches(@"^INV-\d{4}-\d{6}$", issued.DocumentNumber);
            }
        }
        finally
        {
            // Leave the fixture graph exactly as CustomerAwardPostgreSqlTests expects to find it (it
            // counts the tenant's awards). Issued documents and converted awards are immutable by
            // trigger, so the disposable test database's owner lifts the row triggers for the sweep.
            await using var cleanup = database.ContextFor(null);
            var tables = new[]
            {
                "ReceivableDocumentLines", "ReceivableDocuments", "OrderItems", "Orders",
                "CustomerAwardLineAllocations", "CustomerAwards", "CustomerPurchaseOrderLines", "CustomerPurchaseOrders",
            };
            foreach (var table in tables)
                await cleanup.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" DISABLE TRIGGER ALL");
            try
            {
                foreach (var sql in new[]
                         {
                             """DELETE FROM "ReceivableDocumentLines" WHERE "BusinessUnitId" = {0}""",
                             """DELETE FROM "ReceivableDocuments" WHERE "BusinessUnitId" = {0}""",
                             """DELETE FROM "OrderItems" WHERE "OrderID" IN (SELECT "ID" FROM "Orders" WHERE "BusinessUnitID" = {0} AND "SourceType" = 'CUSTOMER_AWARD')""",
                             """DELETE FROM "Orders" WHERE "BusinessUnitID" = {0} AND "SourceType" = 'CUSTOMER_AWARD'""",
                             """DELETE FROM "CustomerAwardLineAllocations" WHERE "BusinessUnitId" = {0}""",
                             """DELETE FROM "CustomerAwards" WHERE "BusinessUnitId" = {0}""",
                             """DELETE FROM "CustomerPurchaseOrderLines" WHERE "BusinessUnitId" = {0}""",
                             """DELETE FROM "CustomerPurchaseOrders" WHERE "BusinessUnitId" = {0}""",
                         })
                    await cleanup.Database.ExecuteSqlRawAsync(sql, BusinessUnitId);
            }
            finally
            {
                foreach (var table in tables)
                    await cleanup.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ENABLE TRIGGER ALL");
            }
        }
    }
}
