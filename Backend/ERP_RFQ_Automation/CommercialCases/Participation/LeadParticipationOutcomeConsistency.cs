namespace ERP_RFQ_Automation.CommercialCases.Participation;

/// <summary>
/// Defines the aggregate invariant between a committed participation header and its immutable
/// line decisions. The application uses this before persistence and again at RFQ promotion;
/// PostgreSQL enforces the same rule at transaction commit.
/// </summary>
internal static class LeadParticipationOutcomeConsistency
{
    internal static LeadParticipationOutcome ExpectedCommittedOutcome(
        IEnumerable<LeadLineParticipationChoice> choices)
    {
        var lines = choices.ToArray();
        if (lines.Length == 0)
            throw new InvalidOperationException(
                "A committed participation decision must contain at least one line.");
        if (lines.Any(x => x is LeadLineParticipationChoice.Pending or LeadLineParticipationChoice.Clarify))
            throw new InvalidOperationException(
                "A committed participation decision cannot contain Pending or Clarify lines.");

        var bidCount = lines.Count(x => x == LeadLineParticipationChoice.Bid);
        return bidCount == 0
            ? LeadParticipationOutcome.NoBid
            : bidCount == lines.Length
                ? LeadParticipationOutcome.FullBid
                : LeadParticipationOutcome.PartialBid;
    }

    internal static void EnsureCommittedSnapshot(
        LeadParticipationOutcome recordedOutcome,
        IEnumerable<LeadLineParticipationChoice> choices)
    {
        var expected = ExpectedCommittedOutcome(choices);
        if (recordedOutcome != expected)
            throw new InvalidOperationException(
                $"The committed participation outcome {recordedOutcome} is inconsistent with its line decisions; expected {expected}.");
    }
}
