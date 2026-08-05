using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Part numbers are unique PER TENANT, not globally. The scaffolded single-column unique
/// index ("UQ__Inventor__7C3FF6B67DFB4EBD" on Products.PartNo alone) meant one tenant's
/// part number aborted another tenant's entire catalogue import with an opaque constraint
/// error. Replaced by UQ_Products_BUID_PartNo on (BUID, PartNo) — the same remedy the
/// Suppliers table received (UX_Suppliers_BU_ContactEmail). The index filters on
/// "BUID" IS NOT NULL for any legacy null-BUID row, though EF itself already refuses to
/// track one (the (BUID, ID) alternate key requires a tenant), so no behavioural test
/// for that branch is possible — or needed.
/// </summary>
public sealed class ProductPartNumberTenantUniquenessTests
{
    private const long Bu1 = 9_710;
    private const long Bu2 = 9_720;

    private static Product Part(long id, long? buid, string partNo) => new()
    {
        Id = id, Buid = buid, PartNo = partNo, ProductName = $"Part {id}",
        CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true
    };

    [Fact]
    public async Task The_same_part_number_is_allowed_in_two_business_units()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, Bu1);
        Seed.EnsureBusinessUnit(db, Bu2);

        // Under the old global index this second insert — the pilot client cataloguing a
        // part some other tenant already stocks — is exactly what blew up the import.
        db.Products.AddRange(Part(1, Bu1, "MC-4X"), Part(2, Bu2, "MC-4X"));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Products.CountAsync(p => p.PartNo == "MC-4X"));
    }

    [Fact]
    public async Task The_same_part_number_is_rejected_twice_within_one_business_unit()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, Bu1);
        db.Products.Add(Part(1, Bu1, "MC-4X"));
        await db.SaveChangesAsync();

        db.Products.Add(Part(2, Bu1, "MC-4X"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void The_uniqueness_is_tenant_scoped_in_the_model_itself()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);

        // The design-time model is the one migrations (and the drift guard) are built from.
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Product))!;
        var indexes = entity.GetIndexes().ToArray();

        var tenantScoped = indexes.Single(x => x.GetDatabaseName() == "UQ_Products_BUID_PartNo");
        Assert.True(tenantScoped.IsUnique);
        Assert.Equal(new[] { nameof(Product.Buid), nameof(Product.PartNo) },
            tenantScoped.Properties.Select(p => p.Name));
        Assert.Equal("\"BUID\" IS NOT NULL", tenantScoped.GetFilter());

        // The defect must not quietly return: no unique index may span PartNo alone.
        Assert.DoesNotContain(indexes, x =>
            x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Product.PartNo) }));
    }
}
