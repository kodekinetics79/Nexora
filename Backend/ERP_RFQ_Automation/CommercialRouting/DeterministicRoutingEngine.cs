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

        var primary = Availability(request, ownership.PrimaryUserId);
        var backup = ownership.BackupUserId is long backupId ? Availability(request, backupId) : null;
        var primaryAvailable = IsAvailable(primary);
        var backupAvailable = IsAvailable(backup);
        var relievePrimary = primaryAvailable && backupAvailable &&
            WorkloadPoints(primary) - WorkloadPoints(backup) >= policy.BackupReliefThresholdPoints;
        var selectedUserId = primaryAvailable && !relievePrimary
            ? ownership.PrimaryUserId
            : backupAvailable ? ownership.BackupUserId : null;
        if (selectedUserId is null)
            return Unassigned(request, policy, CustomerMatchStatus.Matched, "OWNER_UNAVAILABLE", winner, winner.Confidence, ownership);

        var outcome = selectedUserId == ownership.PrimaryUserId
            ? RoutingOutcome.AssignedPrimary
            : RoutingOutcome.AssignedBackup;
        var code = outcome == RoutingOutcome.AssignedPrimary
            ? "PRIMARY_OWNER_ASSIGNED"
            : relievePrimary ? "BACKUP_OWNER_ASSIGNED_FOR_WORKLOAD" : "BACKUP_OWNER_ASSIGNED";
        var decision = CreateDecision(request, policy, CustomerMatchStatus.Matched, outcome,
            code,
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

        if (!request.ScopeKeys.TryGetValue(ownership.Scope, out var requestKey)) return false;

        // Both sides are collapsed the same way so that whitespace noise in a region copied out
        // of a document cannot miss a hand-typed rule. A blank on EITHER side must never match:
        // two empty strings comparing equal would make a scoped rule fire on every RFQ and
        // outrank every rule beneath it, which is the one failure worse than never firing.
        var derived = RoutingScopeKeys.CollapseWhitespace(requestKey);
        var configured = RoutingScopeKeys.CollapseWhitespace(ownership.ScopeKey);
        return derived.Length > 0 && configured.Length > 0
            && string.Equals(derived, configured, StringComparison.OrdinalIgnoreCase);
    }

    private static RoutingUserAvailability? Availability(RoutingRequest request, long userId) =>
        request.UserAvailability.SingleOrDefault(x =>
            x.BusinessUnitId == request.BusinessUnitId && x.UserId == userId);

    private static bool IsAvailable(RoutingUserAvailability? availability) =>
        availability is { IsActive: true, IsAvailable: true, CapacityPercent: > 0 };

    private static int WorkloadPoints(RoutingUserAvailability? availability) =>
        availability?.Workload?.WorkloadPoints ?? 0;

    /// <summary>
    /// The decision code written when the tenant's configured fallback owner takes an inquiry the
    /// engine could not place by any ownership rule.
    /// </summary>
    public const string DefaultOwnerAssignedCode = "DEFAULT_OWNER_ASSIGNED";

    /// <summary>
    /// The decision code written when a human deliberately releases a lead back to the pool.
    /// Declared here rather than in the application service because the queue-item shape it
    /// produces is this engine's, and <see cref="PriorityFor"/> has to recognise it.
    /// </summary>
    public const string ManuallyUnassignedCode = "MANUALLY_UNASSIGNED";

    private static RoutingResult Unassigned(
        RoutingRequest request,
        RoutingPolicy policy,
        CustomerMatchStatus status,
        string code,
        CustomerMatchCandidate? candidate,
        decimal confidence,
        CustomerOwnership? ownership = null)
    {
        // FALLBACK OWNER — the one customer-set answer to "when Nexora cannot work out who owns an
        // inquiry, give it to ___". It is consulted HERE, at the single point where every
        // un-placeable path converges, so it cannot be reached without every ownership rule,
        // threshold and ambiguity check having already been evaluated and lost.
        //
        // The named person must clear the SAME IsAvailable bar as any other candidate — active,
        // available, capacity above zero, governed profile — because a fallback that hands work to
        // someone who cannot act on it is worse than a queue nobody has looked at yet. Unset or
        // ineligible falls through to the queue, which is the behaviour that existed before.
        //
        // The original code ("why could you not place it?") is preserved in the explanation and the
        // ownership that was considered is still recorded, so "why did this person get it?" stays
        // answerable: they got it because nothing else could be determined, not because a rule
        // named them.
        if (request.DefaultOwnerUserId is long fallbackUserId &&
            IsAvailable(Availability(request, fallbackUserId)))
        {
            var fallbackDecision = CreateDecision(request, policy, status,
                RoutingOutcome.AssignedPrimary, DefaultOwnerAssignedCode,
                candidate, confidence, ownership, fallbackUserId, fallbackFor: code);
            return new RoutingResult(fallbackDecision, new LeadAssignment
            {
                BusinessUnitId = request.BusinessUnitId,
                LeadId = request.LeadId,
                ToUserId = fallbackUserId,
                AssignmentScope = request.AssignmentScope,
                // No OwnershipId: this assignment is NOT derived from an ownership row. The row
                // that was considered and lost is on the decision, where it belongs.
                OwnershipId = null,
                RoutingDecision = fallbackDecision,
                ReasonCode = fallbackDecision.DecisionCode,
                EffectiveFrom = request.OccurredOn,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey
            }, null);
        }

        var decision = CreateDecision(request, policy, status, RoutingOutcome.Unassigned, code,
            candidate, confidence, ownership, ownership?.PrimaryUserId);
        return new RoutingResult(decision, null, WorkItemFor(decision, policy));
    }

    /// <summary>
    /// The ONE place an <see cref="UnassignedWorkItem"/> is built, for any reason.
    ///
    /// <para>Every field is derived from the decision that caused it, so a queue row can never
    /// disagree with the decision it points at. That is why this takes the decision rather than the
    /// request: the application service's manual-unassign path has a decision and no
    /// <see cref="RoutingRequest"/>, and it must not hand-roll a second copy of this shape —
    /// "Unassign" previously wrote a decision and NO queue row at all, which is precisely how a
    /// released lead became invisible to every screen and every sweeper.</para>
    /// </summary>
    public static UnassignedWorkItem WorkItemFor(LeadRoutingDecision decision, RoutingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(policy);
        return new UnassignedWorkItem
        {
            BusinessUnitId = decision.BusinessUnitId,
            LeadId = decision.LeadId,
            RoutingDecision = decision,
            ReasonCode = decision.DecisionCode,
            Priority = PriorityFor(decision.MatchStatus, decision.DecisionCode, decision.MatchConfidence),
            EnteredOn = decision.CreatedOn,
            SlaDueOn = decision.CreatedOn.Add(policy.UnassignedSla),
            SuggestedCustomerId = decision.CustomerId,
            SuggestedUserId = decision.SuggestedUserId,
            MatchConfidence = decision.MatchConfidence,
            // The line a human actually reads on the queue. It is plain English on purpose: the
            // decision code beside it is an audit key, not a sentence, and must never be the only
            // thing a user is given to act on.
            RequiredAction = decision.MatchStatus is CustomerMatchStatus.Ambiguous
                or CustomerMatchStatus.BelowThreshold
                ? "Confirm customer and owner"
                : "Assign an eligible owner",
            IdempotencyKey = decision.IdempotencyKey
        };
    }

    private static int PriorityFor(CustomerMatchStatus status, string code, decimal confidence) => code switch
    {
        "AMBIGUOUS_CUSTOMER" => 90,
        "OWNER_UNAVAILABLE" => 80,
        "MATCH_BELOW_THRESHOLD" => confidence >= 0.70m ? 75 : 70,
        // A lead a human deliberately handed back is triaged, real work that somebody already
        // decided was worth someone's time. It ranks above the two codes that mean "this tenant
        // has not finished configuring routing yet", and below anything needing a customer decision.
        ManuallyUnassignedCode => 65,
        "NO_EFFECTIVE_OWNERSHIP" => 60,
        "NO_MATCH_EVIDENCE" => 50,
        _ when status == CustomerMatchStatus.Ambiguous => 90,
        _ => 50
    };

    private static LeadRoutingDecision CreateDecision(
        RoutingRequest request,
        RoutingPolicy policy,
        CustomerMatchStatus status,
        RoutingOutcome outcome,
        string code,
        CustomerMatchCandidate? candidate,
        decimal confidence,
        CustomerOwnership? ownership,
        long? selectedUserId,
        string? fallbackFor = null) => new()
        {
            BusinessUnitId = request.BusinessUnitId,
            LeadId = request.LeadId,
            CustomerId = candidate?.CustomerId,
            // IdentifierId 0 means the match came from a customer a human already CONFIRMED on
            // the lead, not from a customer_identifiers row. MatchedIdentifierId carries a real
            // foreign key to that table, so a synthetic 0 must be written as null rather than
            // fabricating a reference to a row that does not exist.
            MatchedIdentifierId = candidate?.IdentifierId is > 0 ? candidate.IdentifierId : null,
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
                decisionCode = code,
                // The code this decision REPLACED. Null on every ordinary decision; set only when
                // the tenant's fallback owner took an inquiry the engine could not place, and it
                // carries the reason it could not — without it, "DEFAULT_OWNER_ASSIGNED" would
                // record that the fallback fired while erasing what it was standing in for.
                fallbackForDecisionCode = fallbackFor,
                requestHash = request.RequestHash,
                // Why each scope did or did not have a key to match on. An underived scope is
                // recorded with its reason rather than omitted, so "no Territory rule was
                // written" stays distinguishable from "this RFQ stated no region".
                scopeKeys = (request.ScopeKeyDerivations ?? [])
                    .OrderBy(derivation => derivation.Scope)
                    .Select(derivation => new
                    {
                        scope = derivation.Scope.ToString(),
                        key = derivation.Key,
                        derived = derivation.IsDerived,
                        source = derivation.Source
                    })
                    .ToArray(),
                workloadPolicy = new
                {
                    maximumPoints = policy.MaximumWorkloadPoints,
                    backupReliefThresholdPoints = policy.BackupReliefThresholdPoints,
                    weights = new
                    {
                        activeLead = policy.ActiveLeadWeight,
                        leadLine = policy.LeadLineWeight,
                        maximumLinePointsPerJourney = policy.MaximumLinePointsPerJourney,
                        overdueDeadline = policy.OverdueDeadlineWeight,
                        urgentDeadline = policy.UrgentDeadlineWeight,
                        approachingDeadline = policy.ApproachingDeadlineWeight,
                        openRfq = policy.OpenRfqWeight,
                        openQuote = policy.OpenQuoteWeight,
                        followUp = policy.FollowUpWeight
                    }
                },
                consideredOwners = request.UserAvailability
                    .Where(item => item.UserId == ownership?.PrimaryUserId || item.UserId == ownership?.BackupUserId)
                    .OrderBy(item => item.UserId)
                    .Select(item => new
                    {
                        item.UserId,
                        item.IsActive,
                        item.IsAvailable,
                        measurementStatus = item.Workload is null ? "unavailable" : "measured",
                        capacityPercent = item.Workload is null ? (int?)null : item.CapacityPercent,
                        workload = item.Workload
                    })
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
