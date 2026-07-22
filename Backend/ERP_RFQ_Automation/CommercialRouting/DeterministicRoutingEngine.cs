using System.Text.Json;

namespace ERP_RFQ_Automation.CommercialRouting;

public sealed class DeterministicRoutingEngine
{
    public RoutingResult Route(RoutingRequest request, RoutingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        ValidateRequest(request);

        var candidates = request.MatchCandidates
            .Where(x => x.BusinessUnitId == request.BusinessUnitId && x.IsVerified)
            .Where(x => policy.IdentifierPrecedence.Contains(x.IdentifierType))
            .ToArray();

        if (candidates.Length == 0)
            return Unassigned(request, policy, CustomerMatchStatus.NoEvidence, "NO_MATCH_EVIDENCE", null, 0m);

        var precedence = policy.IdentifierPrecedence
            .Select((type, rank) => (type, rank))
            .ToDictionary(x => x.type, x => x.rank);
        var bestRank = candidates.Min(x => precedence[x.IdentifierType]);
        var ranked = candidates
            .Where(x => precedence[x.IdentifierType] == bestRank)
            .GroupBy(x => x.CustomerId)
            .Select(group => group
                .OrderByDescending(x => x.Confidence)
                .ThenBy(x => x.IdentifierId)
                .First())
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.CustomerId)
            .ToArray();

        var winner = ranked[0];
        if (winner.Confidence < policy.MatchThreshold)
            return Unassigned(request, policy, CustomerMatchStatus.BelowThreshold, "MATCH_BELOW_THRESHOLD", winner, winner.Confidence);

        if (ranked.Length > 1 && winner.Confidence - ranked[1].Confidence < policy.AmbiguityMargin)
            return Unassigned(request, policy, CustomerMatchStatus.Ambiguous, "AMBIGUOUS_CUSTOMER", winner, winner.Confidence);

        var ownership = SelectOwnership(request, policy, winner.CustomerId);
        if (ownership is null)
            return Unassigned(request, policy, CustomerMatchStatus.Matched, "NO_EFFECTIVE_OWNERSHIP", winner, winner.Confidence);

        var primaryAvailable = IsAvailable(request, ownership.PrimaryUserId);
        var backupAvailable = ownership.BackupUserId is long backupId && IsAvailable(request, backupId);
        var selectedUserId = primaryAvailable ? ownership.PrimaryUserId : backupAvailable ? ownership.BackupUserId : null;
        if (selectedUserId is null)
            return Unassigned(request, policy, CustomerMatchStatus.Matched, "OWNER_UNAVAILABLE", winner, winner.Confidence, ownership);

        var outcome = primaryAvailable ? RoutingOutcome.AssignedPrimary : RoutingOutcome.AssignedBackup;
        var decision = CreateDecision(request, policy, CustomerMatchStatus.Matched, outcome,
            outcome == RoutingOutcome.AssignedPrimary ? "PRIMARY_OWNER_ASSIGNED" : "BACKUP_OWNER_ASSIGNED",
            winner, winner.Confidence, ownership, selectedUserId);
        var assignment = new LeadAssignment
        {
            BusinessUnitId = request.BusinessUnitId,
            LeadId = request.LeadId,
            ToUserId = selectedUserId.Value,
            AssignmentScope = request.AssignmentScope,
            OwnershipId = ownership.Id,
            RoutingDecision = decision,
            ReasonCode = decision.DecisionCode,
            EffectiveFrom = request.OccurredOn,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey
        };

