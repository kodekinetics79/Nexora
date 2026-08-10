using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Shipment
{
    /// <summary>
    /// The commercial case this shipment fulfils, carried rather than joined.
    ///
    /// <para>FR-DLM-01 requires the delivery note to reference the Sales Order, the material lots
    /// and the customer PO. A despatch document that cannot state its own master reference makes
    /// that a join every time it is printed, and makes a POD photographed on a loading bay
    /// impossible to tie back without the system in front of you.</para>
    /// </summary>
    public long? CommercialCaseId { get; set; }

    public string? NexoraSerial { get; set; }

    /// <summary>
    /// Copies the despatched sales order's master reference onto the shipment.
    ///
    /// <para>The order is the only honest source: a shipment exists because an order line has to
    /// leave the warehouse, and the order already carries the case it belongs to. Carrying it
    /// forward here — rather than deriving it when a delivery note is printed — is what lets the
    /// despatch document, and a POD photographed against it, name its own case.</para>
    ///
    /// <para>An order with no case (a manually raised sales order that never came from a lead)
    /// leaves both fields null. That is a gap in the chain, and recording it as a gap is better
    /// than minting a reference the rest of the system would then treat as real.</para>
    /// </summary>
    public void InheritCommercialIdentity(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.BusinessUnitId != BusinessUnitId)
            throw new InvalidOperationException("Order and Shipment must belong to the same tenant.");
        if (order.Id != OrderId)
            throw new InvalidOperationException("A shipment can only inherit from the order it despatches.");
        if (CommercialCaseId.HasValue && CommercialCaseId != order.CommercialCaseId)
            throw new InvalidOperationException("A shipment commercial identity cannot be replaced.");

        CommercialCaseId = order.CommercialCaseId;
        NexoraSerial = order.NexoraSerial;
    }

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
