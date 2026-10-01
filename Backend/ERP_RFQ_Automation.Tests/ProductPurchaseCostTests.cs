using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProductPurchaseCostTests
{
    private static readonly DateTime Anchor = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("SENT", true)]
    [InlineData("ISSUED", true)]
    [InlineData("ACKNOWLEDGED", true)]
    [InlineData("IN_PRODUCTION", true)]
    [InlineData("SHIPPED", true)]
    [InlineData("PARTIALLY_RECEIVED", true)]
    [InlineData("RECEIVED", true)]
    [InlineData("CLOSED", true)]
    [InlineData("DRAFT", false)]
    [InlineData("APPROVED", false)]
    [InlineData("CANCELLED", false)]
    public async Task List_uses_latest_dispatched_purchase_evidence_with_its_own_currency(string status, bool eligible)
    {
        using var fixture = new ProcurementScenario();
        await using (var seed = fixture.Context())
        {
            var product = await seed.Products.SingleAsync(p => p.Id == ProcurementTestData.Product);
            product.UnitCost = 30m;
            product.SellingPrice = 40m;
            product.FinalLandedCost = 999m;
            product.PriceCurrencyId = ProcurementTestData.Currency;
            seed.Currencies.Add(new Currency
            {
                Id = 98001, BusinessUnitId = ProcurementTestData.Tenant, Code = "EUR", CurrencyName = "Euro",
                IsActive = true, CreatedBy = "test", CreatedOn = Anchor
            });
            AddPurchase(seed, 98010, "RECEIVED", 11m, Anchor);
            AddPurchase(seed, 98020, status, 22m, Anchor.AddDays(1));
            AddPurchase(seed, 98030, "SENT", 88m, Anchor.AddDays(2), rejected: true);
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var (products, total) = await new ProductRepository(context, null!, null!).GetAllAsync(ProcurementTestData.Tenant);
        var row = Assert.Single(products);
        Assert.Equal(1, total);
        Assert.Equal(eligible ? 22m : 11m, row.LastPurchaseCost);
        Assert.Equal("EUR", row.LastPurchaseCurrencyCode);
        Assert.Equal(eligible ? Anchor.AddDays(1) : Anchor, row.LastPurchaseOn);
        Assert.Equal(30m, row.UnitCost);
        Assert.Equal(40m, row.SellingPrice);
        Assert.Equal("Q0", row.PriceCurrencyCode);
        var sheetRow = await ReadPricingRowAsync(context);
        Assert.Equal(row.LastPurchaseCost, sheetRow.LastPurchasePrice);
        Assert.Equal(row.LastPurchaseCurrencyCode, sheetRow.LastPurchaseCurrencyCode);
        Assert.Equal(row.LastPurchaseOn, sheetRow.LastPurchaseOn);

        await using var otherTenant = fixture.Context(ProcurementTestData.OtherTenant);
        var (otherProducts, _) = await new ProductRepository(otherTenant, null!, null!)
            .GetAllAsync(ProcurementTestData.OtherTenant);
        Assert.Null(Assert.Single(otherProducts).LastPurchaseCost);
    }

    [Fact]
    public async Task No_purchase_evidence_does_not_fall_back_to_legacy_or_master_cost()
    {
        using var fixture = new ProcurementScenario();
        await using var context = fixture.Context();
        var product = await context.Products.SingleAsync(p => p.Id == ProcurementTestData.Product);
        product.FinalLandedCost = 999m;
        product.UnitCost = 30m;
        await context.SaveChangesAsync();
        var (products, _) = await new ProductRepository(context, null!, null!).GetAllAsync(ProcurementTestData.Tenant);
        var row = Assert.Single(products);
        Assert.Null(row.LastPurchaseCost);
        Assert.Null(row.LastPurchaseCurrencyCode);
        Assert.Null(row.LastPurchaseOn);
        var sheetRow = await ReadPricingRowAsync(context);
        Assert.Null(sheetRow.LastPurchasePrice);
        Assert.Null(sheetRow.LastPurchaseCurrencyCode);
        Assert.Null(sheetRow.LastPurchaseOn);
    }

    private static async Task<PricingSheetController.PricingRow> ReadPricingRowAsync(ErpRfqAutomationContext context)
    {
        var controller = new PricingSheetController(context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("businessUnitId", ProcurementTestData.Tenant.ToString())], "test"))
                }
            }
        };
        var result = await controller.Get(null);
        var page = Assert.IsType<PricingSheetController.PricingPage>(Assert.IsType<OkObjectResult>(result.Result).Value);
        return Assert.Single(page.Rows);
    }

    private static void AddPurchase(ErpRfqAutomationContext context, long id, string status, decimal cost,
        DateTime sentOn, bool rejected = false)
    {
        AgentSeed.Award(context, id, ProcurementTestData.Tenant, ProcurementTestData.Rfq,
            ProcurementTestData.Supplier, unitPrice: cost, quantity: 1m);
        context.SupplierQuotedItems.Add(new SupplierQuotedItem
        {
            Id = id, BusinessUnitId = ProcurementTestData.Tenant, SupplierId = ProcurementTestData.Supplier,
            RfqId = ProcurementTestData.Rfq, RfqItemId = ProcurementTestData.RfqItem,
            ProductId = ProcurementTestData.Product, CurrencyId = 98001, Quantity = 1m,
            UnitPrice = cost, LandedUnitCost = cost + 5m, QuoteRevision = 1, IsActive = true,
            CreatedBy = "test", CreatedDate = Anchor, Version = 1
        });
        context.SupplierPurchaseOrders.Add(new SupplierPurchaseOrder
        {
            Id = id, BusinessUnitId = ProcurementTestData.Tenant, RfqId = ProcurementTestData.Rfq,
            SupplierId = ProcurementTestData.Supplier, CurrencyId = 98001, PurchaseOrderNumber = $"PO-{id}",
            Status = status, TotalValue = cost + 5m, SentToSupplierOn = sentOn,
            AcknowledgementStatus = rejected ? SupplierAcknowledgementStatuses.Rejected : null,
            IdempotencyKey = $"cost-{id}", RequestHash = new string('a', 64), CreatedBy = "test", CreatedOn = Anchor,
            Lines = [new SupplierPurchaseOrderLine
            {
                Id = id, BusinessUnitId = ProcurementTestData.Tenant, SourcingAwardId = id, SupplierQuotedItemId = id,
                RfqId = ProcurementTestData.Rfq, RfqItemId = ProcurementTestData.RfqItem,
                ProductId = ProcurementTestData.Product, WarehouseId = ProcurementTestData.Warehouse,
                OrderedQuantity = 1m, UnitCost = cost, LandedUnitCost = cost + 5m
            }]
        });
    }
}
