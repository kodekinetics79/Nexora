using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

/// <summary>
/// RouteLeadAsync proved customer matches and persisted them onto
/// LeadRoutingDecision.CustomerId — but never onto Lead.CustomerId, which is why the
/// matching engine "worked" while zero production leads linked to a Customer. The
/// write-through is deliberately narrower than routing itself: only an unambiguous engine
/// match proven by an exact-identifier-grade signal (ErpAccount / TaxRegistration / Email /
/// Domain — all verified rows by engine precondition) links the lead. Name similarity
/// routes but never links, and an existing link is never overwritten — a wrong client on a
/// lead is worse than an unresolved one.
/// </summary>
public sealed class RoutingCustomerWriteThroughTests
{
    [Fact]
    public async Task Exact_email_match_writes_the_customer_through_to_the_lead()
    {
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        var service = Service(context);

        var result = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-writethrough-701", "corr-writethrough-701"),
            CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        var lead = await context.Leads.SingleAsync(l => l.Id == 701);
        Assert.Equal(7201, lead.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, lead.CustomerMatchStatus);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
        Assert.Equal(0.99m, lead.CustomerMatchConfidence);
        Assert.Contains("buyer@acme.example", lead.CustomerMatchExplanation);
        // The decision row carries the same link — the two records can no longer disagree.
        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        Assert.Equal(lead.CustomerId, decision.CustomerId);
    }

    [Fact]
    public async Task Matched_customer_without_an_effective_owner_still_links_the_lead()
    {
        // The customer identity is proven even when no owner can be assigned: the lead
        // goes to the unassigned queue AND carries its customer.
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: false);
        await using var context = db.ContextFor(71);
        var service = Service(context);

        var result = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-writethrough-noowner", "corr-writethrough-noowner"),
            CancellationToken.None);

        Assert.Equal(RoutingOutcome.Unassigned, result.Outcome);
        Assert.Equal("NO_EFFECTIVE_OWNERSHIP", result.DecisionCode);
        Assert.NotNull(result.WorkItemId);
        var lead = await context.Leads.SingleAsync(l => l.Id == 701);
        Assert.Equal(7201, lead.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, lead.CustomerMatchStatus);
    }

    [Fact]
    public async Task Name_similarity_routes_the_lead_but_never_links_it()
    {
        // A verified CustomerName identifier above threshold produces a Matched routing
        // decision and a real assignment — but name similarity is not identifier-grade
        // evidence of WHO the client is, so Lead.CustomerId must stay null.
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: false, includeOwnership: true);
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                Id = 7361,
                BusinessUnitId = 71,
                CustomerId = 7201,
                IdentifierType = CustomerIdentifierType.CustomerName,
                NormalizedValue = RoutingValueNormalizer.Normalize(
                    CustomerIdentifierType.CustomerName, "Acme Trading"),
                DisplayValue = "Acme Trading",
                IsVerified = true,
                Confidence = 0.95m,
                Source = "test",
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        var service = Service(context);

        var result = await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-writethrough-name", "corr-writethrough-name"),
            CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(RoutingOutcome.AssignedPrimary, result.Outcome);
        var lead = await context.Leads.SingleAsync(l => l.Id == 701);
        Assert.Null(lead.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, lead.CustomerMatchStatus);
    }

    [Fact]
    public async Task An_existing_customer_link_is_never_overwritten()
    {
        // A reviewer confirmed customer 7202; routing's email evidence points at 7201.
        // Routing may still route, but the human's link must survive untouched.
        using var db = new TestDb();
        await SeedRoutingGraphAsync(db, includeIdentifier: true, includeOwnership: true);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, 7202, 71, "Confirmed Client");
            var lead = await seed.Leads.SingleAsync(l => l.Id == 701);
            lead.ResolveCommercialIdentity(7202, null, LeadCustomerMatchStatuses.Confirmed);
            await seed.SaveChangesAsync();
        }
        await using var context = db.ContextFor(71);
        await AddEligibleProfileAsync(context, 7101);
        var service = Service(context);

        await service.RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-writethrough-existing", "corr-writethrough-existing"),
            CancellationToken.None);

        var routed = await context.Leads.SingleAsync(l => l.Id == 701);
        Assert.Equal(7202, routed.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Confirmed, routed.CustomerMatchStatus);
    }

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    private static async Task AddEligibleProfileAsync(ErpRfqAutomationContext context, long userId)
    {
        var now = DateTime.UtcNow;
        context.SalesRepProfiles.Add(new SalesRepProfile
        {
            BusinessUnitId = 71, UserId = userId, IsRoutingEligible = true,
            CapacityPercent = 100, DistributionWeight = 1, EffectiveFromUtc = now.AddDays(-1),
            Version = 1, UpdatedAtUtc = now, UpdatedBy = "test",
            LastMutationIdempotencyKey = $"writethrough-profile-{userId}"
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedRoutingGraphAsync(TestDb db, bool includeIdentifier, bool includeOwnership)
    {
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 701, 71, buyersName: "Acme Trading");
        lead.Clientemail = "buyer@acme.example";
        Seed.Customer(context, 7201, 71, "Acme Trading");
        context.Users.AddRange(User(7101, 71, "owner"), User(7102, 71, "manager"));
        await context.SaveChangesAsync();

        if (includeIdentifier)
        {
            context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                Id = 7301,
                BusinessUnitId = 71,
                CustomerId = 7201,
                IdentifierType = CustomerIdentifierType.Email,
                NormalizedValue = "buyer@acme.example",
                DisplayValue = "buyer@acme.example",
                IsVerified = true,
                Confidence = 0.99m,
                Source = "test",
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
        }
        if (includeOwnership)
        {
            context.Set<CustomerOwnership>().Add(new CustomerOwnership
            {
                Id = 7401,
                BusinessUnitId = 71,
                CustomerId = 7201,
                PrimaryUserId = 7101,
                BackupUserId = 7102,
                Scope = OwnershipScope.GeneralCustomer,
                Priority = 100,
                EffectiveFrom = DateTime.UtcNow.AddDays(-1),
                IsActive = true,
                Source = "test",
                Version = 1
            });
        }
        await context.SaveChangesAsync();
    }

    private static User User(long id, long businessUnitId, string name) => new()
    {
        Id = id,
        FirstName = name,
        LastName = "User",
        Email = $"{name}@example.com",
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        Buid = businessUnitId,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };
}
