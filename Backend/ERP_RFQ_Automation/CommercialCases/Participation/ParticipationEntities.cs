using System.Text.Json.Serialization;
using ERP_RFQ_Automation.LeadIdentity;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeadLineParticipationChoice
{
    Pending,
    Bid,
    NoBid,
    Clarify
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeadParticipationOutcome
{
    Pending,
    FullBid,
    PartialBid,
    NoBid
}

/// <summary>An immutable decision-support snapshot for one immutable lead revision.</summary>
public sealed class LeadFitAssessment
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long LeadRevisionId { get; set; }
    public int Sequence { get; set; }
    public string PolicyVersion { get; set; } = null!;
    public string Recommendation { get; set; } = null!;
    public bool IsActionable { get; set; }
    public string AssessmentJson { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string AssessedBy { get; set; } = null!;
    public DateTimeOffset AssessedAtUtc { get; set; }
}

/// <summary>
/// An append-only commercial commitment for one lead revision. Corrections create a new
/// sequence; they never rewrite the decision an auditor or promotion already saw.
/// </summary>
public sealed class LeadParticipationDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long LeadRevisionId { get; set; }
    public long FitAssessmentId { get; set; }
    public int Sequence { get; set; }
    public bool IsCommitted { get; set; }
    public LeadParticipationOutcome Outcome { get; set; }
    public string? ReasonCode { get; set; }
    public string? Notes { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string DecidedBy { get; set; } = null!;
    public DateTimeOffset DecidedAtUtc { get; set; }
    public ICollection<LeadLineParticipationDecision> Lines { get; } = new List<LeadLineParticipationDecision>();
}

/// <summary>An immutable answer for exactly one line of the decision's lead revision.</summary>
public sealed class LeadLineParticipationDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long LeadRevisionId { get; set; }
    public long ParticipationDecisionId { get; set; }
    public long LeadItemRevisionId { get; set; }
    public LeadLineParticipationChoice Choice { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonNotes { get; set; }
    public long? ProductId { get; set; }
    public int? Quantity { get; set; }
    public string? UnitOfMeasure { get; set; }
    public int? UomId { get; set; }
    public string? Currency { get; set; }
    public long? CurrencyId { get; set; }
    public string CatalogPolicyVersion { get; set; } = "lead-conversion-preview/v1";
    public string WarningSnapshotJson { get; set; } = "{}";
    public LeadParticipationDecision ParticipationDecision { get; set; } = null!;
    public LeadItemRevision LeadItemRevision { get; set; } = null!;
}
