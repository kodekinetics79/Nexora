using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

/// <summary>
/// FR-RFQ-07 routes an RFQ by customer, product category OR REGION. The scope-key builder used
/// to derive Branch and ProductCategory only, so a Territory or KeyAccountTeam ownership rule
/// could never match an ingested RFQ — the engine looked the scope up, found no key, and skipped
/// the rule. These tests pin the region half down: what a Territory key is derived from, that a
/// scope with no source stays UNDERIVED, and that an underived scope matches nothing rather than
/// everything.
/// </summary>
public sealed class RoutingScopeKeyDerivationTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TerritoryRegionAlias[] NoAliases = [];

    // ── Territory derivation ────────────────────────────────────────────────

    [Fact]
    public void Territory_derives_the_region_the_buyer_stated_on_the_rfq()
    {
        var derivation = RoutingScopeKeys.Territory(
            "Eastern Province", customer: null, NoAliases, NoAliases);

        Assert.True(derivation.IsDerived);
        Assert.Equal("Eastern Province", derivation.Key);
        Assert.StartsWith("leads.DeliveryLocation", derivation.Source);
    }

    [Fact]
    public void Territory_resolves_a_stated_city_to_its_region_through_the_tenant_masters()
    {
        // A rule is written on the province; the buyer named a city inside it. Without the
        // masters that rule would silently never fire.
        var derivation = RoutingScopeKeys.Territory(
            "Dammam", customer: null, NoAliases, [new TerritoryRegionAlias("Dammam", "Eastern Province")]);

        Assert.Equal("Eastern Province", derivation.Key);
        Assert.Contains("set_cities", derivation.Source);
    }

    [Fact]
    public void Territory_resolves_a_state_code_to_its_region_name()
    {
        var derivation = RoutingScopeKeys.Territory(
            "EP", customer: null, [new TerritoryRegionAlias("EP", "Eastern Province")], NoAliases);

        Assert.Equal("Eastern Province", derivation.Key);
        Assert.Contains("set_states", derivation.Source);
    }

    [Fact]
    public void Territory_keeps_the_stated_wording_when_a_city_name_spans_two_regions()
    {
        // Two masters claim the same wording. Picking one would be a guess, so the buyer's own
        // wording stands and only a rule written on that wording can fire.
        var derivation = RoutingScopeKeys.Territory("Khalidiyah", customer: null, NoAliases,
        [
            new TerritoryRegionAlias("Khalidiyah", "Makkah"),
            new TerritoryRegionAlias("Khalidiyah", "Riyadh")
        ]);

        Assert.Equal("Khalidiyah", derivation.Key);
        Assert.Contains("no region master match", derivation.Source);
    }

    [Fact]
    public void Territory_falls_back_to_the_customer_region_only_when_the_rfq_states_none()
    {
        var derivation = RoutingScopeKeys.Territory(
            deliveryLocation: null,
            new CustomerRegionEvidence("Riyadh", "Riyadh City", "Makkah", "Jeddah"),
            NoAliases, NoAliases);

        Assert.Equal("Riyadh", derivation.Key);
        Assert.StartsWith("customers.ShippingState", derivation.Source);
    }

    [Fact]
    public void Territory_prefers_the_rfq_delivery_location_over_the_customer_address()
    {
        var derivation = RoutingScopeKeys.Territory(
            "Eastern Province",
            new CustomerRegionEvidence("Riyadh", "Riyadh City", "Riyadh", "Riyadh City"),
            NoAliases, NoAliases);

        Assert.Equal("Eastern Province", derivation.Key);
        Assert.StartsWith("leads.DeliveryLocation", derivation.Source);
    }

    [Fact]
    public void Territory_walks_past_blank_customer_fields_to_the_first_that_states_a_region()
    {
        var derivation = RoutingScopeKeys.Territory(
            deliveryLocation: "   ",
            new CustomerRegionEvidence(null, "  ", null, "Jeddah"),
            NoAliases, NoAliases);

        Assert.Equal("Jeddah", derivation.Key);
        Assert.StartsWith("customers.BillingCity", derivation.Source);
    }

    [Fact]
    public void Territory_is_unavailable_and_derives_nothing_when_no_source_states_a_region()
    {
        var noSource = RoutingScopeKeys.Territory(null, null, NoAliases, NoAliases);
        var blankSources = RoutingScopeKeys.Territory(
            "  ", new CustomerRegionEvidence(null, null, "", "   "), NoAliases, NoAliases);

        Assert.False(noSource.IsDerived);
        Assert.Null(noSource.Key);
        Assert.Equal(RoutingScopeKeys.TerritoryUnavailable, noSource.Source);
        Assert.False(blankSources.IsDerived);
        Assert.Null(blankSources.Key);
    }

    [Fact]
    public void Territory_collapses_whitespace_so_document_wording_still_meets_a_typed_rule()
    {
        var derivation = RoutingScopeKeys.Territory(
            "  Eastern   Province \n", customer: null, NoAliases, NoAliases);

        Assert.Equal("Eastern Province", derivation.Key);
    }

    // ── KeyAccountTeam has no source at all ─────────────────────────────────

    [Fact]
    public void KeyAccountTeam_reports_itself_underivable_rather_than_inventing_a_key()
    {
        var derivation = RoutingScopeKeys.KeyAccountTeam();

        Assert.False(derivation.IsDerived);
        Assert.Null(derivation.Key);
        Assert.StartsWith("UNDERIVABLE", derivation.Source);
        Assert.Contains("customer-to-team", derivation.Source);
    }

    // ── Engine matching ─────────────────────────────────────────────────────

    [Fact]
    public void Route_matches_a_territory_rule_once_the_region_key_is_derived()
    {
        var request = Request(
            ownerships:
            [
                Ownership(11, 501, OwnershipScope.Territory, "Eastern Province"),
                Ownership(12, 502, OwnershipScope.GeneralCustomer)
            ],
            scopeKeys: new Dictionary<OwnershipScope, string?>
            {
                [OwnershipScope.Territory] = "Eastern Province"
            });

        var result = new DeterministicRoutingEngine().Route(request, new RoutingPolicy());

        Assert.Equal(11, result.Decision.OwnershipId);
        Assert.Equal(501, result.Assignment?.ToUserId);
    }

    [Fact]
    public void Route_matches_a_key_account_team_rule_once_a_key_is_supplied()
    {
        // The engine side of KeyAccountTeam works; only the derivation has no source. A caller
        // that knows the team (an operator-driven route) can still use the scope today.
        var request = Request(
            ownerships:
            [
                Ownership(13, 503, OwnershipScope.KeyAccountTeam, "Strategic Accounts"),
                Ownership(14, 504, OwnershipScope.GeneralCustomer)
            ],
            scopeKeys: new Dictionary<OwnershipScope, string?>
            {
                [OwnershipScope.KeyAccountTeam] = "Strategic Accounts"
            });

        var result = new DeterministicRoutingEngine().Route(request, new RoutingPolicy());

        Assert.Equal(13, result.Decision.OwnershipId);
        Assert.Equal(503, result.Assignment?.ToUserId);
    }

    [Fact]
    public void Route_matches_a_territory_rule_across_whitespace_noise_on_either_side()
    {
        var request = Request(
            ownerships: [Ownership(11, 501, OwnershipScope.Territory, " Eastern Province ")],
            scopeKeys: new Dictionary<OwnershipScope, string?>
            {
                [OwnershipScope.Territory] = "Eastern  Province"
            });

        var result = new DeterministicRoutingEngine().Route(request, new RoutingPolicy());

        Assert.Equal(11, result.Decision.OwnershipId);
    }

    [Fact]
    public void Route_matches_nothing_when_the_territory_source_is_absent()
    {
        // The rule must NOT fire on an RFQ with no region. A scoped rule that matches everything
        // is worse than one that matches nothing: it outranks every rule beneath it.
        var request = Request(
            ownerships: [Ownership(11, 501, OwnershipScope.Territory, "Eastern Province")],
            scopeKeys: new Dictionary<OwnershipScope, string?>());

        var result = new DeterministicRoutingEngine().Route(request, new RoutingPolicy());

        Assert.Equal(RoutingOutcome.Unassigned, result.Decision.Outcome);
        Assert.Equal("NO_EFFECTIVE_OWNERSHIP", result.Decision.DecisionCode);
        Assert.Null(result.Decision.OwnershipId);
        Assert.Null(result.Assignment);
    }

    [Fact]
    public void Route_never_matches_a_blank_key_against_a_blank_rule()
    {
        var request = Request(
            ownerships: [Ownership(11, 501, OwnershipScope.Territory, "   ")],
            scopeKeys: new Dictionary<OwnershipScope, string?> { [OwnershipScope.Territory] = "  " });

        var result = new DeterministicRoutingEngine().Route(request, new RoutingPolicy());

        Assert.Equal("NO_EFFECTIVE_OWNERSHIP", result.Decision.DecisionCode);
        Assert.Null(result.Assignment);
    }

    [Fact]
    public void Route_leaves_a_territory_rule_unmatched_when_the_derived_region_differs()
    {
        var request = Request(
            ownerships:
            [
                Ownership(11, 501, OwnershipScope.Territory, "Eastern Province"),
                Ownership(12, 502, OwnershipScope.GeneralCustomer)
            ],
            scopeKeys: new Dictionary<OwnershipScope, string?>
            {
                [OwnershipScope.Territory] = "Riyadh"
            });

        var result = new DeterministicRoutingEngine().Route(request, new RoutingPolicy());

        Assert.Equal(12, result.Decision.OwnershipId);
        Assert.Equal(502, result.Assignment?.ToUserId);
    }

    // ── End to end through the application service ──────────────────────────

    [Fact]
    public async Task Route_matches_a_territory_rule_derived_from_the_rfq_delivery_location()
    {
        using var db = new TestDb();
        await SeedAsync(db, deliveryLocation: "Eastern Province",
            territoryRuleKey: "Eastern Province");
        await using var context = db.ContextFor(71);
        var result = await Service(context).RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-territory", "corr-territory"), CancellationToken.None);

        Assert.Equal(RoutingOutcome.AssignedPrimary, result.Outcome);
        Assert.Equal(7101, result.SelectedUserId);
        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        Assert.Equal(7402, decision.OwnershipId);
        Assert.Equal("Eastern Province", ScopeKey(decision, OwnershipScope.Territory));
    }

    [Fact]
    public async Task Route_derives_the_region_from_the_customer_recorded_on_the_lead()
    {
        using var db = new TestDb();
        await SeedAsync(db, deliveryLocation: null, territoryRuleKey: "Riyadh",
            customerShippingState: "Riyadh");
        await using var context = db.ContextFor(71);
        var result = await Service(context).RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-customer-region", "corr-customer-region"),
            CancellationToken.None);

        Assert.Equal(RoutingOutcome.AssignedPrimary, result.Outcome);
        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        Assert.Equal(7402, decision.OwnershipId);
        Assert.Equal("Riyadh", ScopeKey(decision, OwnershipScope.Territory));
    }

    [Fact]
    public async Task Route_canonicalises_a_stated_city_against_the_tenant_region_masters()
    {
        using var db = new TestDb();
        await SeedAsync(db, deliveryLocation: "Dammam", territoryRuleKey: "Eastern Province",
            seedRegionMasters: true);
        await using var context = db.ContextFor(71);
        var result = await Service(context).RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-city", "corr-city"), CancellationToken.None);

        Assert.Equal(RoutingOutcome.AssignedPrimary, result.Outcome);
        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        Assert.Equal(7402, decision.OwnershipId);
        Assert.Equal("Eastern Province", ScopeKey(decision, OwnershipScope.Territory));
    }

    [Fact]
    public async Task Route_ignores_a_territory_rule_when_the_rfq_and_customer_state_no_region()
    {
        using var db = new TestDb();
        await SeedAsync(db, deliveryLocation: null, territoryRuleKey: "Eastern Province");
        await using var context = db.ContextFor(71);
        var result = await Service(context).RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-no-region", "corr-no-region"), CancellationToken.None);

        // The customer matched; the Territory rule is the only ownership and must not fire.
        Assert.Equal(RoutingOutcome.Unassigned, result.Outcome);
        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        Assert.Equal("NO_EFFECTIVE_OWNERSHIP", decision.DecisionCode);
        Assert.Null(ScopeKey(decision, OwnershipScope.Territory));
    }

    [Fact]
    public async Task Route_records_the_key_account_team_scope_as_underivable_on_every_decision()
    {
        using var db = new TestDb();
        await SeedAsync(db, deliveryLocation: "Riyadh", territoryRuleKey: "Riyadh");
        await using var context = db.ContextFor(71);
        await Service(context).RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-audit", "corr-audit"), CancellationToken.None);

        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        var team = Scope(decision, OwnershipScope.KeyAccountTeam);
        Assert.False(team.GetProperty("derived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, team.GetProperty("key").ValueKind);
        Assert.StartsWith("UNDERIVABLE", team.GetProperty("source").GetString());

        // Every ranked scope is accounted for — a silent omission would be indistinguishable
        // from the defect this fixes.
        var recorded = Scopes(decision).Select(s => s.GetProperty("scope").GetString()).ToArray();
        Assert.Equal(
            [nameof(OwnershipScope.ProductCategory), nameof(OwnershipScope.Branch),
             nameof(OwnershipScope.Territory), nameof(OwnershipScope.KeyAccountTeam)],
            recorded);
    }

    [Fact]
    public async Task Route_never_derives_a_region_from_another_tenants_data()
    {
        using var db = new TestDb();
        // This tenant's own customer states no region; another tenant's customer and region
        // masters do. Neither may leak in — a territory borrowed across a business unit would
        // route the RFQ on a region nobody stated.
        await SeedAsync(db, deliveryLocation: null, territoryRuleKey: "Eastern Province",
            otherTenantRegion: "Eastern Province");
        await using var context = db.ContextFor(71);
        var result = await Service(context).RouteLeadAsync(71,
            new RouteLeadCommand(701, "route-cross-tenant", "corr-cross-tenant"),
            CancellationToken.None);

        Assert.Equal(RoutingOutcome.Unassigned, result.Outcome);
        var decision = await context.Set<LeadRoutingDecision>().SingleAsync();
        Assert.Equal("NO_EFFECTIVE_OWNERSHIP", decision.DecisionCode);
        Assert.Null(ScopeKey(decision, OwnershipScope.Territory));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static JsonElement[] Scopes(LeadRoutingDecision decision) =>
        JsonDocument.Parse(decision.Explanation).RootElement
            .GetProperty("scopeKeys").EnumerateArray().ToArray();

    private static JsonElement Scope(LeadRoutingDecision decision, OwnershipScope scope) =>
        Scopes(decision).Single(item =>
            item.GetProperty("scope").GetString() == scope.ToString());

    private static string? ScopeKey(LeadRoutingDecision decision, OwnershipScope scope)
    {
        var key = Scope(decision, scope).GetProperty("key");
        return key.ValueKind == JsonValueKind.Null ? null : key.GetString();
    }

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    /// <summary>
    /// A routed lead whose customer is proven by a verified e-mail identifier, owned through a
    /// single Territory-scoped rule so the test turns entirely on whether that scope matches.
    /// </summary>
    private static async Task SeedAsync(
        TestDb db,
        string? deliveryLocation,
        string territoryRuleKey,
        string? customerShippingState = null,
        bool seedRegionMasters = false,
        string? otherTenantRegion = null)
    {
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 701, 71, buyersName: "Acme Trading");
        lead.Clientemail = "buyer@acme.example";
        lead.DeliveryLocation = deliveryLocation;

        var customer = Seed.Customer(context, 7201, 71, "Acme Trading");
        customer.ShippingState = customerShippingState;
        context.Users.AddRange(
            User(7101, 71, "owner"), User(7102, 71, "manager"));
        await context.SaveChangesAsync();

        // The lead names the customer, which is how BuildScopeKeyDerivationsAsync is allowed to
        // read a registered address at all.
        lead.ResolveCommercialIdentity(7201, null,
            LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

        var now = DateTime.UtcNow;
        context.SalesRepProfiles.Add(new SalesRepProfile
        {
            BusinessUnitId = 71, UserId = 7101, IsRoutingEligible = true, CapacityPercent = 100,
            DistributionWeight = 1, EffectiveFromUtc = now.AddDays(-1), Version = 1,
            UpdatedAtUtc = now, UpdatedBy = "test", LastMutationIdempotencyKey = "profile-7101"
        });
        context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
        {
            Id = 7301, BusinessUnitId = 71, CustomerId = 7201,
            IdentifierType = CustomerIdentifierType.Email,
            NormalizedValue = "buyer@acme.example", DisplayValue = "buyer@acme.example",
            IsVerified = true, Confidence = 0.99m, Source = "test",
            EffectiveFrom = now.AddDays(-1)
        });
        context.Set<CustomerOwnership>().Add(new CustomerOwnership
        {
            Id = 7402, BusinessUnitId = 71, CustomerId = 7201, PrimaryUserId = 7101,
            Scope = OwnershipScope.Territory, ScopeKey = territoryRuleKey, Priority = 100,
            EffectiveFrom = now.AddDays(-1), IsActive = true, Source = "test", Version = 1
        });

        if (otherTenantRegion != null)
        {
            Seed.EnsureBusinessUnit(context, 72);
            var stranger = Seed.Customer(context, 7299, 72, "Stranger Trading");
            stranger.ShippingState = otherTenantRegion;
            context.SetCountries.Add(new SetCountry
            {
                CountryId = 967, CountryCode = "XX", CountryName = "Elsewhere", Buid = 72,
                IsActive = true, CreatedBy = "test", CreatedDate = now
            });
            context.SetStates.Add(new SetState
            {
                StateId = 41, StateCode = "EP", StateName = otherTenantRegion, CountryId = 967,
                Buid = 72, IsActive = true, CreatedBy = "test", CreatedDate = now
            });
        }

        if (seedRegionMasters)
        {
            context.SetCountries.Add(new SetCountry
            {
                CountryId = 966, CountryCode = "SA", CountryName = "Saudi Arabia", Buid = 71,
                IsActive = true, CreatedBy = "test", CreatedDate = now
            });
            context.SetStates.Add(new SetState
            {
                StateId = 31, StateCode = "EP", StateName = "Eastern Province", CountryId = 966,
                Buid = 71, IsActive = true, CreatedBy = "test", CreatedDate = now
            });
            context.SetCities.Add(new SetCity
            {
                CityId = 311, CityName = "Dammam", StateId = 31, CountryId = 966, Buid = 71,
                IsActive = true, CreatedBy = "test", CreatedDate = now
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

    private static RoutingRequest Request(
        IReadOnlyCollection<CustomerOwnership> ownerships,
        IReadOnlyDictionary<OwnershipScope, string?> scopeKeys) => new(
            7, 42, "route-scope", "correlation-scope", Now,
            [new CustomerMatchCandidate(7, 100, 1, CustomerIdentifierType.ErpAccount, 0.99m)],
            ownerships,
            ownerships.SelectMany(o => new[] { o.PrimaryUserId })
                .Distinct()
                .Select(id => new RoutingUserAvailability(7, id,
                    Workload: new RoutingWorkloadSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0)))
                .ToArray(),
            scopeKeys);

    private static CustomerOwnership Ownership(
        long id, long primaryUserId, OwnershipScope scope, string? scopeKey = null) => new()
        {
            Id = id,
            BusinessUnitId = 7,
            CustomerId = 100,
            PrimaryUserId = primaryUserId,
            Scope = scope,
            ScopeKey = scopeKey,
            EffectiveFrom = Now.AddDays(-30),
            IsActive = true
        };
}
