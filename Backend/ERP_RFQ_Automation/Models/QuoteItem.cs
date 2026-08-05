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

    /// <summary>Unit the quantity is quoted in (EA, PACK, M, …), carried from the RFQ line
    /// so the customer-facing document never prints a bare quantity. Null on legacy rows.</summary>
    public string? UnitOfMeasure { get; set; }

    /// <summary>The buyer's own line reference from their RFQ (e.g. SAP "00010", "OPT-29",
    /// traced from LeadItem/Rfqitem.LineItemNo). Printed instead of a synthetic 1,2,3 so the
    /// buyer can match our quote lines against their request. Null on legacy rows.</summary>
    public string? CustomerLineRef { get; set; }

    public virtual SetupMaster? DiscountType { get; set; }

    public virtual Product? Product { get; set; }

    public virtual Quote Quote { get; set; } = null!;

    public virtual Rfqitem? Rfqitem { get; set; }
}
