using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Shipment
{
    public long Id { get; set; }

    public string ShipmentNo { get; set; } = null!;

    public long OrderId { get; set; }

    public long BusinessUnitId { get; set; }

    public long StatusId { get; set; }

    public DateTime ShipmentDate { get; set; }

    public DateTime? EstimatedDeliveryDate { get; set; }

    public DateTime? ActualDeliveryDate { get; set; }

    public string? Carrier { get; set; }

    public string? ServiceLevel { get; set; }

    public string? TrackingNumber { get; set; }

    public string? ExternalId { get; set; }

    public decimal? ShippingCost { get; set; }

    public string? LabelUrl { get; set; }

    public string? RawResponse { get; set; }

    public string? ShippingAddress { get; set; }

    public string? Notes { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public bool IsActive { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual Order Order { get; set; } = null!;

    public virtual ICollection<ShipmentItem> ShipmentItems { get; set; } = new List<ShipmentItem>();

    public virtual ICollection<ShipmentStatusHistory> ShipmentStatusHistories { get; set; } = new List<ShipmentStatusHistory>();

    public virtual SetupMaster Status { get; set; } = null!;
}
