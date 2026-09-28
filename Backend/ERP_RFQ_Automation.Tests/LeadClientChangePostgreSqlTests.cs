using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Migration 20260929010000: nexora_validate_lead_commercial_identity() allows a resolved lead's
/// client to change until an RFQ references the lead, and refuses it after, exactly as before.
/// Pilot audit HT-02: an Aramco RFQ linked to SEC by one click could never be put right.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class LeadClientChangePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_lead_client_changes_before_an_RFQ_and_is_fixed_after_one()
    {
        var suffix = Random.Shared.Next(1, 40_000);
        var tenant = 9_610_000L + suffix;
        var sec = 9_620_000L + suffix;
        var aramco = 9_630_000L + suffix;
        var leadId = 9_640_000L + suffix;
        var rfqId = 9_650_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, sec, tenant, "Saudi Electricity Company");
            Seed.Customer(seed, aramco, tenant, "Saudi Aramco");
            var lead = Seed.Lead(seed, leadId, tenant);
            lead.Rfqno = "6000000028";
            lead.ResolveCommercialIdentity(sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            await seed.SaveChangesAsync();
        }

        // No RFQ yet: the wrong client can be corrected.
        await using (var context = database.ContextFor(tenant))
        {
            var lead = await context.Leads.SingleAsync(l => l.Id == leadId);
            lead.ResolveCommercialIdentity(aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            await context.SaveChangesAsync();
        }
        await using (var verify = database.ContextFor(tenant))
            Assert.Equal(aramco, (await verify.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId)).CustomerId);

        // With an RFQ naming the lead the client is fixed. The RFQ is inserted directly (its promotion
        // lineage check is lifted inside the transaction, which is rolled back): the rule under test
        // is the lead trigger's, not the promotion's.
        await using (var context = database.ContextFor(null))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.Database.ExecuteSqlRawAsync("""ALTER TABLE "RFQ" DROP CONSTRAINT IF EXISTS "CK_RFQ_LeadPromotionLineage" """);
            var lead = await context.Leads.SingleAsync(l => l.Id == leadId);
            var rfq = new Rfq
            {
                Id = rfqId, Rfqno = "RFQ-CLIENT-CHANGE-PG", RecDate = DateTime.UtcNow, LeadId = leadId,
                CustomerId = aramco, BusinessUnitId = tenant, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            };
            rfq.InheritCommercialIdentity(lead);
            context.Rfqs.Add(rfq);
            await context.SaveChangesAsync();

            lead.ResolveCommercialIdentity(sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            var refusal = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(refusal.InnerException);
            Assert.Equal("55000", postgres.SqlState);
            Assert.Contains("once the lead has an RFQ", postgres.MessageText);

            await transaction.RollbackAsync();
        }

        await using var after = database.ContextFor(tenant);
        Assert.Equal(aramco, (await after.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId)).CustomerId);
        Assert.False(await after.Rfqs.IgnoreQueryFilters().AnyAsync(r => r.Id == rfqId));
    }
}
