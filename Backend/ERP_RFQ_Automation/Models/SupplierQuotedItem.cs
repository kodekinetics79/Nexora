using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class SupplierQuotedItem
{
    public long Id { get; set; }

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

    public DateTime CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public bool IsActive { get; set; }

    public long? BusinessUnitId { get; set; }

    public virtual BusinessUnit? BusinessUnit { get; set; }

    public virtual Currency? Currency { get; set; }

    public virtual ICollection<Rfqitem> Rfqitems { get; set; } = new List<Rfqitem>();

    public virtual Supplier Supplier { get; set; } = null!;

    public virtual SetUom? Uom { get; set; }
}
