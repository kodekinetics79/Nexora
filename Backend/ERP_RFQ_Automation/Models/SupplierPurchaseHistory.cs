using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class SupplierPurchaseHistory
{
    public long Id { get; set; }

    public long ProductId { get; set; }

    public long SupplierId { get; set; }

    public DateTime PurchaseDate { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public string? Currency { get; set; }

    public string? BatchNo { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? PoDocId { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual Supplier Supplier { get; set; } = null!;
}
