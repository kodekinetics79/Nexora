using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Quote
{
    public long Id { get; set; }

    public string QuoteNo { get; set; } = null!;

    public long? Rfqid { get; set; }

    public long? CustomerId { get; set; }

    public long BusinessUnitId { get; set; }

    public DateTime? QuoteDate { get; set; }

    public DateTime? ValidUntil { get; set; }

    public long? StatusId { get; set; }

    public long? CurrencyId { get; set; }

    public decimal? TotalAmount { get; set; }

    public string? HeaderRemarks { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime? CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public long? DiscountTypeId { get; set; }

    public decimal? DiscountValue { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual Currency? Currency { get; set; }

    public virtual Customer? Customer { get; set; }

    public virtual SetupMaster? DiscountType { get; set; }

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<QuoteItem> QuoteItems { get; set; } = new List<QuoteItem>();

    public virtual Rfq? Rfq { get; set; }

    public virtual SetupMaster? Status { get; set; }
}
