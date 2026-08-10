using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-MDM-05 — "audit every change with before/after values" — and register item E44.
///
/// <para>Entering this change, <c>CustomerController</c>, <c>SupplierController</c> and
/// <c>ProductController</c> emitted no audit event of any kind. <c>Product.FinalLandedCost</c> —
/// the cost basis reported margin is computed from — was hand-editable on the product screen and
/// through column 28 of the import sheet, with no record of who changed it or why.</para>
///
/// <para>The tests that matter most here are the ones that never touch a controller. A capture
/// that only works when the request goes through <c>ProductController</c> is not a control: the
/// spreadsheet uploader writes products with no controller involved at all, and that is precisely
/// where an unaudited mass change would happen.</para>
/// </summary>
public sealed class MasterDataChangeAuditTests
{
    private const long Tenant = 88_400;
    private const long OtherTenant = 88_401;

    // ============================================================ the bypass proof

    /// <summary>
    /// THE test for this change. A product is created entirely through the spreadsheet uploader —
    /// no controller, no repository, no audit call anywhere in that service's own code — and the
    /// audit row exists anyway, with the imported landed cost captured as an after value.
    ///
    /// <para>The uploader is the write path a controller-level log would miss. This is the reason
    /// the capture lives in <c>ErpRfqAutomationContext.SaveChanges</c> rather than in a
    /// controller.</para>
    /// </summary>
    [Fact]
    public async Task A_product_imported_from_a_spreadsheet_is_audited_although_the_uploader_never_asks_for_it()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        byte[] workbook;
        await using (var context = database.ContextFor(Tenant))
            workbook = await Uploader(context).GenerateTemplateAsync(Tenant);

        await using (var context = database.ContextFor(Tenant))
        {
            using var stream = new MemoryStream(workbook);
            var result = await Uploader(context).UploadTemplateAsync(stream, Tenant, "importer@acme.invalid");
            Assert.True(result.Success);
        }

        await using var verify = database.ContextFor(Tenant);
        var laptop = await verify.Products.AsNoTracking().SingleAsync(x => x.PartNo == "CLP-001");

        var changeEvent = await verify.MasterDataChangeEvents.AsNoTracking()
            .Include(x => x.Fields)
            .SingleAsync(x => x.EntityType == MasterDataEntityTypes.Product && x.EntityId == laptop.Id);

        Assert.Equal(MasterDataChangeTypes.Created, changeEvent.ChangeType);
        Assert.Equal("CLP-001", changeEvent.EntityLabel);
        // The bulk path is separable from a screen edit: "who changed every landed cost last
        // Tuesday" is a different review question from "who edited this one product".
        Assert.Equal(MasterDataChangeSources.Import, changeEvent.ChangeSource);
        // No ambient HTTP actor in an import test, so attribution falls back to the row's own
        // server-set CreatedBy stamp rather than inventing a person.
        Assert.Equal("importer@acme.invalid", changeEvent.Actor);

        // The number the whole finding is about, captured with its value and its classification.
        var landedCost = Assert.Single(changeEvent.Fields.Where(f => f.FieldName == nameof(Product.FinalLandedCost)));
        Assert.Null(landedCost.BeforeValue);
        // Rendered at the COLUMN's scale — Product.FinalLandedCost is decimal(18,2) — so the trail
        // reads the same figure whichever engine stored it. "850" was the CLR literal's own scale,
        // which is not a property of the value and differs after a PostgreSQL round trip.
        Assert.Equal("850.00", landedCost.AfterValue);
        Assert.Equal(MasterDataSensitivity.Commercial, landedCost.Sensitivity);

