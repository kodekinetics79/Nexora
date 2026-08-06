using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Regression for the defect behind "44 of 44 production leads unassigned".
///
/// <para>The deterministic engine only ever considered <c>customer_identifiers</c> as match
/// evidence. A lead whose customer a <b>human had already confirmed</b> — <c>Lead.CustomerId</c>
/// set with <c>CustomerMatchStatus = CUSTOMER_CONFIRMED</c> — produced
/// <c>candidates.Length == 0</c> and routed <c>NO_MATCH_EVIDENCE</c> into the unassigned queue,
/// even where an active <c>CustomerOwnership</c> named an owner. The strongest evidence in the
/// system was the one input routing ignored.</para>
///
/// <para>The confirmed customer is now supplied as a top-precedence <c>ErpAccount</c> candidate
/// at full confidence, so ownership precedence, workload relief, the ambiguity margin and the
/// audit trail all still apply — it is evidence, not a bypass.</para>
///
/// <para>PostgreSQL, because the persistence half of the fix is a real foreign key:
/// <c>lead_routing_decisions.MatchedIdentifierId</c> references <c>customer_identifiers</c>, so
/// a match derived from the lead must write <c>null</c> rather than a synthetic id. A SQLite
/// lane would not enforce that constraint and the bug would have shipped.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ConfirmedCustomerRoutingPostgreSqlTests
{
    private const long Tenant = 948_201;

    private readonly PostgreSqlTestDatabase _database;

    public ConfirmedCustomerRoutingPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    private async Task<(long customerId, long ownerUserId)> SeedTenantAsync()
    {
        await using var owner = _database.ContextFor(null);
        var existing = await owner.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Buid == Tenant);
        if (existing is not null)
            return (existing.Id, await owner.Users.IgnoreQueryFilters()
                .Where(u => u.Buid == Tenant).Select(u => u.Id).FirstAsync());

        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        await owner.SaveChangesAsync();

        var role = new SetupMaster
        {
            SetupType = "Role", SetupCode = "SUPER_ADMIN", SetupValue = "Routing Tester",
            BusinessUnitId = Tenant, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        owner.SetupMasters.Add(role);
        await owner.SaveChangesAsync();

        var user = new User
        {
            FirstName = "Named", LastName = "Owner", Email = $"named.owner.{Tenant}@tests.local",
            PasswordHash = "x", ImageUrl = string.Empty, RoleId = role.SetupId, Buid = Tenant,
            Timezone = "UTC", Region = "T", IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        owner.Users.Add(user);
        var customer = new Customer
        {
            Name = "Confirmed Customer", Buid = Tenant, IsActive = true,
            ImageUrl = string.Empty, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        owner.Customers.Add(customer);
        await owner.SaveChangesAsync();
        return (customer.Id, user.Id);
    }

    private async Task<long> QualifiedLeadWithConfirmedCustomerAsync(long customerId, string reference)
    {
        await using var owner = _database.ContextFor(null);
        var qualifiedId = await LifecycleStatusCatalog.ResolveIdAsync(owner, Tenant, "Lead", "QUALIFIED");
        var lead = new Lead
        {
            Rfqno = reference, BuyersName = "Buyer", RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(10), LeadSource = "tests",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Tenant, LeadStatusId = qualifiedId, NoOfLineItems = 1
        };
        lead.LeadItems.Add(new LeadItem
        {
            LineItemNo = "1", ProductShortDescription = "Widget",
            Quantity = 3, UnitOfMeasure = "EA", Currency = "SAR"
        });
        owner.Leads.Add(lead);
        await owner.SaveChangesAsync();
        lead.ResolveCommercialIdentity(customerId, null, "CUSTOMER_CONFIRMED");
        await owner.SaveChangesAsync();
        return lead.Id;
    }

    private ICommercialRoutingApplicationService RoutingFor(ErpRfqAutomationContext db) =>
        new CommercialRoutingApplicationService(db, new DeterministicRoutingEngine(), new RoutingPolicy());

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_lead_whose_customer_a_human_confirmed_routes_to_the_named_owner()
    {
        var (customerId, ownerUserId) = await SeedTenantAsync();

        await using (var setup = _database.ContextFor(null))
        {
            // A routing profile is what makes the owner AVAILABLE to the engine. Without it the
            // decision is OWNER_UNAVAILABLE, not PRIMARY_OWNER_ASSIGNED — which is precisely why
            // production, with zero sales_rep_profiles rows, could never assign anyone. Created
            // through the real application service, not by inserting the row.
            if (!await setup.Set<SalesRepProfile>().IgnoreQueryFilters()
                    .AnyAsync(p => p.BusinessUnitId == Tenant && p.UserId == ownerUserId))
                await new SalesApplicationService(new EfSalesPersistence(setup))
                    .UpsertProfileAsync(Tenant, new UpsertSalesRepProfileCommand(
                        UserId: ownerUserId, IsRoutingEligible: true, CapacityPercent: 100,
                        DistributionWeight: 1m, TerritoryKeys: Array.Empty<string>(),
                        ProductCategoryKeys: Array.Empty<string>(),
                        EffectiveFromUtc: DateTime.UtcNow.Date, EffectiveToUtc: null,
                        ExpectedVersion: 0, ActorId: "tests",
                        IdempotencyKey: $"conf-profile-{Tenant}-{ownerUserId}"), CancellationToken.None);

            if (!await setup.Set<CustomerOwnership>().IgnoreQueryFilters()
                    .AnyAsync(o => o.BusinessUnitId == Tenant && o.CustomerId == customerId && o.IsActive))
                await RoutingFor(setup).CreateOwnershipAsync(Tenant, new CreateCustomerOwnershipCommand(
                    CustomerId: customerId, PrimaryUserId: ownerUserId, BackupUserId: null,
                    Scope: OwnershipScope.GeneralCustomer, ScopeKey: null, Priority: 1,
                    EffectiveFrom: DateTime.UtcNow.Date, EffectiveTo: null,
                    Source: "tests", Reason: "regression"), CancellationToken.None);
        }

        var leadId = await QualifiedLeadWithConfirmedCustomerAsync(customerId, $"CONF-{Tenant}-A");

        await using var db = _database.ContextFor(null);
        var response = await RoutingFor(db).RouteLeadAsync(Tenant, new RouteLeadCommand(
            leadId, $"conf-route-{leadId}", $"conf-corr-{leadId}"), CancellationToken.None);

        Assert.Equal("PRIMARY_OWNER_ASSIGNED", response.DecisionCode);

        await using var assert = _database.ContextFor(null);
        var assignment = await assert.Set<LeadAssignment>().AsNoTracking()
            .SingleAsync(a => a.BusinessUnitId == Tenant && a.LeadId == leadId && a.EffectiveTo == null);
        Assert.Equal(ownerUserId, assignment.ToUserId);

        // The persistence half: the match came from the lead, not from a customer_identifiers
        // row, so the FK column must be null. Writing a synthetic id here violated
        // FK_lead_routing_decisions_customer_identifiers and aborted the whole route.
        var decision = await assert.Set<LeadRoutingDecision>().AsNoTracking()
            .OrderByDescending(d => d.Id)
            .FirstAsync(d => d.BusinessUnitId == Tenant && d.LeadId == leadId);
        Assert.Null(decision.MatchedIdentifierId);
        Assert.Equal(customerId, decision.CustomerId);

        // Nothing was dropped into the unassigned queue — that was the whole symptom.
        Assert.Empty(await assert.Set<UnassignedWorkItem>().AsNoTracking()
            .Where(w => w.BusinessUnitId == Tenant && w.LeadId == leadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_confirmed_customer_with_no_ownership_still_falls_back_to_the_controlled_queue()
    {
        // The fix supplies EVIDENCE, not an assignment. With no active ownership there is still
        // no one to assign to, and the lead must land in the governed queue with its SLA rather
        // than being force-assigned to satisfy the match.
        var (customerId, _) = await SeedTenantAsync();
        var orphanCustomerId = await NewCustomerWithoutOwnershipAsync();
        var leadId = await QualifiedLeadWithConfirmedCustomerAsync(orphanCustomerId, $"CONF-{Tenant}-B");

        await using var db = _database.ContextFor(null);
        var response = await RoutingFor(db).RouteLeadAsync(Tenant, new RouteLeadCommand(
            leadId, $"conf-route-{leadId}", $"conf-corr-{leadId}"), CancellationToken.None);

        Assert.Equal("NO_EFFECTIVE_OWNERSHIP", response.DecisionCode);

        await using var assert = _database.ContextFor(null);
        Assert.Single(await assert.Set<UnassignedWorkItem>().AsNoTracking()
            .Where(w => w.BusinessUnitId == Tenant && w.LeadId == leadId).ToListAsync());
        Assert.NotEqual(customerId, orphanCustomerId);
    }

    private async Task<long> NewCustomerWithoutOwnershipAsync()
    {
        await using var owner = _database.ContextFor(null);
        var customer = new Customer
        {
            Name = $"Unowned Customer {Guid.NewGuid():N}", Buid = Tenant, IsActive = true,
            ImageUrl = string.Empty, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        owner.Customers.Add(customer);
        await owner.SaveChangesAsync();
        return customer.Id;
    }
}
