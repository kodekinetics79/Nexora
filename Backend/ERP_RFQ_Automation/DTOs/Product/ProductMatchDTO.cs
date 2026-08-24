namespace ERP_RFQ_Automation.DTOs.ProductDTOs
{
    // Request DTO for product matching
    public class ProductMatchRequestDTO
    {
        public string? PartNo { get; set; }
        public string? Manufacturer { get; set; }
        public string? Description { get; set; }
        public long BusinessUnitId { get; set; }
        public decimal Quantity { get; set; } = 1m;
    }

    // Response DTO for product matching
    public class ProductMatchResponseDTO
    {
        public bool HasExactMatch { get; set; }
        public ProductMatchSuggestionDTO? ExactMatch { get; set; }
        public List<ProductMatchSuggestionDTO> FuzzyMatches { get; set; } = new List<ProductMatchSuggestionDTO>();
    }

    // Individual product match suggestion
    public class ProductMatchSuggestionDTO
    {
        public long ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public string PartNo { get; set; } = null!;
        public string? Manufacturer { get; set; }
        public string? Description { get; set; }
        public decimal QtyOnHand { get; set; }
        public decimal? UnitCost { get; set; }
        public decimal? SellingPrice { get; set; }
        public decimal? FinalLandedCost { get; set; }
        public decimal? FinalSalesPrice { get; set; }

        public int? LeadTime { get; set; }
        public long? PreferredSupplierId { get; set; }
        public string? PreferredSupplierName { get; set; }
        public string? PreferredSupplierEmail { get; set; }
        public int MatchConfidence { get; set; } // 0-100
        public string MatchReason { get; set; } = null!;
        public decimal AvailableToPromise { get; set; }
        public decimal IncomingAvailable { get; set; }
        public decimal ProjectedShortage { get; set; }
        public string AvailabilityStatus { get; set; } = "UnknownProduct";
        public int? LeadTimeDays { get; set; }
        public DateOnly? ExpectedAvailableOn { get; set; }
        public string? CostCurrencyCode { get; set; }
        public string DecisionState { get; set; } = "ReviewRequired";
        public string? EvidenceReference { get; set; }
    }

    // Purchase history DTO
    public class PurchaseHistoryDTO
    {
        public long ProductId { get; set; }
        public List<PurchaseHistoryItemDTO> PurchaseHistory { get; set; } = new List<PurchaseHistoryItemDTO>();
    }

    public class PurchaseHistoryItemDTO
    {
        public long OrderId { get; set; }
        public string? OrderNumber { get; set; }
        public DateTime OrderDate { get; set; }
        public string? SupplierName { get; set; }
        public int? Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public string? Currency { get; set; }
    }

    // Stock details DTO
    public class StockDetailsDTO
    {
        public long ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public string PartNo { get; set; } = null!;
        public decimal QtyOnHand { get; set; }
        public decimal ReorderPoint { get; set; }
        public string? WarehouseName { get; set; }
        public string? StockPartNumber { get; set; }
        public decimal? UnitCost { get; set; }
        public decimal? SellingPrice { get; set; }
        public decimal? FinalLandedCost { get; set; }
        public decimal? FinalSalesPrice { get; set; }

        public decimal? ReplacementCost { get; set; }
        public string? Currency { get; set; }
        public int? LeadTime { get; set; }
        public bool HasPurchaseHistory { get; set; }
    }
}
