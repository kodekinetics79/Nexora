using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class QuoteItem
{
    public long Id { get; set; }

    public long QuoteId { get; set; }

    public long? RfqitemId { get; set; }

    public long? ProductId { get; set; }

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal? Discount { get; set; }

    public decimal? TaxAmount { get; set; }

    public int? DeliveryLeadTime { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime? CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public long? DiscountTypeId { get; set; }

    public decimal? DiscountValue { get; set; }

    public virtual SetupMaster? DiscountType { get; set; }

    public virtual Product? Product { get; set; }

    public virtual Quote Quote { get; set; } = null!;

    public virtual Rfqitem? Rfqitem { get; set; }
}
