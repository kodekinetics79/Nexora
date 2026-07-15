using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class ShipmentStatusHistory
{
    public long Id { get; set; }

    public long ShipmentId { get; set; }

    public long? PreviousStatusId { get; set; }

    public long NewStatusId { get; set; }

    public string ChangedBy { get; set; } = null!;

    public DateTime ChangedOn { get; set; }

    public string? Notes { get; set; }

    public virtual SetupMaster NewStatus { get; set; } = null!;

    public virtual SetupMaster? PreviousStatus { get; set; }

    public virtual Shipment Shipment { get; set; } = null!;
}