        return new RoutingResult(decision, assignment, null);
    }

    private static CustomerOwnership? SelectOwnership(RoutingRequest request, RoutingPolicy policy, long customerId)
    {
        var scopeRanks = policy.OwnershipPrecedence
            .Select((scope, rank) => (scope, rank))
            .ToDictionary(x => x.scope, x => x.rank);

        return request.Ownerships
            .Where(x => x.BusinessUnitId == request.BusinessUnitId && x.CustomerId == customerId)
            .Where(x => x.IsActive && x.EffectiveFrom <= request.OccurredOn)
            .Where(x => x.EffectiveTo is null || x.EffectiveTo > request.OccurredOn)
            .Where(x => scopeRanks.ContainsKey(x.Scope) && ScopeMatches(request, x))
            .OrderBy(x => scopeRanks[x.Scope])
            .ThenByDescending(x => x.Priority)
            .ThenByDescending(x => x.EffectiveFrom)
            .ThenBy(x => x.Id)
            .FirstOrDefault();
    }

    private static bool ScopeMatches(RoutingRequest request, CustomerOwnership ownership)
    {
        if (ownership.Scope is OwnershipScope.CustomerException or OwnershipScope.GeneralCustomer)
            return true;

        return request.ScopeKeys.TryGetValue(ownership.Scope, out var requestKey)
            && !string.IsNullOrWhiteSpace(requestKey)
            && string.Equals(requestKey.Trim(), ownership.ScopeKey?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAvailable(RoutingRequest request, long userId) => request.UserAvailability.Any(x =>
        x.BusinessUnitId == request.BusinessUnitId &&
        x.UserId == userId &&
        x.IsActive &&
        x.IsAvailable &&
        x.CapacityPercent > 0);

    private static RoutingResult Unassigned(
        RoutingRequest request,
        RoutingPolicy policy,
        CustomerMatchStatus status,
        string code,
        CustomerMatchCandidate? candidate,
        decimal confidence,
        CustomerOwnership? ownership = null)
    {
        var decision = CreateDecision(request, policy, status, RoutingOutcome.Unassigned, code,
            candidate, confidence, ownership, ownership?.PrimaryUserId);
        var workItem = new UnassignedWorkItem
        {
            BusinessUnitId = request.BusinessUnitId,
            LeadId = request.LeadId,
            RoutingDecision = decision,
            ReasonCode = code,
            EnteredOn = request.OccurredOn,
            SlaDueOn = request.OccurredOn.Add(policy.UnassignedSla),
            SuggestedCustomerId = candidate?.CustomerId,
            SuggestedUserId = ownership?.PrimaryUserId,
            MatchConfidence = confidence,
            RequiredAction = status is CustomerMatchStatus.Ambiguous or CustomerMatchStatus.BelowThreshold
                ? "Confirm customer and owner"
                : "Assign an eligible owner",
            IdempotencyKey = request.IdempotencyKey
        };
        return new RoutingResult(decision, null, workItem);
    }

    private static LeadRoutingDecision CreateDecision(
        RoutingRequest request,
        RoutingPolicy policy,
        CustomerMatchStatus status,
        RoutingOutcome outcome,
        string code,
        CustomerMatchCandidate? candidate,
        decimal confidence,
        CustomerOwnership? ownership,
        long? selectedUserId) => new()
        {
            BusinessUnitId = request.BusinessUnitId,
            LeadId = request.LeadId,
            CustomerId = candidate?.CustomerId,
            MatchedIdentifierId = candidate?.IdentifierId,
            OwnershipId = ownership?.Id,
            SuggestedUserId = ownership?.PrimaryUserId,
            SelectedUserId = selectedUserId,
            MatchStatus = status,
            Outcome = outcome,
            MatchConfidence = confidence,
            DecisionCode = code,
            Explanation = JsonSerializer.Serialize(new
            {
                matchStatus = status.ToString(),
                outcome = outcome.ToString(),
                decisionCode = code
            }),
            PolicyVersion = policy.Version,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            CreatedOn = request.OccurredOn
        };

    private static void ValidateRequest(RoutingRequest request)
    {
        if (request.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(request.BusinessUnitId));
        if (request.LeadId <= 0) throw new ArgumentOutOfRangeException(nameof(request.LeadId));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(request.IdempotencyKey));
        if (string.IsNullOrWhiteSpace(request.CorrelationId)) throw new ArgumentException("Correlation ID is required.", nameof(request.CorrelationId));
        if (request.OccurredOn.Kind != DateTimeKind.Utc) throw new ArgumentException("OccurredOn must be UTC.", nameof(request.OccurredOn));
    }
}
