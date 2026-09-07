using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Lets a test construct the one state the tenant plane is no longer allowed to be in: two Tenant
/// rows claiming the same primary business unit.
///
/// <para><b>That state is a cross-tenant data merge.</b> Row-level security isolates on
/// <c>PrimaryBusinessUnitId</c>, the purge executor resolves a tenant's data through it, and
/// tenant access checks resolve to it — and until 20260907111347 it carried no unique index, so
/// the database would accept two customers resolving to one scope. Several tests documented that
/// gap in passing ("nothing keeps a business unit to one Tenant row"). It is closed now.</para>
///
/// <para><b>Why those tests survive rather than being deleted.</b> Each one pins a real guarantee
/// about how the READ side behaves if the mapping is ever ambiguous: the leaderboard drops nobody,
/// the fleet total counts a document once, job attribution is deterministic, and the work gate
/// fails closed. Those are defence in depth, and defence in depth that nothing exercises rots. So
/// the index is lifted for the fixture, explicitly and with this explanation attached, rather than
/// the guarantees being quietly retired along with the defect.</para>
///
/// <para><b>Why lifting it is safe here and impossible in production.</b> Unlike the commercial
/// CHECK constraints, which ship <c>NOT VALID</c> and tolerate historical rows, a unique index is
/// validated the moment it is created: a database carrying duplicates fails the migration instead
/// of acquiring the index. <c>scripts/deploy/tenant-integrity-preflight.sql</c> check 1 exists to
/// find those rows first, and finding any is an incident rather than a migration blocker.</para>
/// </summary>
public static class SharedBusinessUnitFixture
{
    /// <summary>The index 20260907111347 adds. Named once so a rename cannot silently no-op these tests.</summary>
    public const string UniqueIndexName = "IX_Tenants_PrimaryBusinessUnitId";

    /// <summary>
    /// Drops the uniqueness guarantee on this test's own throwaway database, and materialises the
    /// business units the fixture is about to point at — because the foreign-key half of the same
    /// migration is NOT relaxed. A dangling isolation pointer is its own defect and no test needs one.
    /// </summary>
    public static async Task AllowAsync(ErpRfqAutomationContext context, params long[] businessUnitIds)
    {
        await context.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{UniqueIndexName}\";");

        foreach (var id in businessUnitIds)
        {
            if (await context.Set<BusinessUnit>().IgnoreQueryFilters().AnyAsync(x => x.Id == id)) continue;
            context.Set<BusinessUnit>().Add(new BusinessUnit
            {
                Id = id,
                BusinessUnitCode = $"BU{id}",
                BusinessUnitName = $"Unit {id}",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
    }
}
