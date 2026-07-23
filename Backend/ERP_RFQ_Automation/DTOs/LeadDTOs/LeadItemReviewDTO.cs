namespace ERP_RFQ_Automation.DTOs.Lead
{
    // A single line item as submitted from the review workbench. When Id is null or 0
    // the item is inserted; otherwise the matching existing item is updated. Items that
    // exist in the database but are absent from the submitted collection are deleted.
    public class LeadItemReviewDTO
    {
        public long? Id { get; set; }
        public string? LineItemNo { get; set; }
        public string? ProductShortName { get; set; }
        public string? ProductShortDescription { get; set; }
        public string? CommodityProduct { get; set; }
        public string? ItemMaterialCode { get; set; }
        [System.ComponentModel.DataAnnotations.StringLength(3, MinimumLength = 3)]
        public string? Currency { get; set; }
        public string? UnitOfMeasure { get; set; }
        [System.ComponentModel.DataAnnotations.Range(typeof(decimal), "0", "79228162514264337593543950335")]
        public decimal? UnitPrice { get; set; }
        [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
        public int? Quantity { get; set; }
        public string? ManufacturerName { get; set; }
        public string? ManufacturerPartNumber { get; set; }
        public string? AlternateProductName { get; set; }
        public string? AlternatePartNumber { get; set; }
        public string? ItemText { get; set; }
        [System.ComponentModel.DataAnnotations.Range(0, int.MaxValue)]
        public int? LeadTime { get; set; }

        /// <summary>
        /// Optional. When omitted (null) the item's stored ExtraFields are preserved
        /// untouched; when provided the sanitized value replaces them.
        /// </summary>
        public Dictionary<string, string>? ExtraFields { get; set; }
    }
}
