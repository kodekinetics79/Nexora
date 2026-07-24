using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public static class LifecycleStatusCatalog
{
    private static readonly (string Code, string Label)[] LeadStatuses =
    {
        ("RECEIVED", "Received"),
        ("PENDING_IDENTIFICATION", "Pending Identification"),
        ("UNASSIGNED", "Unassigned"),
        ("ASSIGNED", "Assigned"),
        ("UNDER_REVIEW", "Under Review"),
        ("QUALIFIED", "Qualified"),
        ("DISQUALIFIED", "Disqualified"),
        ("CONVERTED_TO_RFQ", "Converted to RFQ"),
        ("QUOTED", "Quoted"),
        ("NEGOTIATION", "Negotiation"),
        ("AWARDED", "Awarded"),
        ("PARTIALLY_AWARDED", "Partially Awarded"),
        ("LOST", "Lost"),
        ("CANCELLED", "Cancelled"),
        ("COMPLETED", "Completed"),
        ("DUPLICATED", "Duplicated")
    };

    private static readonly (string Code, string Label)[] RfqStatuses =
    {
        ("DRAFT", "Draft"),
        ("VALIDATING", "Validating"),
        ("NEEDS_REVIEW", "Needs Review"),
        ("READY_FOR_SALES", "Ready for Sales"),
        ("INVENTORY_CHECK", "Inventory Check"),
        ("SUPPLIER_SOURCING", "Supplier Sourcing"),
        ("AWAITING_SUPPLIER_RESPONSE", "Awaiting Supplier Response"),
        ("PRICING_COMPLETE", "Pricing Complete"),
        ("QUOTE_PREPARATION", "Quote Preparation"),
        ("QUOTE_SENT", "Quote Sent"),
        ("CUSTOMER_FOLLOW_UP", "Customer Follow-Up"),
        ("REVISION_REQUESTED", "Revision Requested"),
        ("AWARDED", "Awarded"),
        ("PARTIALLY_AWARDED", "Partially Awarded"),
        ("LOST", "Lost"),
        ("EXPIRED", "Expired"),
        ("CANCELLED", "Cancelled")
    };

    private static readonly (string Code, string Label)[] QuoteStatuses =
    {
        ("DRAFT", "Draft"),
        ("SENT", "Sent"),
        ("ACCEPTED", "Accepted"),
        ("REJECTED", "Rejected"),
        ("EXPIRED", "Expired"),
        ("ORDERED", "Ordered")
    };

    public static IReadOnlyList<SetupMaster> CreateFor(BusinessUnit businessUnit, string actor, DateTime? now = null)
    {
        var createdOn = now ?? DateTime.UtcNow;
        return LeadStatuses.Select(item => Create("LeadStatus", item, businessUnit, actor, createdOn))
            .Concat(RfqStatuses.Select(item => Create("RFQStatus", item, businessUnit, actor, createdOn)))
            .Concat(QuoteStatuses.Select(item => Create("QuoteStatus", item, businessUnit, actor, createdOn)))
            .ToArray();
    }

    public static async Task<long> ResolveIdAsync(
        ErpRfqAutomationContext db, long businessUnitId, string aggregateType, string code, CancellationToken ct = default)
    {
        var setupType = aggregateType switch
        {
            "Lead" => "leadstatus",
            "Rfq" => "rfqstatus",
            "Quote" => "quotestatus",
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        };
        var statuses = await db.SetupMasters.AsNoTracking()
            .Where(item => item.BusinessUnitId == businessUnitId && item.IsActive != false)
            .Where(item => item.SetupType.ToLower().Replace(" ", "") == setupType)
            .Select(item => new { item.SetupId, item.SetupCode, item.SetupValue })
            .ToListAsync(ct);
        var match = statuses
            .Where(item => LifecyclePolicy.Canonicalize(aggregateType, item.SetupCode, item.SetupValue) == code)
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.SetupCode))
            .ThenBy(item => item.SetupId)
            .FirstOrDefault();
        return match?.SetupId
            ?? throw new InvalidOperationException($"{code} is not configured and active for this tenant.");
    }

    private static SetupMaster Create(
        string type, (string Code, string Label) item, BusinessUnit businessUnit, string actor, DateTime createdOn)
        => new()
        {
            SetupType = type,
            SetupCode = item.Code,
            SetupValue = item.Label,
            Description = $"Governed lifecycle state ({LifecyclePolicy.Version})",
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = createdOn,
            BusinessUnit = businessUnit
        };
}
