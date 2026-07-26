namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    // Request DTO for supplier search
    public class SupplierSearchRequestDTO
    {
        public string? SearchTerm { get; set; }
        public string? ProductCategory { get; set; }
        public long BusinessUnitId { get; set; }
    }

    // Response DTO for supplier search
    public class SupplierSearchResultDTO
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? ContactEmail { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public decimal? SuccessRate { get; set; }
        public int? AvgResponseTime { get; set; }
        public string? Tags { get; set; }
        public string GovernanceStatus { get; set; } = null!;
        public string VerificationStatus { get; set; } = null!;
        public string ComplianceStatus { get; set; } = null!;
        public string RiskStatus { get; set; } = null!;
        public string ReadinessStatus { get; set; } = null!;
    }

    // Batch quote request DTO
    public class BatchQuoteRequestDTO
    {
        public long SupplierId { get; set; }
        public string SupplierEmail { get; set; } = null!;
        public string SupplierName { get; set; } = null!;
        public List<QuoteItemDTO> Items { get; set; } = new List<QuoteItemDTO>();
        public string? RfqNumber { get; set; }
        public DateTime? RequiredDate { get; set; }
        public string? AdditionalNotes { get; set; }
    }

    public class QuoteItemDTO
    {
        public string? PartNumber { get; set; }
        public string? Manufacturer { get; set; }
        public string? Description { get; set; }
        public int Quantity { get; set; }
        public string? UnitOfMeasure { get; set; }
    }

    // Email template DTO
    public class QuoteEmailTemplateDTO
    {
        public string Subject { get; set; } = null!;
        public string Body { get; set; } = null!;
        public string ToEmail { get; set; } = null!;
        public string? CcEmail { get; set; }
        public List<QuoteItemDTO> Items { get; set; } = new List<QuoteItemDTO>();
    }
}
