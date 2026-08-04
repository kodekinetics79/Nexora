using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace ERP_RFQ_Automation.Tests;

public sealed class Module04ProductAvailabilityTests
{
    [Fact]
    public async Task Exact_match_returns_authoritative_atp_and_truthful_partial_incoming_shortage()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 1);
            seed.Products.Add(new Product
            {
                Id = 10, Buid = 1, PartNo = "PN-100", ProductName = "Control module",
                UnitCost = 12.5m, LeadTime = 14, IsActive = true,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            seed.Warehouses.Add(new Warehouse
            {
                Id = 20, BusinessUnitId = 1, WarehouseCode = "MAIN", WarehouseName = "Main",
                IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            seed.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
            {
                Id = 30, Buid = 1, ProductId = 10, WarehouseId = 20, PartNo = "PN-100",
                ProductName = "Control module", QtyOnHand = 20m, AllocatedQuantity = 2m,
                QuarantineQuantity = 1m, SafetyStockQuantity = 2m, ReorderPoint = 0m,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            seed.StockReservations.Add(new StockReservation
            {
                Id = 40, BusinessUnitId = 1, InventoryId = 30, Quantity = 5m,
                Status = StockReservationStatus.Active, IdempotencyKey = "existing-hold",
                CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            seed.IncomingInventory.Add(new IncomingInventory
            {
                Id = 50, BusinessUnitId = 1, ProductId = 10, WarehouseId = 20,
                OrderedQuantity = 3m, ExpectedOn = new DateOnly(2026, 8, 10),
                Status = IncomingInventoryStatus.Confirmed, SourceType = "PurchaseOrder", SourceId = "PO-50",
            });
            seed.Currencies.Add(new Currency
            {
                Id = 60, BusinessUnitId = 1, Code = "EUR", CurrencyName = "Euro",
                IsBaseCurrency = true, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(1);
        var repository = new ProductRepository(context, new TestEnvironment(), new ClearingFileInspection());
        var result = await repository.MatchProductAsync(new ProductMatchRequestDTO
        {
            BusinessUnitId = 1, PartNo = "PN-100", Quantity = 20m,
        });

        var match = Assert.IsType<ProductMatchSuggestionDTO>(result.ExactMatch);
        Assert.True(result.HasExactMatch);
        Assert.Equal(10m, match.AvailableToPromise);
        Assert.Equal(3m, match.IncomingAvailable);
        Assert.Equal(7m, match.ProjectedShortage);
        Assert.Equal("KnownShortage", match.AvailabilityStatus);
        Assert.Equal("EUR", match.CostCurrencyCode);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14), match.ExpectedAvailableOn);
        Assert.StartsWith("product:10:inventory-as-of:", match.EvidenceReference);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Module04Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
