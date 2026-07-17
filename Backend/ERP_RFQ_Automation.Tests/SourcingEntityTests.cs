using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The NEW sourcing entities (SupplierSolicitation, SourcingAward) must obey the same
/// fail-closed tenant isolation as every commercial document (ADR-0005): a BU-scoped
/// context can never observe another tenant's solicitations or awards, while a null
/// tenant (background worker) sees everything. Also pins the solicitation lifecycle
/// (Sent -> Responded) persisting through the string-converted status column.
/// </summary>
public class SourcingEntityTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;

    // ---- 9. Tenant query filters ----

    [Fact]
    public void SupplierSolicitations_ScopedContext_SeesOnlyOwnBusinessUnit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Solicitation(seed, id: 1, businessUnitId: Bu1, rfqId: 10, supplierId: 100);
            AgentSeed.Solicitation(seed, id: 2, businessUnitId: Bu1, rfqId: 10, supplierId: 101);
            AgentSeed.Solicitation(seed, id: 3, businessUnitId: Bu2, rfqId: 20, supplierId: 200);
            seed.SaveChanges();
        }

        using var bu1 = db.ContextFor(Bu1);
        var visible = bu1.Set<SupplierSolicitation>().ToList();

        Assert.Equal(2, visible.Count);
        Assert.All(visible, s => Assert.Equal(Bu1, s.BusinessUnitId));

        // Even a point lookup by primary key cannot cross the tenant boundary.
        Assert.Null(bu1.Set<SupplierSolicitation>().FirstOrDefault(s => s.Id == 3));
    }

    [Fact]
    public void SupplierSolicitations_NullTenant_SeesAllBusinessUnits()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Solicitation(seed, id: 1, businessUnitId: Bu1, rfqId: 10, supplierId: 100);
            AgentSeed.Solicitation(seed, id: 3, businessUnitId: Bu2, rfqId: 20, supplierId: 200);
            seed.SaveChanges();
        }

        using var worker = db.ContextFor(null);
        var all = worker.Set<SupplierSolicitation>().ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.BusinessUnitId == Bu1);
        Assert.Contains(all, s => s.BusinessUnitId == Bu2);
    }

    [Fact]
    public void SourcingAwards_ScopedContext_SeesOnlyOwnBusinessUnit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Award(seed, id: 1, businessUnitId: Bu1, rfqId: 10, supplierId: 100, unitPrice: 25m, quantity: 4);
            AgentSeed.Award(seed, id: 2, businessUnitId: Bu2, rfqId: 20, supplierId: 200, unitPrice: 90m, quantity: 2);
            AgentSeed.Award(seed, id: 3, businessUnitId: Bu2, rfqId: 21, supplierId: 201, unitPrice: 15m);
            seed.SaveChanges();
        }

        using var bu1 = db.ContextFor(Bu1);
        var visible = bu1.Set<SourcingAward>().ToList();

        Assert.Single(visible);
        Assert.Equal(Bu1, visible[0].BusinessUnitId);
        Assert.Equal(100m, visible[0].TotalValue); // 25 * 4 persisted round-trip

        // Aggregates are filtered server-side too — no cross-tenant leakage via COUNT.
        Assert.Equal(1, bu1.Set<SourcingAward>().Count());
        Assert.Null(bu1.Set<SourcingAward>().FirstOrDefault(a => a.Id == 2));
    }

    [Fact]
    public void SourcingAwards_NullTenant_SeesAllBusinessUnits()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Award(seed, id: 1, businessUnitId: Bu1, rfqId: 10, supplierId: 100, unitPrice: 25m, quantity: 4);
            AgentSeed.Award(seed, id: 2, businessUnitId: Bu2, rfqId: 20, supplierId: 200, unitPrice: 90m, quantity: 2);
            seed.SaveChanges();
        }

        using var worker = db.ContextFor(null);
        Assert.Equal(2, worker.Set<SourcingAward>().Count());
    }

    // ---- 10. Solicitation lifecycle persists ----

    [Fact]
    public void Solicitation_SentToResponded_TransitionPersists()
    {
        var respondedAt = new DateTime(2026, 7, 17, 12, 30, 0, DateTimeKind.Utc);

        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Solicitation(seed, id: 1, businessUnitId: Bu1, rfqId: 10, supplierId: 100,
                status: SolicitationStatus.Sent);
            seed.SaveChanges();
        }

        // Transition through a BU-scoped context, the way tools do it.
        using (var ctx = db.ContextFor(Bu1))
        {
            var s = ctx.Set<SupplierSolicitation>().Single(x => x.Id == 1);
            Assert.Equal(SolicitationStatus.Sent, s.Status);
            Assert.Null(s.RespondedOn);

            s.Status = SolicitationStatus.Responded;
            s.RespondedOn = respondedAt;
            s.UpdatedOn = respondedAt;
            ctx.SaveChanges();
        }

        // Assert with a FRESH context so we read the stored row, not tracked state.
        using var verify = db.ContextFor(Bu1);
        var reloaded = verify.Set<SupplierSolicitation>().AsNoTracking().Single(x => x.Id == 1);

        Assert.Equal(SolicitationStatus.Responded, reloaded.Status); // string-converted enum round-trips
        Assert.Equal(respondedAt, reloaded.RespondedOn);
        Assert.Equal(respondedAt, reloaded.UpdatedOn);
        Assert.Equal(AgentSeed.Now, reloaded.SentOn); // original send timestamp untouched
    }
}
