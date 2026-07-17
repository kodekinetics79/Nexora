using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Agent.Tools;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// capture_supplier_quote executed for real against the SQLite-backed context: it must
/// write SupplierQuotedItem rows carrying the RFQ linkage in the QuoteReference string
/// (format "rfq={id};item={id};lead={days}" — the schema has no RfqId/LeadTime columns),
/// stamp the tenant + acting user, fall back to RFQ-line quantity/name when a quoted
/// line omits them, and flip the supplier's Sent solicitation to Responded.
/// </summary>
public class SourcingToolTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;
    private const long RfqId = 700;
    private const long SupplierId = 500;

    private static readonly AgentToolContext Bu1Ctx = new() { BusinessUnitId = Bu1, UserId = 42, UserName = "tester" };

    private static void SeedSourcingGraph(TestDb db)
    {
        using var seed = db.ContextFor(null);
        AgentSeed.Supplier(seed, SupplierId, buid: Bu1, name: "Bolt Traders", email: "sales@bolts.example");
        AgentSeed.Rfq(seed, RfqId, Bu1);
        AgentSeed.RfqItem(seed, id: 7001, rfqId: RfqId, productName: "Hex Bolt M8", quantity: 25);
        AgentSeed.RfqItem(seed, id: 7002, rfqId: RfqId, productName: "Gasket 40mm", quantity: 10);
        AgentSeed.Solicitation(seed, id: 1, businessUnitId: Bu1, rfqId: RfqId, supplierId: SupplierId,
            status: SolicitationStatus.Sent);
        seed.SaveChanges();
    }

    [Fact]
    public async Task CaptureSupplierQuote_WritesQuotedItems_AndFlipsSolicitationToResponded()
    {
        using var db = new TestDb();
        SeedSourcingGraph(db);

        using (var ctx = db.ContextFor(Bu1))
        {
            var tool = new CaptureSupplierQuoteTool(ctx);
            var input = AgentSeed.Json(
                "{\"rfqId\":700,\"supplierId\":500,\"lines\":[" +
                "{\"rfqItemId\":7001,\"unitPrice\":12.5,\"quantity\":20,\"leadTimeDays\":14}," +
                "{\"rfqItemId\":7002,\"unitPrice\":3.75}]}");

            var result = await tool.ExecuteAsync(input, Bu1Ctx, CancellationToken.None);

            Assert.True(result.Success, result.Error);

            // The anonymous result payload is contract too — assert via JSON round-trip.
            var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;
            Assert.Equal(2, payload.GetProperty("linesCaptured").GetInt32());
            Assert.True(payload.GetProperty("solicitationUpdated").GetBoolean());
        }

        // Verify persisted state with a fresh, unfiltered context.
        using var verify = db.ContextFor(null);
        var items = verify.SupplierQuotedItems.AsNoTracking()
            .Where(q => q.SupplierId == SupplierId)
            .OrderBy(q => q.QuoteReference)
            .ToList();

        Assert.Equal(2, items.Count);

        // QuoteReference carries the RFQ linkage in the documented encoding.
        var line1 = Assert.Single(items, i => i.QuoteReference == "rfq=700;item=7001;lead=14");
        Assert.Equal(12.5m, line1.UnitPrice);
        Assert.Equal(20m, line1.Quantity);                    // explicit quantity wins
        Assert.Equal("Hex Bolt M8", line1.ItemName);          // name from the RFQ line

        var line2 = Assert.Single(items, i => i.QuoteReference == "rfq=700;item=7002;lead=");
        Assert.Equal(3.75m, line2.UnitPrice);
        Assert.Equal(10m, line2.Quantity);                    // falls back to RFQ-line quantity
        Assert.Equal("Gasket 40mm", line2.ItemName);

        Assert.All(items, i =>
        {
            Assert.Equal(Bu1, i.BusinessUnitId);              // tenant stamped from ctx, not input
            Assert.Equal("tester", i.CreatedBy);              // acting user from JWT-derived ctx
            Assert.True(i.IsActive);
        });

        // The matching Sent solicitation is now Responded with a response timestamp.
        var solicitation = verify.Set<SupplierSolicitation>().AsNoTracking().Single(s => s.Id == 1);
        Assert.Equal(SolicitationStatus.Responded, solicitation.Status);
        Assert.NotNull(solicitation.RespondedOn);
    }

    [Fact]
    public async Task CaptureSupplierQuote_CrossTenantRfq_IsInvisibleAndRejected()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            // RFQ + supplier belong to BU2; the tool runs as BU1.
            AgentSeed.Supplier(seed, SupplierId, buid: Bu2, name: "Other Tenant Supplier");
            AgentSeed.Rfq(seed, RfqId, Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var tool = new CaptureSupplierQuoteTool(ctx);
        var input = AgentSeed.Json(
            "{\"rfqId\":700,\"supplierId\":500,\"lines\":[{\"rfqItemId\":7001,\"unitPrice\":1}]}");

        var result = await tool.ExecuteAsync(input, Bu1Ctx, CancellationToken.None);

        // The tenant filter makes the foreign RFQ look nonexistent — request rejected...
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);

        // ...and nothing was written.
        using var verify = db.ContextFor(null);
        Assert.Equal(0, verify.SupplierQuotedItems.Count());
    }

    [Fact]
    public async Task CaptureSupplierQuote_LineMissingUnitPrice_FailsWithoutPersistingAnything()
    {
        using var db = new TestDb();
        SeedSourcingGraph(db);

        using var ctx = db.ContextFor(Bu1);
        var tool = new CaptureSupplierQuoteTool(ctx);
        var input = AgentSeed.Json(
            "{\"rfqId\":700,\"supplierId\":500,\"lines\":[" +
            "{\"rfqItemId\":7001,\"unitPrice\":2.5}," +
            "{\"rfqItemId\":7002}]}"); // second line has no unitPrice

        var result = await tool.ExecuteAsync(input, Bu1Ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unitPrice", result.Error);

        // All-or-nothing: the valid first line must not have been saved either,
        // and the solicitation stays Sent.
        using var verify = db.ContextFor(null);
        Assert.Equal(0, verify.SupplierQuotedItems.Count());
        Assert.Equal(SolicitationStatus.Sent,
            verify.Set<SupplierSolicitation>().AsNoTracking().Single(s => s.Id == 1).Status);
    }
}