        // A create records the values it was given, not a blob: every field is its own row.
        Assert.Equal(changeEvent.Fields.Count, changeEvent.FieldCount);
        Assert.Contains(changeEvent.Fields, f => f.FieldName == nameof(Product.PartNo) && f.AfterValue == "CLP-001");
    }

    /// <summary>
    /// The same proof for a bulk EDIT rather than a bulk create: a landed cost moved directly
    /// through the DbContext — the shape any service, worker or future importer would take — is
    /// captured with both values and nothing else is recorded as having changed.
    /// </summary>
    [Fact]
    public async Task Moving_a_landed_cost_records_that_one_field_with_both_values()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        var productId = await SeedProductAsync(database, Tenant, landedCost: 100.50m);

        await using (var context = database.ContextFor(Tenant))
        {
            var product = await context.Products.SingleAsync(x => x.Id == productId);
            product.FinalLandedCost = 175.25m;
            product.ModifiedBy = "buyer@acme.invalid";
            product.ModifiedOn = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        var changeEvent = await verify.MasterDataChangeEvents.AsNoTracking().Include(x => x.Fields)
            .SingleAsync(x => x.EntityId == productId && x.ChangeType == MasterDataChangeTypes.Updated);

        // ONE field row. ModifiedBy/ModifiedOn moved too, but attribution is not a change — the
        // audit row already carries stronger attribution than those columns do, and recording them
        // would add two noise rows to every single edit and bury the one that mattered.
        var field = Assert.Single(changeEvent.Fields);
        Assert.Equal(nameof(Product.FinalLandedCost), field.FieldName);
        Assert.Equal("100.50", field.BeforeValue);
        Assert.Equal("175.25", field.AfterValue);
        Assert.Equal("buyer@acme.invalid", changeEvent.Actor);
    }

    /// <summary>
    /// EF marks a property modified when it is ASSIGNED, not when its value differs, and
    /// <c>ProductController.Update</c> re-assigns all twenty-four fields from the request body on
    /// every save. Without the value comparison, editing one field would report twenty-four
    /// changes and the trail would be unreadable exactly when it is needed.
    /// </summary>
    [Fact]
    public async Task Re_assigning_every_field_records_only_the_one_that_actually_moved()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        var productId = await SeedProductAsync(database, Tenant, landedCost: 100m);

        await using (var context = database.ContextFor(Tenant))
        {
            var product = await context.Products.SingleAsync(x => x.Id == productId);
            // Exactly what the controller does: assign everything from the request.
            product.ProductName = product.ProductName;
            product.PartNo = product.PartNo;
            product.Description = product.Description;
            product.UnitCost = product.UnitCost;
            product.SellingPrice = product.SellingPrice;
            product.CountryOfOrigin = product.CountryOfOrigin;
            product.FinalLandedCost = 111m;
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        var changeEvent = await verify.MasterDataChangeEvents.AsNoTracking().Include(x => x.Fields)
            .SingleAsync(x => x.EntityId == productId && x.ChangeType == MasterDataChangeTypes.Updated);
        Assert.Equal(nameof(Product.FinalLandedCost), Assert.Single(changeEvent.Fields).FieldName);
    }

    /// <summary>A save that touches nothing an auditor would call a change writes no header at
    /// all. A trail full of rows that say nothing happened is a trail nobody reads.</summary>
    [Fact]
    public async Task A_save_that_moves_only_the_modified_stamp_writes_no_audit_row()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        var productId = await SeedProductAsync(database, Tenant, landedCost: 100m);

        await using (var context = database.ContextFor(Tenant))
        {
            var product = await context.Products.SingleAsync(x => x.Id == productId);
            product.ModifiedBy = "someone@acme.invalid";
            product.ModifiedOn = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        Assert.Empty(await verify.MasterDataChangeEvents.AsNoTracking()
            .Where(x => x.EntityId == productId && x.ChangeType == MasterDataChangeTypes.Updated).ToListAsync());
    }

    // ============================================================ delete

    /// <summary>
    /// A delete records what was there before it. The audit row carries no foreign key to the
    /// product, deliberately: the record of who deleted it has to outlive the row it names.
    /// </summary>
    [Fact]
    public async Task Deleting_a_product_records_its_previous_values_and_survives_the_deletion()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        var productId = await SeedProductAsync(database, Tenant, landedCost: 640m);

        await using (var context = database.ContextFor(Tenant))
        {
            context.Products.Remove(await context.Products.SingleAsync(x => x.Id == productId));
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        Assert.Empty(await verify.Products.AsNoTracking().Where(x => x.Id == productId).ToListAsync());

        var changeEvent = await verify.MasterDataChangeEvents.AsNoTracking().Include(x => x.Fields)
            .SingleAsync(x => x.EntityId == productId && x.ChangeType == MasterDataChangeTypes.Deleted);
        Assert.Equal("MDM-PART-1", changeEvent.EntityLabel);
        var landedCost = Assert.Single(changeEvent.Fields.Where(f => f.FieldName == nameof(Product.FinalLandedCost)));
        Assert.Equal("640.00", landedCost.BeforeValue); // decimal(18,2), rendered at column scale
        Assert.Null(landedCost.AfterValue);
    }

    // ============================================================ customer and supplier

    [Fact]
    public async Task A_customer_email_change_is_audited_and_classified_as_personal_data()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        long customerId;

        await using (var context = database.ContextFor(Tenant))
        {
            var customer = NewCustomer(Tenant, "Riyadh Metals", "buyer@riyadh.invalid");
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
            customerId = customer.Id;
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var customer = await context.Customers.SingleAsync(x => x.Id == customerId);
            customer.ContactEmail = "procurement@riyadh.invalid";
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        var events = await verify.MasterDataChangeEvents.AsNoTracking().Include(x => x.Fields)
            .Where(x => x.EntityType == MasterDataEntityTypes.Customer && x.EntityId == customerId)
            .OrderBy(x => x.Id).ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(MasterDataChangeTypes.Created, events[0].ChangeType);

        var emailChange = Assert.Single(events[1].Fields);
        Assert.Equal(nameof(Customer.ContactEmail), emailChange.FieldName);
        Assert.Equal("buyer@riyadh.invalid", emailChange.BeforeValue);
        Assert.Equal("procurement@riyadh.invalid", emailChange.AfterValue);
        // Recorded in full, and flagged. Redacting the previous value would leave a trail that
        // cannot answer the question it exists for; the flag is what lets a future PDPL erasure
        // find the personal-data rows without deleting the proof that the change happened.
        Assert.Equal(MasterDataSensitivity.Personal, emailChange.Sensitivity);
    }

    [Fact]
    public async Task Supplier_payment_terms_are_audited_and_classified_as_commercial()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        long supplierId;

        await using (var context = database.ContextFor(Tenant))
        {
            var supplier = NewSupplier(Tenant, "Gulf Cables", "NET 30");
            context.Suppliers.Add(supplier);
            await context.SaveChangesAsync();
            supplierId = supplier.Id;
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var supplier = await context.Suppliers.SingleAsync(x => x.Id == supplierId);
            supplier.PaymentTerms = "NET 90";
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        var update = await verify.MasterDataChangeEvents.AsNoTracking().Include(x => x.Fields)
            .SingleAsync(x => x.EntityType == MasterDataEntityTypes.Supplier
                              && x.EntityId == supplierId
                              && x.ChangeType == MasterDataChangeTypes.Updated);

        var terms = Assert.Single(update.Fields);
        Assert.Equal(nameof(Supplier.PaymentTerms), terms.FieldName);
        Assert.Equal("NET 30", terms.BeforeValue);
        Assert.Equal("NET 90", terms.AfterValue);
        Assert.Equal(MasterDataSensitivity.Commercial, terms.Sensitivity);
    }

    // ============================================================ append-only

    /// <summary>
    /// The database trigger <c>trg_master_data_audit_append_only</c> is the real control and it
    /// cannot be exercised on SQLite. This is the portable half, so the same mistake fails here
    /// instead of passing the portable lane and shipping.
    /// </summary>
    [Fact]
    public async Task The_trail_cannot_be_rewritten_or_erased_through_the_application()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        var productId = await SeedProductAsync(database, Tenant, landedCost: 12m);

        await using var context = database.ContextFor(Tenant);
        var changeEvent = await context.MasterDataChangeEvents.SingleAsync(x => x.EntityId == productId);

        changeEvent.Actor = "somebody-else";
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());

        context.ChangeTracker.Clear();
        context.MasterDataChangeEvents.Remove(
            await context.MasterDataChangeEvents.SingleAsync(x => x.EntityId == productId));
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    // ============================================================ tenant isolation

    [Fact]
    public async Task Another_tenant_cannot_see_or_read_back_the_trail()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        await SeedTenantAsync(database, OtherTenant);
        var productId = await SeedProductAsync(database, Tenant, landedCost: 500m);

        await using (var intruder = database.ContextFor(OtherTenant))
        {
            Assert.Empty(await intruder.MasterDataChangeEvents.AsNoTracking().ToListAsync());
            Assert.Empty(await intruder.MasterDataFieldChanges.AsNoTracking().ToListAsync());
            // Not even by exact primary key.
            Assert.Empty(await new MasterDataChangeHistoryReader(intruder)
                .ReadAsync(MasterDataEntityTypes.Product, productId, OtherTenant, 50));
        }

        await using var owner = database.ContextFor(Tenant);
        var history = await new MasterDataChangeHistoryReader(owner)
            .ReadAsync(MasterDataEntityTypes.Product, productId, Tenant, 50);
        Assert.Equal(MasterDataChangeTypes.Created, Assert.Single(history).ChangeType);
    }

    // ============================================================ actor attribution

    /// <summary>The ambient actor pushed from a validated token wins over the row's own stamp, and
    /// it carries the user id, the role held at the time and the request correlation id — none of
    /// which the CreatedBy string can supply.</summary>
    [Fact]
    public async Task An_authenticated_actor_is_recorded_with_its_user_role_and_correlation_id()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        using (MasterDataAuditScope.Push(new MasterDataAuditActor(
                   UserId: 4_242, RoleId: 7, Name: "user:4242 (admin@acme.invalid)",
                   CorrelationId: "trace-abc", Source: MasterDataChangeSources.Api)))
        {
            await SeedProductAsync(database, Tenant, landedCost: 9m);
        }

        await using var verify = database.ContextFor(Tenant);
        var changeEvent = await verify.MasterDataChangeEvents.AsNoTracking().SingleAsync();
        Assert.Equal(4_242, changeEvent.ActorUserId);
        Assert.Equal(7, changeEvent.ActorRoleId);
        Assert.Equal("trace-abc", changeEvent.CorrelationId);
        Assert.Equal(MasterDataChangeSources.Api, changeEvent.ChangeSource);
    }

    // ============================================================ scope and cost

    /// <summary>
    /// The interceptor runs on every save in the application, so it must do nothing for the saves
    /// that are not master data. A quote-status change, a lead, an order — none of them may cost a
    /// row or a query.
    /// </summary>
    [Fact]
    public async Task A_save_that_touches_no_master_data_writes_nothing()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        await using (var context = database.ContextFor(Tenant))
        {
            context.Warehouses.Add(new Warehouse
            {
                WarehouseName = "Jeddah Main",
                WarehouseCode = "JED",
                BusinessUnitId = Tenant,
                CreatedBy = "seed",
                CreatedOn = DateTime.UtcNow,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(Tenant);
        Assert.Empty(await verify.MasterDataChangeEvents.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The capture runs on EVERY save in the application, so its cost on a save that has nothing
    /// to do with master data has to be negligible, and "negligible" has to be a measured number
    /// rather than an assurance.
    ///
    /// <para>The bound is deliberately loose — an order of magnitude above what the measurement
    /// actually shows — because a tight timing assertion on a shared CI box is a test that fails
    /// for reasons unrelated to the code. It is here to catch a regression that adds a query or an
    /// allocation per entry, not to police microseconds. The measured figure is printed so the
    /// number in the change report is a real one.</para>
    /// </summary>
    [Fact]
    public async Task The_capture_costs_a_single_pass_over_the_change_tracker()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        await using var context = database.ContextFor(Tenant);
        // A deliberately fat change tracker of entities the capture must ignore.
        for (var i = 0; i < 500; i++)
            context.Warehouses.Add(new Warehouse
            {
                WarehouseName = $"WH-{i}",
                WarehouseCode = $"W{i}",
                BusinessUnitId = Tenant,
                CreatedBy = "seed",
                CreatedOn = DateTime.UtcNow,
                IsActive = true
            });
        context.ChangeTracker.DetectChanges();

        // Warm up the type-switch and the JIT before timing.
        for (var i = 0; i < 20; i++) MasterDataAuditInterceptor.Capture(context);

        const int iterations = 200;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) MasterDataAuditInterceptor.Capture(context);
        stopwatch.Stop();

        var microsecondsPerSave = stopwatch.Elapsed.TotalMilliseconds * 1000 / iterations;
        Assert.True(microsecondsPerSave < 2_000,
            $"Capture cost {microsecondsPerSave:F1}µs over 500 tracked non-master-data entries. "
            + "That is far above a single in-memory pass and suggests a query or an allocation per entry.");

        // Nothing was enlisted: the fast path must not touch the tracker it walked.
        Assert.Equal(500, context.ChangeTracker.Entries().Count());
    }

    // ============================================================ helpers

    private static ProductUploaderService Uploader(ErpRfqAutomationContext context)
        => new(context, NullLogger<ProductUploaderService>.Instance);

    private static async Task SeedTenantAsync(TestDb database, long tenant)
    {
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, tenant);
        await context.SaveChangesAsync();
    }

    private static async Task<long> SeedProductAsync(TestDb database, long tenant, decimal landedCost)
    {
        await using var context = database.ContextFor(tenant);
        var product = new Product
        {
            PartNo = "MDM-PART-1",
            ProductName = "Armoured cable 4c 16mm",
            Description = "Reference item",
            UnitCost = 400m,
            SellingPrice = 700m,
            FinalLandedCost = landedCost,
            CountryOfOrigin = "SA",
            Buid = tenant,
            IsActive = true,
            CreatedBy = "seed@acme.invalid",
            CreatedOn = DateTime.UtcNow
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product.Id;
    }

    private static Customer NewCustomer(long tenant, string name, string email) => new()
    {
        Name = name,
        ContactEmail = email,
        ImageUrl = string.Empty,
        Buid = tenant,
        IsActive = true,
        CreatedBy = "seed@acme.invalid",
        CreatedOn = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid()
    };

    private static Supplier NewSupplier(long tenant, string name, string paymentTerms) => new()
    {
        Name = name,
        ImageUrl = string.Empty,
        PaymentTerms = paymentTerms,
        Buid = tenant,
        IsActive = true,
        CreatedBy = "seed@acme.invalid",
        CreatedOn = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid()
    };
}
