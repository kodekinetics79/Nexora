using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the resolution service builds out of a saved lead row and hands the resolver. The resolver's
/// own tests state each rule on hand-built evidence; these prove the rule is reachable from the lead
/// the extraction worker actually saves, through the entry point it calls.
/// </summary>
public sealed class LeadCustomerResolutionServiceWiringTests
{
    private const long Tenant = 8700;
    private const long Aramco = 8711;
    private const long Hyundai = 8712;

    [Fact]
    public async Task A_contractors_mailbox_signed_with_a_customers_name_offers_that_customer_end_to_end()
    {
        // #14. Hyundai is a customer, nobody registered hdec.com and no contact writes from it. The
        // display name EmailService stores ("Hyundai E&C Procurement <procurement@hdec.com>") is the only
        // statement of who is writing, and it was read as a mention that named nobody: Aramco linked on
        // its site address at 0.88 and Hyundai was never offered.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            Seed.Customer(seed, Hyundai, Tenant, "Hyundai Engineering & Construction");
            var lead = Seed.Lead(seed, 8721, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "procurement@hdec.com";
            lead.DeliveryLocation = "Saudi Aramco Ras Tanura Refinery";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8721).FromEmail =
                "Hyundai E&C Procurement <procurement@hdec.com>";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, 8721);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(Hyundai, outcome.Candidates[0].CustomerId);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == Aramco && candidate.Confidence < 0.85m);
    }
}
