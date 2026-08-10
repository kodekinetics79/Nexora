using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs
{
    public class CreateOrderDto
    {
        public string? OrderNo { get; set; }
        public long? QuoteId { get; set; }
        public long? LeadId { get; set; }
        public long? RfqId { get; set; }
        public long CustomerId { get; set; }
        public long BusinessUnitId { get; set; }
        public long? CurrencyId { get; set; }
        public long? WarehouseId { get; set; } 

        // Payment Info
        public long? PaymentMethodId { get; set; }
        public long? PaymentStatusId { get; set; }
        public DateTime? PaymentDate { get; set; }
        public decimal PaidAmount { get; set; }
        public string? PaymentReference { get; set; }

        public DateTime OrderDate { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string? TermsAndConditions { get; set; }
        public string? Notes { get; set; }
        public List<CreateOrderItemDto> Items { get; set; } = new List<CreateOrderItemDto>();
    }

    public class CreateOrderItemDto
    {
        public long ProductId { get; set; }
        public string? Description { get; set; }
        [Range(double.Epsilon, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal Quantity { get; set; }
        [Range(double.Epsilon, double.MaxValue, ErrorMessage = "Unit price must be greater than zero.")]
        public decimal UnitPrice { get; set; }
        [Range(0, double.MaxValue, ErrorMessage = "Discount cannot be negative.")]
        public decimal Discount { get; set; }
        [Range(0, double.MaxValue, ErrorMessage = "Tax cannot be negative.")]
        public decimal TaxAmount { get; set; }
        public int? UomId { get; set; }
        public long? WarehouseId { get; set; }
    }

    public class UpdateOrderDto
    {
        public long StatusId { get; set; }
        public long? PaymentMethodId { get; set; }
        public long? PaymentStatusId { get; set; }
        public DateTime? PaymentDate { get; set; }
        public decimal PaidAmount { get; set; }
        public string? PaymentReference { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string? Notes { get; set; }
    }

    public class OrderDto
    {
        public long Id { get; set; }
        public string OrderNo { get; set; } = null!;
        public long CustomerId { get; set; }
        public long? CommercialCaseId { get; set; }
        public string? NexoraSerial { get; set; }
        public long? ContactId { get; set; }
        public string CustomerName { get; set; } = null!;
        public long? QuoteId { get; set; }
        public string? QuoteNo { get; set; }
        public long? RfqId { get; set; }
        public string? RfqNo { get; set; }
        public long? LeadId { get; set; }
        public string? LeadNo { get; set; }
        public long StatusId { get; set; }
        public string Status { get; set; } = null!;
        public long? PaymentStatusId { get; set; }
        public string PaymentStatus { get; set; } = null!;
        public long? PaymentMethodId { get; set; }

        /// <summary>
        /// The currency every monetary figure on this DTO is denominated in.
        /// <c>Order.CurrencyId</c> has always been persisted, but no order DTO exposed it, so the
        /// Orders and Shipments screens had no way to render an amount with its currency and were
        /// left implying one. Null means the order itself has no currency recorded — render it as
        /// unknown rather than defaulting to a house currency.
        /// </summary>
        public long? CurrencyId { get; set; }

        /// <summary>ISO code of <see cref="CurrencyId"/>, e.g. "SAR". Null when unset.</summary>
        public string? CurrencyCode { get; set; }

        public decimal TotalAmount { get; set; }
        public decimal SubTotal { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal BalanceAmount { get; set; }
        public DateTime OrderDate { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string? Notes { get; set; }
        public string? TermsAndConditions { get; set; }
        public bool HasShipments { get; set; }
        public List<OrderItemDto> Items { get; set; } = new List<OrderItemDto>();
    }

    public class OrderItemDto
    {
        public long Id { get; set; }
        public long ProductId { get; set; }
        public string ProductName { get; set; } = null!;
        public string? Description { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Discount { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public int? UomId { get; set; }
        public long? WarehouseId { get; set; }
    }

    public class InvoiceDto
    {
        public long OrderId { get; set; }
        public string OrderNo { get; set; } = null!;
        public long? CommercialCaseId { get; set; }
        public string? NexoraSerial { get; set; }
        public DateTime OrderDate { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string Status { get; set; } = null!;
        public string? QuoteNo { get; set; }
        public string? RfqNo { get; set; }
        public string? LeadNo { get; set; }
        public string CustomerName { get; set; } = null!;
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }
        public string? CustomerAddress { get; set; }

        /// <summary>Denomination of every figure below. See <see cref="OrderDto.CurrencyCode"/>.</summary>
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }

        public decimal SubTotal { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string? TermsAndConditions { get; set; }
        public string? Notes { get; set; }
        public List<InvoiceItemDto> Items { get; set; } = new List<InvoiceItemDto>();
    }

    public class InvoiceItemDto
    {
        public string ProductName { get; set; } = null!;
        public string Description { get; set; } = null!;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
    }
}
