using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Purchase-history writes move real stock, so they must be tenant-bound, atomic, and
/// numbered by the database rather than by application memory.
///
/// The previous implementation issued PO document numbers by reading MAX(PoDocId),
/// parsing it and incrementing it in process, with no transaction, no row lock and no
/// sequence — two concurrent callers read the same maximum and issued the same PO number.
/// It also accepted whatever ProductId and SupplierId the caller supplied, so a batch
/// could silently move another tenant's stock.
/// </summary>
public sealed class SupplierPurchaseHistoryRepositoryTests
{
    private const long Tenant = 97_101;
    private const long OtherTenant = 97_102;
    private const long Warehouse = 97_110;
    private const long Product = 97_120;
    private const long OtherProduct = 97_121;
    private const long Supplier = 97_130;
    private const long OtherSupplier = 97_131;
    private const decimal InitialOnHand = 5m;

    private static readonly DateTime Now = new(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Each_batch_is_issued_its_own_server_generated_po_document_number()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);

        var first = await repository.AddBatchAsync([Row(quantity: 1m)]);
        var second = await repository.AddBatchAsync([Row(quantity: 1m)]);
        var third = await repository.AddBatchAsync([Row(quantity: 1m)]);

        Assert.Equal("PO00000001", first);
        Assert.Equal("PO00000002", second);
        Assert.Equal("PO00000003", third);

