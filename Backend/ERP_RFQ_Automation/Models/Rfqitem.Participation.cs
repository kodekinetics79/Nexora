namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Line-level bid participation — the decision a sales engineer makes per RFQ line about
/// whether Nexora will quote it.
///
/// <para><b>Why this is new state rather than a reuse.</b> The only bid/no-bid field that
/// existed was <see cref="Rfq.BiddingDecision"/>: header-level, a nullable <c>string</c>, no
/// enum, no validation, no reason and no actor. A multi-line SEC bid list is routinely a
/// partial bid — 12 of 84 lines quoted, the rest declined for stock, lead time or scope — so a
/// header-level decision cannot express what the business actually does. The transient
/// <c>ConvertRequestItem.Include</c> flag is not a substitute: it lives for the duration of one
/// conversion request and is never persisted, so nothing downstream can read what was decided
/// or why.</para>
///
/// <para><b>Deliberately NOT a new status system.</b> This does not touch the RFQ header
/// lifecycle (<c>RfqstatusId</c> + <c>LifecyclePolicy</c>), which stays exactly as it is. A
/// line decision is orthogonal to where the RFQ sits in its lifecycle.</para>
/// </summary>
public partial class Rfqitem
{
    /// <summary>The line has not been decided yet. Every line starts here, including every
    /// line created by Lead→RFQ conversion — a line nobody has looked at must never read as
    /// an implicit "yes".</summary>
    public const string ParticipationPending = "Pending";

    /// <summary>Nexora will quote this line. Only these lines reach a Customer Quote Draft.</summary>
    public const string ParticipationQuote = "Quote";

    /// <summary>Nexora declines this line. Requires a reason — a silent decline is
    /// indistinguishable from an oversight when the buyer asks why a line is absent.</summary>
    public const string ParticipationNoQuote = "NoQuote";

    /// <summary>One of <see cref="ParticipationPending"/> / <see cref="ParticipationQuote"/> /
    /// <see cref="ParticipationNoQuote"/>. Never null; legacy rows are backfilled to Pending.</summary>
    public string ParticipationDecision { get; private set; } = ParticipationPending;

    /// <summary>Operator-supplied reason, mandatory for <see cref="ParticipationNoQuote"/> and
    /// cleared on any other decision so a stale decline reason cannot outlive it.</summary>
    public string? NoQuoteReason { get; private set; }

    public string? ParticipationDecidedBy { get; private set; }

    public DateTime? ParticipationDecidedOn { get; private set; }

    /// <summary>True only for an explicit, recorded decision to quote this line.</summary>
    public bool IsMarkedForQuote => ParticipationDecision == ParticipationQuote;

    /// <summary>
    /// Records a line participation decision. The reason rule is enforced here, in the domain,
    /// rather than in a controller, so every caller — API, worker, test — obeys it.
    /// </summary>
    public void DecideParticipation(string decision, string? reason, string actor, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required to decide a line.", nameof(actor));

        var normalized = (decision ?? string.Empty).Trim();
        normalized = normalized switch
        {
            _ when string.Equals(normalized, ParticipationPending, StringComparison.OrdinalIgnoreCase) => ParticipationPending,
            _ when string.Equals(normalized, ParticipationQuote, StringComparison.OrdinalIgnoreCase) => ParticipationQuote,
            _ when string.Equals(normalized, ParticipationNoQuote, StringComparison.OrdinalIgnoreCase) => ParticipationNoQuote,
            _ => throw new ArgumentException(
                $"Unknown participation decision '{decision}'. Expected Pending, Quote or NoQuote.", nameof(decision))
        };

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalized == ParticipationNoQuote && trimmedReason is null)
            throw new InvalidOperationException("A No-Quote decision requires a reason.");
        if (normalized == ParticipationNoQuote && trimmedReason!.Length < 5)
            throw new InvalidOperationException("A No-Quote reason must be meaningful (at least 5 characters).");

        ParticipationDecision = normalized;
        // A reason belongs to a decline. Carrying it forward onto a Quote or Pending line would
        // leave the printed audit trail asserting a decline that was reversed.
        NoQuoteReason = normalized == ParticipationNoQuote ? trimmedReason : null;
        ParticipationDecidedBy = actor.Trim();
        ParticipationDecidedOn = nowUtc;
    }
}
