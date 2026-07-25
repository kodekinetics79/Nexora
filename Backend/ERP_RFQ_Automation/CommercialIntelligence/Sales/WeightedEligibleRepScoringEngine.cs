namespace ERP_RFQ_Automation.CommercialIntelligence.Sales;

public sealed class WeightedEligibleRepScoringEngine
{
    public const string PolicyVersion = "sales-new-customer-v1";

    public WeightedRoutingResult Score(NewCustomerRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(request.BusinessUnitId);
        RequireUtc(request.OccurredAtUtc, nameof(request.OccurredAtUtc));

        var effectiveOwnership = request.ExistingOwnerships.Any(x =>
            x.BusinessUnitId == request.BusinessUnitId && x.IsActive &&
            (!request.CustomerId.HasValue || x.CustomerId == request.CustomerId.Value) &&
            x.EffectiveFrom <= request.OccurredAtUtc &&
            (x.EffectiveTo == null || x.EffectiveTo > request.OccurredAtUtc));
        if (effectiveOwnership)
            throw new SalesConflictException("Existing account ownership must be resolved by CommercialRouting.");

        var eligible = request.Candidates.Where(candidate => IsEligible(request, candidate)).ToArray();
        if (eligible.Length == 0)
            throw new SalesConflictException("No eligible sales representative is available.");

        var maxWeight = eligible.Max(x => x.Profile.DistributionWeight);
        var scores = request.Candidates.Select(candidate => ScoreCandidate(request, candidate, maxWeight))
            .OrderByDescending(x => x.Eligible)
            .ThenByDescending(x => x.TotalScore)
            .ThenBy(x => x.ActiveAssignmentCount)
            .ThenBy(x => request.Candidates.Single(c => c.User.Id == x.UserId).LastAssignedAtUtc ?? DateTime.MinValue)
            .ThenBy(x => x.UserId)
            .ToArray();

        return new WeightedRoutingResult(scores.First(x => x.Eligible).UserId, scores, PolicyVersion);
    }

    private static RepScoreBreakdown ScoreCandidate(
        NewCustomerRoutingRequest request, WeightedRepCandidate candidate, decimal maxWeight)
    {
        if (!IsEligible(request, candidate))
            return new(candidate.User.Id, false, 0, 0, 0, 0, 0, 0, 0,
                candidate.ActiveAssignments.Count, "Inactive, unavailable, cross-tenant, or outside the effective profile period.");

        var territory = Match(request.TerritoryKey, candidate.Profile.TerritoryKeys) ? 20m : 0m;
        var product = Match(request.ProductCategoryKey, candidate.Profile.ProductCategoryKeys) ? 25m : 0m;
        var team = request.PreferredTeamId.HasValue && candidate.TeamMemberships.Any(x =>
            x.TeamId == request.PreferredTeamId && IsEffective(x, request.OccurredAtUtc)) ? 10m : 0m;
        var capacity = Math.Clamp(candidate.Profile.CapacityPercent, 0, 100) * 0.20m;
        var workload = Math.Max(0, 100 - Math.Clamp(candidate.WorkloadPoints, 0, 100)) * 0.15m;
        var distribution = maxWeight <= 0 ? 0 : candidate.Profile.DistributionWeight / maxWeight * 10m;
        var total = territory + product + team + capacity + workload + distribution;
        return new(candidate.User.Id, true, decimal.Round(total, 4), territory, product, team,
            capacity, workload, decimal.Round(distribution, 4),
            candidate.ActiveAssignments.Count(x => x.EffectiveTo == null),
            "Deterministic score from fit, effective team, configured capacity, workload, and distribution weight.");
    }

    private static bool IsEligible(NewCustomerRoutingRequest request, WeightedRepCandidate candidate) =>
        candidate.User.Buid == request.BusinessUnitId && candidate.User.IsActive == true &&
        candidate.Profile.BusinessUnitId == request.BusinessUnitId &&
        candidate.Profile.UserId == candidate.User.Id && candidate.Profile.IsRoutingEligible &&
        candidate.Profile.CapacityPercent > 0 && candidate.Profile.DistributionWeight > 0 &&
        candidate.Profile.EffectiveFromUtc <= request.OccurredAtUtc &&
        (candidate.Profile.EffectiveToUtc == null || candidate.Profile.EffectiveToUtc > request.OccurredAtUtc) &&
        candidate.TeamMemberships.All(x => x.BusinessUnitId == request.BusinessUnitId && x.UserId == candidate.User.Id) &&
        candidate.ActiveAssignments.All(x => x.BusinessUnitId == request.BusinessUnitId &&
            x.ToUserId == candidate.User.Id && x.EffectiveTo == null);

    private static bool IsEffective(SalesTeamMembership membership, DateTime atUtc) =>
        membership.EffectiveFromUtc <= atUtc && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > atUtc);

    private static bool Match(string? requested, IEnumerable<string> configured) =>
        !string.IsNullOrWhiteSpace(requested) && configured.Any(value =>
            string.Equals(value.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static void RequireTenant(long businessUnitId)
    {
        if (businessUnitId <= 0) throw new SalesValidationException("Business unit ID is required.");
    }

    internal static void RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc) throw new SalesValidationException($"{name} must be UTC.");
    }
}
