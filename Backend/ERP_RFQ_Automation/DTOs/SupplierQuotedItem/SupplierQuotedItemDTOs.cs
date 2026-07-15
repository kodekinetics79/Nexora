using System;

namespace ERP_RFQ_Automation.DTOs.SupplierQuotedItem
{
    public class SupplierQuotedItemResponseDTO
    {
        public long Id { get; set; }
        public long SupplierId { get; set; }
        public string? SupplierName { get; set; }
        public string? ItemName { get; set; }
        public string? Description { get; set; }
        public int? UomId { get; set; }
        public string? UomName { get; set; }
        public decimal Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public long? CurrencyId { get; set; }
        public string? CurrencyName { get; set; }
        public string? QuoteReference { get; set; }
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public decimal? TaxAmount { get; set; }
        public decimal? DiscountAmount { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public bool IsActive { get; set; }
        public long? BusinessUnitId { get; set; }
    }

    public class SupplierQuotedItemCreateDTO
    {
        public long SupplierId { get; set; }
        public string? ItemName { get; set; }
        public string? Description { get; set; }
        public int? UomId { get; set; }
        public decimal Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public long? CurrencyId { get; set; }
        public string? QuoteReference { get; set; }
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public decimal? TaxAmount { get; set; }
        public decimal? DiscountAmount { get; set; }
        public string CreatedBy { get; set; } = null!;
        public bool IsActive { get; set; } = true;
        public long? BusinessUnitId { get; set; }
    }

    public class SupplierQuotedItemUpdateDTO : SupplierQuotedItemCreateDTO
    {
        public long Id { get; set; }
    }
}
