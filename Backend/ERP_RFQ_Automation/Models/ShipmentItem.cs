using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class ShipmentItem
{
    public long Id { get; set; }

    public long ShipmentId { get; set; }

    public long OrderItemId { get; set; }

    public decimal Quantity { get; set; }

    public string? Notes { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public bool IsActive { get; set; }

    public virtual OrderItem OrderItem { get; set; } = null!;

    public virtual Shipment Shipment { get; set; } = null!;
}
