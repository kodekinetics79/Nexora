using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The startup reconciler against the production dialect, because the property it has to hold —
/// "run it twice on a fleet and nothing is duplicated" — is a property of what PostgreSQL
/// actually commits, under the advisory lock the reconciler takes there and nowhere else.
///
/// <para>The shape mirrors the live fleet on 2026-09-02: business unit 1 held every list, 7 and 8
/// held none of the five, and nothing ever re-checked. One unit here is bare; the other already
/// carries a ShipmentStatus list of its own making, which the reconciler must leave alone.</para>
///
/// <para>The run is SCOPED to the two units this class owns. The container is shared by every
/// class in the collection and other classes leave business units behind by design (a finalized
/// billing statement pins its tenant's unit forever), so an unscoped sweep would count and write
/// into whatever fixture ran before — which is exactly how this test passed alone and failed in
/// the full suite.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantReferenceListReconcilerPostgreSqlTests
{
    private const long BareUnit = 948_910_001;
    private const long ShapedUnit = 948_910_002;
    private const string CustomerActor = "admin@shaped.example";

    private readonly PostgreSqlTestDatabase _database;

    public TenantReferenceListReconcilerPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Running_the_reconciler_twice_fills_empty_lists_once_and_never_touches_a_shaped_one()
    {
        await CleanupAsync();
        try
        {
            await SeedAsync();

            var first = await RunAsync();
            var listCount = TenantBaselineCatalog.ReferenceLists.Sum(list => list.Entries.Count);
            var shipmentStatusCount = TenantBaselineCatalog.ReferenceLists
                .Single(list => list.SetupType == "ShipmentStatus").Entries.Count;
            Assert.Equal(2, first.BusinessUnitsCompleted);
            Assert.Equal(listCount + (listCount - shipmentStatusCount), first.RowsCreated);
            Assert.Equal(0, first.Failures);

            var second = await RunAsync();
            Assert.Equal(0, second.RowsCreated);
            Assert.Equal(0, second.BusinessUnitsCompleted);
            Assert.Equal(0, second.Failures);

            await using var verify = _database.ContextFor(null);
            var rows = await verify.SetupMasters.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.BusinessUnitId == BareUnit || row.BusinessUnitId == ShapedUnit)
                .ToListAsync();

            // No (unit, type, code) appears twice anywhere — the whole claim of "run it twice".
            Assert.Empty(rows.GroupBy(row => (row.BusinessUnitId, row.SetupType, row.SetupCode))
                .Where(group => group.Count() > 1)
                .Select(group => group.Key));

            // The bare unit now holds every list in full, stamped with the reconciler's actor so a
            // support engineer can tell these rows from provisioned or customer-added ones.
            foreach (var list in TenantBaselineCatalog.ReferenceLists)
            {
                var seeded = rows.Where(row => row.BusinessUnitId == BareUnit && row.SetupType == list.SetupType).ToList();
                Assert.Equal(list.Entries.Select(entry => entry.Code).Order(), seeded.Select(row => row.SetupCode).Order());
                Assert.All(seeded, row => Assert.Equal(TenantReferenceListReconciler.Actor, row.CreatedBy));
                Assert.All(seeded, row => Assert.True(row.IsActive));
            }

            // The shaped unit's own ShipmentStatus list is untouched: same two rows, same
            // spelling, one still inactive, nobody's "missing" codes added to it.
            var shaped = rows.Where(row => row.BusinessUnitId == ShapedUnit && row.SetupType == "ShipmentStatus")
                .OrderBy(row => row.SetupCode).ToList();
            Assert.Equal(["COLLECTED", "ON_HOLD"], shaped.Select(row => row.SetupCode));
            Assert.All(shaped, row => Assert.Equal(CustomerActor, row.CreatedBy));
            Assert.False(shaped.Single(row => row.SetupCode == "ON_HOLD").IsActive);

            // Lifecycle statuses were never the reconciler's business, and it wrote none.
            Assert.DoesNotContain(rows, row => row.SetupType == "OrderStatus" && row.CreatedBy == TenantReferenceListReconciler.Actor);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// The race the per-unit advisory lock exists for: a provisioning transaction that has ALREADY
    /// written a unit's lists but not yet committed, while the startup sweep reaches the same unit.
    /// The sweep must wait for the commit and then find nothing to do — never write a second copy.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_sweep_racing_an_uncommitted_provision_of_the_same_unit_waits_and_writes_nothing()
    {
        await CleanupAsync();
        try
        {
            await SeedAsync();

            // Provisioning: seed inside an open transaction and hold it.
            await using var provisioning = _database.ContextFor(null);
            await using var provisioningTransaction = await provisioning.Database.BeginTransactionAsync();
            var provisioner = new TenantBaselineSeeder(provisioning, NullLogger<TenantBaselineSeeder>.Instance);
            var provisioned = await provisioner.ReconcileReferenceListsAsync(BareUnit, "provisioning");
            Assert.Equal(TenantBaselineCatalog.ReferenceLists.Sum(list => list.Entries.Count), provisioned);

            // The sweep starts while that transaction is still open. It must block on the lock.
            var sweep = Task.Run(RunAsync);
            var finishedEarly = await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(sweep, finishedEarly);

            await provisioningTransaction.CommitAsync();
            var result = await sweep.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.Failures);
            await using var verify = _database.ContextFor(null);
            var rows = await verify.SetupMasters.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.BusinessUnitId == BareUnit).ToListAsync();
            Assert.Empty(rows.GroupBy(row => (row.SetupType, row.SetupCode)).Where(group => group.Count() > 1));
            Assert.All(rows.Where(row => TenantBaselineCatalog.ReferenceLists.Any(list => list.SetupType == row.SetupType)),
                row => Assert.Equal("provisioning", row.CreatedBy));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<TenantReferenceListReconciliation> RunAsync()
    {
        await using var db = _database.ContextFor(null);
        var seeder = new TenantBaselineSeeder(db, NullLogger<TenantBaselineSeeder>.Instance);
        return await TenantReferenceListReconciler.RunAsync(
            db, seeder, NullLogger.Instance, businessUnitIds: [BareUnit, ShapedUnit]);
    }

    private async Task SeedAsync()
    {
        await using var db = _database.ContextFor(null);
        var bare = new BusinessUnit
        {
            Id = BareUnit, BusinessUnitCode = "REF-BARE", BusinessUnitName = "Bare unit",
            IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        var shaped = new BusinessUnit
        {
            Id = ShapedUnit, BusinessUnitCode = "REF-SHAPED", BusinessUnitName = "Shaped unit",
            IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        db.BusinessUnits.AddRange(bare, shaped);
        // Both units were provisioned normally as far as lifecycle states go — that is the
        // production shape; it is only the reference lists that the catalogue grew after them.
        db.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(bare, "tests"));
        db.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(shaped, "tests"));
        db.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupType = "ShipmentStatus", SetupCode = "COLLECTED", SetupValue = "Collected by customer",
                BusinessUnit = shaped, IsActive = true, CreatedBy = CustomerActor, CreatedOn = DateTime.UtcNow
            },
            new SetupMaster
            {
                SetupType = "ShipmentStatus", SetupCode = "ON_HOLD", SetupValue = "On hold",
                BusinessUnit = shaped, IsActive = false, CreatedBy = CustomerActor, CreatedOn = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
    }

    private async Task CleanupAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        // Inserting a BusinessUnit fans out into per-unit policy rows by trigger, and those hold
        // the unit by foreign key; replica mode is how the sibling PostgreSQL suites clear their
        // own units too.
        command.CommandText = $"""
            SET session_replication_role = replica;
            DELETE FROM public."Setup_Master" WHERE "BusinessUnitID" IN ({BareUnit}, {ShapedUnit});
            DELETE FROM public."AiProcessingPolicies" WHERE "BusinessUnitId" IN ({BareUnit}, {ShapedUnit});
            DELETE FROM public."BusinessUnits" WHERE "ID" IN ({BareUnit}, {ShapedUnit});
            SET session_replication_role = origin;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
