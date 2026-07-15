using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class ViewSupplierPriceList
{
    public long ProductId { get; set; }

    public string? ProductName { get; set; }

    public string PartNo { get; set; } = null!;

    public long SupplierId { get; set; }

    public string SupplierName { get; set; } = null!;

    public decimal LatestPrice { get; set; }

    public string? Currency { get; set; }

    public DateTime LastPurchasedDate { get; set; }
}
