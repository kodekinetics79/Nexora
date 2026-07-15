namespace ERP_RFQ_Automation.DTOs.Lead
{
    public class LeadItemResponseDTO
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
}
