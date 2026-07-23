// DTOs/AcceptedLeadDTOs.cs

using ERP_RFQ_Automation.DTOs.LeadDTOs;

namespace ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;

public class AcceptedLeadResponseDTO
{
    public long Id { get; set; }
    public long? CommercialCaseId { get; set; }
    public string? CommercialCaseReference { get; set; }
    public string? Rfqno { get; set; }
    public string? BuyersName { get; set; }
    public DateTime RecDate { get; set; }
    public DateTime? BidClosingDate { get; set; }
    public string? BiddingDecision { get; set; }
    public DateTime? AcknowledgmentDate { get; set; }
    public DateTime? SubDate { get; set; }
    public string? HeaderRemarks { get; set; }
    public string? OpportunityNo { get; set; }
    public int? NoOfLineItems { get; set; }
    public string? Rfqtype { get; set; }
    public string? DurationAgreement { get; set; }
    public decimal? Aiconfidence { get; set; }
    public string LeadSource { get; set; } = null!;
    public string? EmailSource { get; set; }
    public string? Clientemail { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public long? LeadStatusId { get; set; }

    // Assignment Info - Enhanced
    public long? AssignedToId { get; set; }
    public string? AssignedToFullName { get; set; }
    public DateTime? AssignedOn { get; set; }
    public string? AssignComment { get; set; }

    // WP-A1 unassigned-aging: whole hours the lead has sat unassigned (null when
    // assigned) and whether that exceeds the tenant's SLA threshold.
    public int? UnassignedHours { get; set; }
    public bool IsUnassignedOverdue { get; set; }

    // WP-BOQ foundation: "product" | "service" | "mixed" | null (unclassified).
    public string? InquiryType { get; set; }
    // Distinct list badge for service-scope leads (service or mixed inquiries).
    public bool IsServiceInquiry => InquiryType is "service" or "mixed";

    // WP-A3 duplicate flag: null | "suspected" | "confirmed" | "not_duplicate".
    public string? DuplicateStatus { get; set; }
    public long? DuplicateOfLeadId { get; set; }

    // Item Count - Optimized for list views
    public int ItemCount { get; set; }

    public List<AcceptedLeadItemDTO> LeadItems { get; set; } = new();
    public List<AttachmentResponseDTO> Attachments { get; set; } = new();
}

public class AcceptedLeadItemDTO
{
    public long Id { get; set; }
    public string? CompanyRef { get; set; }
    public string? CustomerAccountPortalId { get; set; }
    public string? CustomerRfqno { get; set; }
    public string? ItemMaterialCode { get; set; }
    public string? CommodityProduct { get; set; }
    public string? BuyerName { get; set; }
    public string? LineItemNo { get; set; }
    public string? ProductShortName { get; set; }
    public string? Alternative { get; set; }
    public string? ProductShortDescription { get; set; }
    public string? Currency { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal? UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string? StorageLocation { get; set; }
    public string? ManufacturerName { get; set; }
    public string? ManufacturerPartNumber { get; set; }
    public string? AlternateProductName { get; set; }
    public string? AlternatePartNumber { get; set; }
    public string? ItemText { get; set; }
    public string? MaterialPotext { get; set; }
    public int? LeadTime { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public DateTime? BidClosingDateLine { get; set; }
    public decimal? Aiconfidence { get; set; }
}

public class UserDropdownDTO
{
    public long Id { get; set; }
    public string FullName { get; set; } = string.Empty;
}

// Updated Request DTO - now supports comment
public class AssignLeadRequestDTO
{
    public long LeadId { get; set; }
    public long AssignedToUserId { get; set; }
    public long? ExpectedAssigneeId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? Comment { get; set; }
}
