using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Order
{
    public long Id { get; set; }

    public string OrderNo { get; set; } = null!;

    public long? QuoteId { get; set; }

    public long? LeadId { get; set; }

    public long? Rfqid { get; set; }

    public long CustomerId { get; set; }

    public long BusinessUnitId { get; set; }

    public long StatusId { get; set; }

    public long? CurrencyId { get; set; }

    public long? PaymentMethodId { get; set; }

    public long? PaymentStatusId { get; set; }

    public DateTime? PaymentDate { get; set; }

    public decimal PaidAmount { get; set; }

    public decimal? BalanceAmount { get; set; }

    public string? PaymentReference { get; set; }

    public DateTime OrderDate { get; set; }

    public DateTime? DeliveryDate { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal? SubTotal { get; set; }

    public decimal? TaxAmount { get; set; }

    public decimal? DiscountAmount { get; set; }

    public string? TermsAndConditions { get; set; }

    public string? Notes { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public bool IsActive { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual Currency? Currency { get; set; }

    public virtual Customer Customer { get; set; } = null!;

    public virtual Lead? Lead { get; set; }

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual SetupMaster? PaymentMethod { get; set; }

    public virtual SetupMaster? PaymentStatus { get; set; }

    public virtual Quote? Quote { get; set; }

    public virtual Rfq? Rfq { get; set; }

    public virtual ICollection<Shipment> Shipments { get; set; } = new List<Shipment>();

    public virtual SetupMaster Status { get; set; } = null!;
}
