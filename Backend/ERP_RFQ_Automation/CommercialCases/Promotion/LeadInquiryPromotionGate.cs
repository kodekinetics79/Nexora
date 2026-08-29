using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.CommercialCases.Promotion;

/// <summary>
/// Keeps service scope out of the product-line RFQ promotion path. Service and mixed inquiries
/// have a dedicated governed BOQ workflow; silently flattening either into product RFQ lines would
/// discard scope, labor, milestone and pricing structure.
/// </summary>
internal static class LeadInquiryPromotionGate
{
    internal static void EnsureProductRfqEligible(Lead lead)
    {
        var inquiryType = lead.InquiryType?.Trim().ToLowerInvariant();
        if (inquiryType == "service")
            throw new LeadInquiryPromotionRouteException(
                "SERVICE_BOQ_REQUIRED",
                "This is a service inquiry. Product RFQ promotion is blocked; create and approve the governed service BOQ in the Services / BOQ workflow.");
        if (inquiryType == "mixed")
            throw new LeadInquiryPromotionRouteException(
                "MIXED_INQUIRY_REVIEW_REQUIRED",
                "This is a mixed product-and-service inquiry. Product RFQ promotion is blocked until a manager separates the product lines from the governed service BOQ scope.");
    }
}

internal sealed class LeadInquiryPromotionRouteException : InvalidOperationException
{
    internal LeadInquiryPromotionRouteException(string reasonCode, string message) : base(message)
        => ReasonCode = reasonCode;

    internal string ReasonCode { get; }
}
