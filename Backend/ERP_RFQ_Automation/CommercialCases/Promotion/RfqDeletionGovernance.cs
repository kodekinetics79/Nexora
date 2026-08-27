using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.CommercialCases.Promotion;

/// <summary>
/// Hard deletion is a narrow correction tool for an unsubmitted manual draft. Governed RFQs
/// retain their commercial lineage and must move through lifecycle/cancellation instead.
/// </summary>
public static class RfqDeletionGovernance
{
    public static void EnsureHardDeletable(Rfq rfq)
    {
        ArgumentNullException.ThrowIfNull(rfq);

        var status = string.IsNullOrWhiteSpace(rfq.Rfqstatus?.SetupCode)
            ? rfq.Rfqstatus?.SetupValue
            : rfq.Rfqstatus.SetupCode;
        if (!string.Equals(status?.Trim(), "DRAFT", StringComparison.OrdinalIgnoreCase))
            throw new RfqDeletionNotAllowedException(
                "Only an unsubmitted DRAFT RFQ can be permanently deleted.");

        if (rfq.LeadId.HasValue
            || rfq.PromotionId.HasValue
            || rfq.SourceLeadRevisionId.HasValue
            || rfq.ParticipationDecisionId.HasValue)
        {
            throw new RfqDeletionNotAllowedException(
                "A lead-origin or promoted RFQ is an immutable commercial record and cannot be permanently deleted.");
        }
    }
}

public sealed class RfqDeletionNotAllowedException : InvalidOperationException
{
    public RfqDeletionNotAllowedException(string message) : base(message) { }
}
