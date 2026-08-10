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
        public bool IsActive { get; set; } = true;
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
        public long? BusinessUnitId { get; set; }
    }

    public class SupplierQuotedItemUpdateDTO : SupplierQuotedItemCreateDTO
    {
        public long Id { get; set; }
    }
}
