using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Currency
{
    public long Id { get; set; }

    public string Code { get; set; } = null!;

    public string CurrencyName { get; set; } = null!;

    public string? Symbol { get; set; }

    public decimal? ExchangeRate { get; set; }

    public bool? IsBaseCurrency { get; set; }

    public long BusinessUnitId { get; set; }

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<Quote> Quotes { get; set; } = new List<Quote>();

    public virtual ICollection<Rfqitem> Rfqitems { get; set; } = new List<Rfqitem>();

    public virtual ICollection<SupplierQuotedItem> SupplierQuotedItems { get; set; } = new List<SupplierQuotedItem>();

    public virtual ICollection<Supplier> Suppliers { get; set; } = new List<Supplier>();
}
