namespace ERP_RFQ_Automation.Services;

/// <summary>Outcome of recording a quote as submitted on the customer's portal.</summary>
public sealed record PortalSubmissionResult(bool IsSubmitted, bool WasAlreadySent, string? QuoteNo, string? BlockCode, string? BlockReason)
{
    public static PortalSubmissionResult Submitted(string quoteNo) => new(true, false, quoteNo, null, null);
    public static PortalSubmissionResult AlreadySent(string quoteNo) => new(false, true, quoteNo, null, null);
    public static PortalSubmissionResult Blocked(string code, string reason) => new(false, false, null, code, reason);
}
