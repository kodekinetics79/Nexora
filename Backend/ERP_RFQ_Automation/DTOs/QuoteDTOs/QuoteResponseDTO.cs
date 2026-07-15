using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    public class QuoteResponseDTO
    {
        public long Id { get; set; }
        public string QuoteNo { get; set; } = null!;
        public long? RfqId { get; set; }
        public string? RfqNo { get; set; }
        public long? CustomerId { get; set; }
        public string? CustomerName { get; set; }
        public long BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }
        public string? CustomerEmail { get; set; } // Added for persistence
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public long? StatusId { get; set; }
        public string? StatusValue { get; set; }
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? HeaderRemarks { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime? CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public long? DiscountTypeId { get; set; }
        public string? DiscountTypeName { get; set; }
        public decimal? DiscountValue { get; set; }
        public int ItemCount { get; set; } // Optimized: Item count for list views

        public List<QuoteItemResponseDTO> QuoteItems { get; set; } = new List<QuoteItemResponseDTO>();
    }

    public class QuoteItemResponseDTO
    {
        public long Id { get; set; }
        public long QuoteId { get; set; }
        public long? RfqItemId { get; set; }
        public long? ProductId { get; set; }
        public string? ProductName { get; set; }
        public string? ItemDescription { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal? Discount { get; set; }
        public decimal? TaxAmount { get; set; }
        public int? DeliveryLeadTime { get; set; }
        public long? DiscountTypeId { get; set; }
        public string? DiscountTypeName { get; set; }
        public decimal? DiscountValue { get; set; }
    }

    public class QuoteCreateRequestDTO
    {
        public string? QuoteNo { get; set; }
        public long? RfqId { get; set; }
        public long? CustomerId { get; set; }
        [Required]
        public long BusinessUnitId { get; set; }
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public long? StatusId { get; set; }
        public long? CurrencyId { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? HeaderRemarks { get; set; }
        [Required]
        public string CreatedBy { get; set; } = null!;
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }

        public List<QuoteItemCreateRequestDTO> QuoteItems { get; set; } = new List<QuoteItemCreateRequestDTO>();
    }

    public class QuoteItemCreateRequestDTO
    {
        public long? RfqItemId { get; set; }
        public long? ProductId { get; set; }
        public string? ItemDescription { get; set; }
        [Required]
        public decimal Quantity { get; set; }
        [Required]
        public decimal UnitPrice { get; set; }
        [Required]
        public decimal TotalAmount { get; set; }
        public decimal? Discount { get; set; }
        public decimal? TaxAmount { get; set; }
        public int? DeliveryLeadTime { get; set; }
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }
    }

    public class QuoteUpdateRequestDTO
    {
        public long Id { get; set; }
        [Required]
        public string QuoteNo { get; set; } = null!;
        public long? CustomerId { get; set; }
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public long? StatusId { get; set; }
        public long? CurrencyId { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? HeaderRemarks { get; set; }
        [Required]
        public string ModifiedBy { get; set; } = null!;
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }

        public List<QuoteItemUpdateRequestDTO> QuoteItems { get; set; } = new List<QuoteItemUpdateRequestDTO>();
    }

    public class QuoteItemUpdateRequestDTO
    {
        public long? Id { get; set; } // Null for new items during update
        public long? RfqItemId { get; set; }
        public long? ProductId { get; set; }
        public string? ItemDescription { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal? Discount { get; set; }
        public decimal? TaxAmount { get; set; }
        public int? DeliveryLeadTime { get; set; }
        public bool IsDeleted { get; set; } = false; // Flag to delete item
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }
    }
}