        await using var verify = database.ContextFor(null);
        var issued = await verify.SupplierPurchaseHistories.Select(x => x.PoDocId).ToListAsync();
        Assert.Equal(3, issued.Count);
        Assert.Equal(issued.Count, issued.Distinct().Count());
    }

    [Fact]
    public async Task All_rows_in_one_batch_share_a_single_po_document_number()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var poDocId = await new SupplierPurchaseHistoryRepository(context)
            .AddBatchAsync([Row(quantity: 2m), Row(quantity: 3m)]);

        await using var verify = database.ContextFor(null);
        var rows = await verify.SupplierPurchaseHistories.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(poDocId, row.PoDocId));
    }

    /// <summary>
    /// Purchase history is a commercial record, not a stock event. It used to do
    /// <c>product.QtyOnHand += …</c>, which is a second, independent stock ledger: no
    /// InventoryMovement, no warehouse, and invisible to available-to-promise. Stock arrives only
    /// through the governed goods-receipt path or the stock ledger; the landed cost is still
    /// recorded here because that is genuinely what a purchase history knows.
    /// </summary>
    [Fact]
    public async Task Recording_purchase_history_moves_no_stock_and_still_records_the_landed_cost()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        await new SupplierPurchaseHistoryRepository(context)
            .AddBatchAsync([Row(quantity: 2m, unitPrice: 10m), Row(quantity: 3m, unitPrice: 12m)]);

        await using var verify = database.ContextFor(null);
        var product = await verify.Products.SingleAsync(x => x.Id == Product);
        Assert.Equal(InitialOnHand, product.QtyOnHand);
        Assert.Equal(12m, product.FinalLandedCost);
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
        Assert.Equal(2, await verify.SupplierPurchaseHistories.CountAsync());
    }

    /// <summary>
    /// The source guard: neither the legacy stock increment nor its delete-time reversal may come
    /// back. Both were writers of a second stock ledger that no availability screen could see.
    /// </summary>
    [Fact]
    public void Purchase_history_never_writes_the_legacy_product_stock_column()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/Repositories/SupplierPurchaseHistoryRepository.cs"));

        Assert.DoesNotContain("product.QtyOnHand +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("product.QtyOnHand -=", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_spanning_business_units_is_rejected_and_moves_no_stock()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddBatchAsync(
        [
            Row(quantity: 4m),
            Row(quantity: 4m, productId: OtherProduct, supplierId: OtherSupplier)
        ]));

        await AssertNothingMovedAsync(database);
    }

    [Fact]
    public async Task A_supplier_outside_the_products_business_unit_is_rejected_and_moves_no_stock()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AddBatchAsync([Row(quantity: 4m, supplierId: OtherSupplier)]));

        await AssertNothingMovedAsync(database);
    }

    [Fact]
    public async Task A_row_referencing_an_unknown_product_is_rejected_and_moves_no_stock()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AddBatchAsync([Row(quantity: 4m, productId: 8_888_888)]));

        await AssertNothingMovedAsync(database);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_quantity_is_rejected_and_moves_no_stock(int quantity)
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.AddBatchAsync([Row(quantity: quantity)]));

        await AssertNothingMovedAsync(database);
    }

    [Fact]
    public async Task Deleting_a_purchase_order_removes_its_rows_and_still_moves_no_stock()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);
        var poDocId = await repository.AddBatchAsync([Row(quantity: 2m), Row(quantity: 3m)]);

        await repository.DeleteByPoDocIdAsync(poDocId, Tenant);

        await using var verify = database.ContextFor(null);
        Assert.Empty(await verify.SupplierPurchaseHistories.ToListAsync());
        Assert.Equal(InitialOnHand, await verify.Products.Where(x => x.Id == Product)
            .Select(x => x.QtyOnHand).SingleAsync());
        Assert.Empty(await verify.InventoryMovements.ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_purchase_order_from_another_tenant_changes_nothing()
    {
        using var database = NewDatabase();

        await using var context = database.ContextFor(null);
        var repository = new SupplierPurchaseHistoryRepository(context);
        var poDocId = await repository.AddBatchAsync([Row(quantity: 2m)]);

        await repository.DeleteByPoDocIdAsync(poDocId, OtherTenant);

        await using var verify = database.ContextFor(null);
        Assert.Single(await verify.SupplierPurchaseHistories.ToListAsync());
        Assert.Equal(InitialOnHand, await verify.Products.Where(x => x.Id == Product)
            .Select(x => x.QtyOnHand).SingleAsync());
    }

    /// <summary>
    /// Mirrors <c>RfqServerAuthorityTests.Rfq_number_sequence_is_schema_qualified</c>: the
    /// PostgreSQL lane must take its PO document numbers from a schema-qualified sequence, and
    /// the in-memory MAX-and-increment generator that produced duplicates must stay deleted.
    /// </summary>
    [Fact]
    public void Po_document_numbers_come_from_a_schema_qualified_database_sequence()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/Repositories/SupplierPurchaseHistoryRepository.cs"));

        Assert.Contains("nextval('public.nexora_supplier_po_doc_seq')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nextval('nexora_supplier_po_doc_seq')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderByDescending(h => h.PoDocId)", source, StringComparison.Ordinal);
    }

    /// <summary>The migration must seed the sequence past every PO number already issued.</summary>
    [Fact]
    public void Po_document_sequence_migration_reconciles_the_persisted_high_water_mark()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/Migrations/" +
            "20260804180919_Module05ProcurementDeadlineAndPoNumberAuthority.cs"));

        Assert.Contains("CREATE SEQUENCE IF NOT EXISTS public.nexora_supplier_po_doc_seq",
            source, StringComparison.Ordinal);
        Assert.Contains("setval('public.nexora_supplier_po_doc_seq'", source, StringComparison.Ordinal);
        Assert.Contains("GRANT USAGE ON SEQUENCE public.nexora_supplier_po_doc_seq",
            source, StringComparison.Ordinal);
    }

    private static async Task AssertNothingMovedAsync(TestDb database)
    {
        await using var verify = database.ContextFor(null);
        Assert.Empty(await verify.SupplierPurchaseHistories.ToListAsync());
        var quantities = await verify.Products.OrderBy(x => x.Id)
            .Select(x => x.QtyOnHand).ToListAsync();
        Assert.All(quantities, quantity => Assert.Equal(InitialOnHand, quantity));
    }

    private static SupplierPurchaseHistory Row(
        decimal quantity,
        decimal unitPrice = 10m,
        long productId = Product,
        long supplierId = Supplier)
        => new()
        {
            ProductId = productId,
            SupplierId = supplierId,
            PurchaseDate = Now,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Currency = "USD",
            CreatedBy = "qa",
            CreatedOn = Now
        };

    private static TestDb NewDatabase()
    {
        var database = new TestDb();
        using var seed = database.ContextFor(null);
        SeedTenant(seed, Tenant, Warehouse, Product, Supplier, "tenant");
        SeedTenant(seed, OtherTenant, Warehouse + 1, OtherProduct, OtherSupplier, "other");
        seed.SaveChanges();
        return database;
    }

    private static void SeedTenant(ErpRfqAutomationContext context, long tenant, long warehouseId,
        long productId, long supplierId, string label)
    {
        Seed.EnsureBusinessUnit(context, tenant);
        context.Warehouses.Add(new Warehouse
        {
            Id = warehouseId,
            BusinessUnitId = tenant,
            WarehouseCode = $"WH-{label}",
            WarehouseName = $"Warehouse {label}",
            IsActive = true,
            CreatedBy = "qa",
            CreatedOn = Now
        });
        context.Products.Add(new Product
        {
            Id = productId,
            Buid = tenant,
            PartNo = $"PART-{label}",
            ProductName = $"Product {label}",
            WarehouseId = warehouseId,
            QtyOnHand = InitialOnHand,
            ReorderPoint = 0,
            IsActive = true,
            CreatedBy = "qa",
            CreatedOn = Now
        });
        AgentSeed.Supplier(context, supplierId, tenant, $"Supplier {label}", $"{label}@example.test");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located from the test output directory.");
    }
}
