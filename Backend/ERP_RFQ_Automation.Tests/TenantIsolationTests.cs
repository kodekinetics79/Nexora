using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The crown-jewel invariant (ADR-0005): EF Core global query filters transparently
/// scope every authenticated read to its business unit, while a null tenant context
/// (login / anonymous / background worker) applies no filter. These tests seed rows for
/// TWO business units and assert a scoped context can NEVER observe the other tenant's
/// data, that a null tenant sees everything, that IgnoreQueryFilters is the deliberate
/// opt-out, and that master data with a null Buid is shared to all tenants.
/// </summary>
public class TenantIsolationTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;

    // ---- Commercial documents (non-nullable BusinessUnitId) ----

    [Fact]
    public void ScopedContext_Leads_ReturnsOnlyOwnBusinessUnit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 101, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var leads = ctx.Leads.ToList();

        Assert.Equal(2, leads.Count);
        Assert.All(leads, l => Assert.Equal(Bu1, l.BusinessUnitId));
        Assert.DoesNotContain(leads, l => l.Id == 200);
    }

    [Fact]
    public void ScopedContext_CannotReadOtherTenantRowByPrimaryKey()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        // BU1 asks for BU2's lead by its exact id -> the filter hides it entirely.
        using var ctx = db.ContextFor(Bu1);
        var found = ctx.Leads.FirstOrDefault(l => l.Id == 200);

        Assert.Null(found);
    }

    [Fact]
    public void NullTenant_BackgroundWorker_SeesAllBusinessUnits()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            Seed.Lead(seed, leadId: 201, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var worker = db.ContextFor(null); // no tenant -> filter is a no-op
        var all = worker.Leads.ToList();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, l => l.BusinessUnitId == Bu1);
        Assert.Contains(all, l => l.BusinessUnitId == Bu2);
    }

    [Fact]
    public void IgnoreQueryFilters_IsTheDeliberateCrossTenantOptOut()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);

        Assert.Single(ctx.Leads);                                  // scoped
        Assert.Equal(2, ctx.Leads.IgnoreQueryFilters().Count());   // opt-out sees both
    }

    [Fact]
    public void ScopedCount_DoesNotLeakOtherTenantThroughAggregates()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            Seed.Lead(seed, leadId: 201, businessUnitId: Bu2);
            Seed.Lead(seed, leadId: 202, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        // A COUNT is pushed to SQL; the filter must be applied server-side, not post-hoc.
        using var bu1 = db.ContextFor(Bu1);
        using var bu2 = db.ContextFor(Bu2);
        Assert.Equal(1, bu1.Leads.Count());
        Assert.Equal(3, bu2.Leads.Count()); // sibling context sees only its own rows
    }

    // ---- Customer identity is tenant-owned even when legacy Buid is null ----

    [Fact]
    public void ScopedContext_Customers_SeesOnlyOwnTenant()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, id: 1, buid: Bu1, name: "BU1 Customer");
            Seed.Customer(seed, id: 2, buid: Bu2, name: "BU2 Customer");
            Seed.Customer(seed, id: 3, buid: null, name: "Shared Reference Customer");
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var visible = ctx.Customers.OrderBy(c => c.Id).ToList();

        Assert.Single(visible);
        Assert.Contains(visible, c => c.Id == 1);   // own tenant
        Assert.DoesNotContain(visible, c => c.Id == 3); // legacy unowned customer hidden
        Assert.DoesNotContain(visible, c => c.Id == 2); // other tenant hidden
    }

    [Fact]
    public void NullTenant_MasterData_SeesEveryRowIncludingBothTenants()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, id: 1, buid: Bu1, name: "BU1 Customer");
            Seed.Customer(seed, id: 2, buid: Bu2, name: "BU2 Customer");
            Seed.Customer(seed, id: 3, buid: null, name: "Shared");
            seed.SaveChanges();
        }

        using var worker = db.ContextFor(null);
        Assert.Equal(3, worker.Customers.Count());
    }
}
