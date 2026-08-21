using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The production failure itself, against real PostgreSQL: a product name longer than
/// <c>Products."ProductName"</c> aborts the INSERT with <c>22001</c>. SQLite cannot stand in here —
/// it does not enforce <c>varchar(n)</c> at all, so the in-memory lane inserts a 150-character name
/// happily and the defect is invisible.
///
/// <para>This is the second half of the proof. The DTO cap said 200; this says the column says 100;
/// between them sits <c>ProductController.Create</c>, which had no <c>try/catch</c>, so the
/// difference arrived at the caller as <c>{"error":"An unexpected error occurred."}</c> — twelve
/// times on 2026-08-20 between 19:36:36Z and 19:42:07Z.</para>
///
/// <para>The test asserts the column's width, NOT that the write must fail forever. It stays true
/// after the fix because the fix is at the DTO, which is where a caller can still be told what to
/// do about it.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ProductNameColumnWidthPostgreSqlTests
{
    private const long Bu = 9_781;
    private readonly PostgreSqlTestDatabase _database;

    public ProductNameColumnWidthPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    public async Task A_name_longer_than_the_column_aborts_the_insert_with_22001()
    {
        await using var db = _database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, Bu);
        await db.SaveChangesAsync();

        db.Products.Add(new Product
        {
            Buid = Bu,
            PartNo = $"WIDTH-{Guid.NewGuid():N}"[..20],
            // 150 characters: accepted by [StringLength(200)], refused by varchar(100).
            ProductName = new string('A', 150),
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(failure.InnerException);

        Assert.Equal("22001", postgres.SqlState);
        Assert.Contains("character varying(100)", postgres.MessageText);
    }

    /// <summary>The boundary, so the number in the DTO comment is the real one.</summary>
    [Fact]
    public async Task A_name_of_exactly_the_column_width_is_stored()
    {
        await using var db = _database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, Bu);
        await db.SaveChangesAsync();

        var partNo = $"FITS-{Guid.NewGuid():N}"[..20];
        db.Products.Add(new Product
        {
            Buid = Bu,
            PartNo = partNo,
            ProductName = new string('A', 100),
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Buid == Bu && p.PartNo == partNo);
        Assert.Equal(100, stored.ProductName!.Length);

        db.Products.Remove(await db.Products.SingleAsync(p => p.Buid == Bu && p.PartNo == partNo));
        await db.SaveChangesAsync();
    }
}
