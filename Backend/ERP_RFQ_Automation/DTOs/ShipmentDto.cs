using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs
{
    public class CreateShipmentDto
    {
        public string? ShipmentNo { get; set; }
        public long OrderId { get; set; }
        public long BusinessUnitId { get; set; }
        public long StatusId { get; set; }
        public DateTime ShipmentDate { get; set; }
        public DateTime? EstimatedDeliveryDate { get; set; }
        public string? Carrier { get; set; }
        public string? ServiceLevel { get; set; }
        public string? TrackingNumber { get; set; }
        public string? ExternalId { get; set; }
        public decimal? ShippingCost { get; set; }
        public string? LabelUrl { get; set; }
        public string? ShippingAddress { get; set; }

        /// <summary>
        /// FR-DLM-01. The governed region mapping for the delivery address, as a city id from the
        /// tenant's own <c>SetCity</c> master. Optional: refusing to despatch because a tenant has
        /// not populated its city reference table would let an empty master stop the warehouse. The
        /// delivery note states the absence rather than guessing a region from the free-text
        /// address.
        /// </summary>
        public int? DeliveryCityId { get; set; }

        public string? Notes { get; set; }

        /// <summary>
        /// FR-MTR-02. The despatch supervisor's written acceptance of a lapsed certificate on
        /// material going out on this note. Required only when a lot being issued holds an expired
        /// certificate; supplying one when every lot is in date is refused, so it cannot become a
        /// field the UI fills in by habit. The reason and the actor are kept on the lot
        /// declaration permanently and show on both traces.
        /// </summary>
        public string? ComplianceOverrideReason { get; set; }

        public List<CreateShipmentItemDto> Items { get; set; } = new List<CreateShipmentItemDto>();
    }

    public class CreateShipmentItemDto
    {
        public long OrderItemId { get; set; }
        public decimal Quantity { get; set; }
        public string? Notes { get; set; }
    }

    public class UpdateShipmentDto
    {
        public long StatusId { get; set; }
        public DateTime? ActualDeliveryDate { get; set; }
        public string? TrackingNumber { get; set; }
        public string? Notes { get; set; }
    }

    public class ShipmentDto
    {
        public long Id { get; set; }
        public string ShipmentNo { get; set; } = null!;
        public long OrderId { get; set; }
        public string OrderNo { get; set; } = null!;
        public long StatusId { get; set; }
        public string Status { get; set; } = null!;
        public DateTime ShipmentDate { get; set; }
        public DateTime? EstimatedDeliveryDate { get; set; }
        public DateTime? ActualDeliveryDate { get; set; }
        public string? Carrier { get; set; }
        public string? ServiceLevel { get; set; }
        public string? TrackingNumber { get; set; }
        public string? ExternalId { get; set; }
        public decimal? ShippingCost { get; set; }
        public string? LabelUrl { get; set; }
        public string? ShippingAddress { get; set; }

        /// <summary>
        /// FR-DLM-05. The governed lifecycle: SCHEDULED, DISPATCHED, IN_TRANSIT, DELIVERED,
        /// DELIVERY_EXCEPTION or CANCELLED. Distinct from <see cref="Status"/>, which is the
        /// tenant's own picklist label and is constrained by nothing.
        /// </summary>
        public string DeliveryStatus { get; set; } = null!;

        public DateTime? DeliveryStatusChangedOn { get; set; }
        public string? DeliveryStatusChangedBy { get; set; }

        /// <summary>FR-DLM-01. Governed region mapping; null means not mapped, and screens say so.</summary>
        public int? DeliveryCityId { get; set; }
        public string? DeliveryCityName { get; set; }
        public string? DeliveryRegionName { get; set; }

        public string? Notes { get; set; }
        public List<ShipmentItemDto> Items { get; set; } = new List<ShipmentItemDto>();
        public List<ShipmentStatusHistoryDto> StatusHistory { get; set; } = new List<ShipmentStatusHistoryDto>();
    }

    public class ShipmentStatusHistoryDto
    {
        public long Id { get; set; }
        public long? PreviousStatusId { get; set; }
        public string? PreviousStatus { get; set; }
        public long NewStatusId { get; set; }
        public string NewStatus { get; set; } = null!;
        public string ChangedBy { get; set; } = null!;
        public DateTime ChangedOn { get; set; }
        public string? Notes { get; set; }
    }

    public class ShipmentItemDto
    {
        public long Id { get; set; }
        public long OrderItemId { get; set; }
        public string ProductName { get; set; } = null!;
        public decimal Quantity { get; set; }
        public string? Notes { get; set; }
    }
}
