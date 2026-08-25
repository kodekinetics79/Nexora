namespace ERP_RFQ_Automation.CommercialCases.Promotion;

/// <summary>The immutable authorization receipt behind one formal lead-origin RFQ.</summary>
public sealed class RfqPromotion
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long LeadRevisionId { get; set; }
    public long ParticipationDecisionId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string PromotedBy { get; set; } = null!;
    public DateTimeOffset PromotedAtUtc { get; set; }
}

public sealed record PromoteLeadToRfqCommand(
    long ExpectedLeadRevisionId,
    int ExpectedDecisionVersion,
    int ExpectedParticipationVersion,
    long? ParticipationDecisionId,
    string IdempotencyKey,
    string Actor);

public sealed record RfqPromotionResult(
    long PromotionId,
    long RfqId,
    string RfqNumber,
    long LeadId,
    long LeadRevisionId,
    long ParticipationDecisionId,
    int LeadRevisionNumber,
    int ParticipationVersion,
    int PromotedLineCount,
    DateTimeOffset PromotedAtUtc,
    string PromotedBy,
    bool Replayed);

public interface IRfqPromotionService
{
    Task<RfqPromotionResult> PromoteAsync(
        long businessUnitId, long leadId, PromoteLeadToRfqCommand command, CancellationToken ct = default);
}
