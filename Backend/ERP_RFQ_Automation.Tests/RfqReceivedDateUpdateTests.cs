using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// An update that does not state when the RFQ was received must not rewrite when it was.
///
/// <para><b>The defect.</b> <c>Rfq.RecDate</c> is NOT NULL and <c>RfqUpdateRequestDTO.RecDate</c>
/// is a non-nullable <c>DateTime</c>, so a payload without <c>recDate</c> binds to
/// <c>0001-01-01</c> and passes validation (<c>[Required]</c> cannot fire on a value type). The
/// create path guards the sentinel; <c>RfqRepository.UpdateAsync</c> copied it verbatim over the
/// real date. <c>CommercialLearningService</c> then computed quote turnaround as
/// <c>CreatedDate - RecDate</c> and kept it because ~17.7 million hours is positive.</para>
/// </summary>
public sealed class RfqReceivedDateUpdateTests
{
    private const long Tenant = 9_841;
    private const long RfqId = 98_411;
    private static readonly DateTime Received = new(2026, 8, 18, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_update_without_a_received_date_keeps_the_one_on_record()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedRfq(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // Exactly what the controller builds from a payload that omits recDate.
        await new RfqRepository(context).UpdateAsync(new Rfq
        {
            Id = RfqId, BusinessUnitId = Tenant, BuyersName = "Renamed buyer",
            RecDate = default, ModifiedBy = "rep@tenant.test"
        });

        context.ChangeTracker.Clear();
        var stored = await context.Rfqs.AsNoTracking().SingleAsync(x => x.Id == RfqId);
        Assert.Equal(Received, stored.RecDate);
        Assert.Equal("Renamed buyer", stored.BuyersName);
    }

    [Fact]
    public async Task An_update_that_states_a_received_date_applies_it()
    {
        // THE CONTROL: the guard ignores the sentinel, not the field.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedRfq(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var corrected = Received.AddDays(-2);

        await new RfqRepository(context).UpdateAsync(new Rfq
        {
            Id = RfqId, BusinessUnitId = Tenant, BuyersName = "Acme Buyer",
            RecDate = corrected, ModifiedBy = "rep@tenant.test"
        });

        context.ChangeTracker.Clear();
        Assert.Equal(corrected, (await context.Rfqs.AsNoTracking().SingleAsync(x => x.Id == RfqId)).RecDate);
    }

    private static void SeedRfq(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        context.Rfqs.Add(new Rfq
        {
            Id = RfqId, Rfqno = "RFQ-98411", BuyersName = "Acme Buyer", RecDate = Received,
            BusinessUnitId = Tenant, CreatedBy = "seed", CreatedDate = Received
        });
    }
}
