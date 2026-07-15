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
        public string? Notes { get; set; }
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
