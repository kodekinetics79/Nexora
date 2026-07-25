using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

public sealed class CoreSalesScoringTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_customer_scoring_combines_fit_capacity_workload_weight_and_stable_tie_breaks()
    {
        var engine = new WeightedEligibleRepScoringEngine();
        var request = new NewCustomerRoutingRequest(71, null, "US-EAST", "VALVES", 900, Now, [],
        [
            Candidate(7101, capacity: 90, weight: 1, workload: 70, territory: "US-EAST", category: "VALVES", team: 900),
            Candidate(7102, capacity: 80, weight: 2, workload: 10, territory: "US-EAST", category: "VALVES", team: 900)
        ]);

        var result = engine.Score(request);

        Assert.Equal(7102, result.SelectedUserId);
        Assert.Equal(WeightedEligibleRepScoringEngine.PolicyVersion, result.PolicyVersion);
        Assert.True(result.RankedCandidates[0].TotalScore > result.RankedCandidates[1].TotalScore);
        Assert.Equal(10m, result.RankedCandidates[0].DistributionScore);
    }

    [Fact]
    public void Scoring_rejects_existing_effective_account_ownership()
    {
        var ownership = new CustomerOwnership
        {
            BusinessUnitId = 71, CustomerId = 200, PrimaryUserId = 7101,
            EffectiveFrom = Now.AddDays(-1), IsActive = true
        };
        var request = new NewCustomerRoutingRequest(71, 200, null, null, null, Now,
            [ownership], [Candidate(7101)]);

        var exception = Assert.Throws<SalesConflictException>(() =>
            new WeightedEligibleRepScoringEngine().Score(request));

        Assert.Contains("CommercialRouting", exception.Message);
    }

    [Fact]
    public void Scoring_excludes_cross_tenant_and_expired_profiles()
    {
        var crossTenant = Candidate(7101);
        crossTenant.User.Buid = 72;
        var expired = Candidate(7102);
        expired.Profile.EffectiveToUtc = Now;

        Assert.Throws<SalesConflictException>(() => new WeightedEligibleRepScoringEngine().Score(
            new NewCustomerRoutingRequest(71, null, null, null, null, Now, [], [crossTenant, expired])));
    }

    private static WeightedRepCandidate Candidate(long userId, int capacity = 100, decimal weight = 1,
        int workload = 0, string? territory = null, string? category = null, long? team = null)
    {
        var memberships = team.HasValue
            ? new[] { new SalesTeamMembership { BusinessUnitId = 71, UserId = userId, TeamId = team.Value, EffectiveFromUtc = Now.AddDays(-1) } }
            : [];
        return new WeightedRepCandidate(
            new User { Id = userId, Buid = 71, IsActive = true, FirstName = "Rep", LastName = userId.ToString(), Email = $"{userId}@example.test", PasswordHash = "x", ImageUrl = "", CreatedBy = "test" },
            new SalesRepProfile
            {
                BusinessUnitId = 71, UserId = userId, IsRoutingEligible = true,
                CapacityPercent = capacity, DistributionWeight = weight,
                TerritoryKeys = territory == null ? [] : [territory],
                ProductCategoryKeys = category == null ? [] : [category],
                EffectiveFromUtc = Now.AddDays(-1)
            }, memberships, [], workload, null);
    }
}
