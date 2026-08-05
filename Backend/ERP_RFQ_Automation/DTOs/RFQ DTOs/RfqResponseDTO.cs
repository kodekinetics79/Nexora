using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.RfqDTOs
{
    public class RfqResponseDTO
    {
        public long Id { get; set; }
        public long? CommercialCaseId { get; set; }
        public string? CommercialCaseReference { get; set; }
        public string? NexoraSerial { get; set; }
        public string Rfqno { get; set; } = null!;
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
        public long? RfqtypeId { get; set; }
        public string? DurationAgreement { get; set; }
        public long? LeadId { get; set; }
        public int ActiveLeadRevision { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public long BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }  // for UI
        public long? RfqstatusId { get; set; }
        public string? RfqstatusValue { get; set; }    // from SetupMaster
        public string? RfqstatusCode { get; set; }
        public int LifecycleVersion { get; set; }
        public long? CustomerId { get; set; }
        public long? ContactId { get; set; }
        public string? ContactName { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public string? LeadEmail { get; set; }
        public string? AccountOwnerName { get; set; }
        public string? OpportunityOwnerName { get; set; }
        public int ItemCount { get; set; } // Optimized: Item count for list views
        public string Readiness => CustomerId.HasValue && LeadId.HasValue && ItemCount > 0 ? "Ready for Quote" : "Review Required";
        public List<RfqitemResponseDTO> Rfqitems { get; set; } = new List<RfqitemResponseDTO>();
    }

    public class RfqitemResponseDTO
    {
        public long Id { get; set; }
        public long Rfqid { get; set; }
        public string? CompanyRef { get; set; }
        public string? CustomerAccountPortalId { get; set; }
        public string? CustomerRfqno { get; set; }
        public string? ItemMaterialCode { get; set; }
        public string? LineItemNo { get; set; }
        public long? ProductId { get; set; }
        public string? ProductName { get; set; }  // for UI
        public string? CommodityProduct { get; set; }
        public string? ProductShortName { get; set; }
        public string? ProductShortDescription { get; set; }
        public string? Alternative { get; set; }
        public string? BuyerName { get; set; }
        public string? Currency { get; set; }
        public long? CurrencyId { get; set; }
        public string? UnitOfMeasure { get; set; }
        public int? UomId { get; set; }
        public decimal? UnitPrice { get; set; }
        public int Quantity { get; set; }
        public string? StorageLocation { get; set; }
        public long? WarehouseId { get; set; }
        public string? WarehouseName { get; set; }  // for UI
        public string? ManufacturerName { get; set; }
        public string? ManufacturerPartNumber { get; set; }
        public long? SupplierId { get; set; }
        public string? SupplierName { get; set; }  // for UI
        public string? AlternateProductName { get; set; }
        public string? AlternatePartNumber { get; set; }
        public string? ItemText { get; set; }
        public string? MaterialPotext { get; set; }
        public int? LeadTime { get; set; }
        public DateTime? RequiredDesiredDate { get; set; }
        public DateTime? ReceivedDate { get; set; }
        public DateTime? BidClosingDateLine { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public decimal? Aiconfidence { get; set; }
    }

    public class RfqCreateRequestDTO
    {
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        // A stray [Required] used to sit here on Id. [Required] can never fire on a
        // non-nullable value type (the binder always materializes a value), so it was
        // dead wherever it pointed — including RecDate, where it would be equally
        // inert. Removed rather than relocated; the server supplies authoritative
        // values for everything a client omits.
        public long Id { get; set; }
        public DateTime RecDate { get; set; }
        public DateTime? BidClosingDate { get; set; }
        public string? BiddingDecision { get; set; }
        public DateTime? AcknowledgmentDate { get; set; }
        public DateTime? SubDate { get; set; }
        public string? HeaderRemarks { get; set; }
        public string? OpportunityNo { get; set; }
        public string? Rfqtype { get; set; }
        public long? RfqtypeId { get; set; }
        public string? DurationAgreement { get; set; }
        public long? LeadId { get; set; }
        public long BusinessUnitId { get; set; }
        public long? RfqstatusId { get; set; }
        public long? CustomerId { get; set; }
        public long? ContactId { get; set; }
        public string? CreatedBy { get; set; }
        public List<RfqitemCreateRequestDTO> Rfqitems { get; set; } = new List<RfqitemCreateRequestDTO>();
    }

    public class RfqitemCreateRequestDTO
    {
        public string? CompanyRef { get; set; }
        public string? CustomerAccountPortalId { get; set; }
        public string? CustomerRfqno { get; set; }
        public string? ItemMaterialCode { get; set; }
        public string? LineItemNo { get; set; }
        public long? ProductId { get; set; }
        public string? CommodityProduct { get; set; }
        public string? ProductShortName { get; set; }
        public string? ProductShortDescription { get; set; }
        public string? Alternative { get; set; }
        public string? BuyerName { get; set; }
        public string? Currency { get; set; }
        public long? CurrencyId { get; set; }
        public string? UnitOfMeasure { get; set; }
        public int? UomId { get; set; }
        public decimal? UnitPrice { get; set; }
        [Required]
        public int Quantity { get; set; }
        public string? StorageLocation { get; set; }
        public long? WarehouseId { get; set; }
        public string? ManufacturerName { get; set; }
        public string? ManufacturerPartNumber { get; set; }
        public long? SupplierId { get; set; }
        public string? AlternateProductName { get; set; }
        public string? AlternatePartNumber { get; set; }
        public string? ItemText { get; set; }
        public string? MaterialPotext { get; set; }
        public int? LeadTime { get; set; }
        public DateTime? RequiredDesiredDate { get; set; }
        public DateTime? ReceivedDate { get; set; }
        public DateTime? BidClosingDateLine { get; set; }
        public string? CreatedBy { get; set; }
        public decimal? Aiconfidence { get; set; }
    }

    public class RfqUpdateRequestDTO
    {
        public long Id { get; set; }
        public string? BuyersName { get; set; }
        [Required]
        public DateTime RecDate { get; set; }
        public DateTime? BidClosingDate { get; set; }
        public string? BiddingDecision { get; set; }
        public DateTime? AcknowledgmentDate { get; set; }
        public DateTime? SubDate { get; set; }
        public string? HeaderRemarks { get; set; }
        public string? OpportunityNo { get; set; }
        public string? Rfqtype { get; set; }
        public long? RfqtypeId { get; set; }
        public string? DurationAgreement { get; set; }
        public long? LeadId { get; set; }
        public long? RfqstatusId { get; set; }
        public long? CustomerId { get; set; }
        public long? ContactId { get; set; }
        public List<RfqitemUpdateRequestDTO> Rfqitems { get; set; } = new List<RfqitemUpdateRequestDTO>();
    }

    public class RfqitemUpdateRequestDTO
    {
        public string? CompanyRef { get; set; }
        public string? CustomerAccountPortalId { get; set; }
        public string? CustomerRfqno { get; set; }
        public string? ItemMaterialCode { get; set; }
        public string? LineItemNo { get; set; }
        public long? ProductId { get; set; }
        public string? CommodityProduct { get; set; }
        public string? ProductShortName { get; set; }
        public string? ProductShortDescription { get; set; }
        public string? Alternative { get; set; }
        public string? BuyerName { get; set; }
        public string? Currency { get; set; }
        public long? CurrencyId { get; set; }
        public string? UnitOfMeasure { get; set; }
        public int? UomId { get; set; }
        public decimal? UnitPrice { get; set; }
        public int Quantity { get; set; }
        public string? StorageLocation { get; set; }
        public long? WarehouseId { get; set; }
        public string? ManufacturerName { get; set; }
        public string? ManufacturerPartNumber { get; set; }
        public long? SupplierId { get; set; }
        public string? AlternateProductName { get; set; }
        public string? AlternatePartNumber { get; set; }
        public string? ItemText { get; set; }
        public string? MaterialPotext { get; set; }
        public int? LeadTime { get; set; }
        public DateTime? RequiredDesiredDate { get; set; }
        public DateTime? ReceivedDate { get; set; }
        public DateTime BidClosingDateLine { get; set; }
        public decimal? Aiconfidence { get; set; }
        public long Id { get; set; }
    }

    public class PaginatedRfqResponseDTO
    {
        public IEnumerable<RfqResponseDTO> Items { get; set; } = new List<RfqResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}
