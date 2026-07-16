using System.Collections.Generic;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>Fields common to every notification request.</summary>
    public abstract class NotificationRequestBase
    {
        /// <summary>Recipient email address (required for a real send).</summary>
        public string ToEmail { get; set; } = string.Empty;

        /// <summary>Optional recipient display name.</summary>
        public string? ToName { get; set; }

        /// <summary>Tenant id for logging / correlation.</summary>
        public string? TenantId { get; set; }

        /// <summary>Business-unit id for logging / correlation.</summary>
        public string? BusinessUnitId { get; set; }

        /// <summary>
        /// Optional CTA target. If it is an absolute URL it is used as-is; otherwise
        /// it is treated as a path and combined with <c>Notifications:AppBaseUrl</c>.
        /// </summary>
        public string? CtaPath { get; set; }

        /// <summary>Optional attachments (e.g. a generated quote/order PDF).</summary>
        public List<EmailAttachment> Attachments { get; set; } = new();
    }

    /// <summary>Internal alert: an extracted document needs a human review.</summary>
    public sealed class LeadNeedsReviewNotification : NotificationRequestBase
    {
        public string ReviewerName { get; set; } = "team";
        public string DocumentName { get; set; } = string.Empty;
        public string Source { get; set; } = "Inbound email";
        public string ReceivedAt { get; set; } = string.Empty;
        public string Confidence { get; set; } = "n/a";
        public string Summary { get; set; } = "Please open Nexora to review and confirm the extracted details.";
    }

    /// <summary>Invitation to a supplier to quote against an RFQ.</summary>
    public sealed class RfqToSupplierNotification : NotificationRequestBase
    {
        public string SupplierName { get; set; } = string.Empty;
        public string BuyerCompany { get; set; } = "Nexora";
        public string RfqNumber { get; set; } = string.Empty;
        public string RfqTitle { get; set; } = string.Empty;
        public string ItemSummary { get; set; } = string.Empty;
        public string DueDate { get; set; } = string.Empty;
        public string Message { get; set; } = "Please submit your best pricing and lead times.";
    }

    /// <summary>Delivery of a prepared quotation to a buyer.</summary>
    public sealed class QuoteToBuyerNotification : NotificationRequestBase
    {
        public string BuyerName { get; set; } = string.Empty;
        public string SupplierCompany { get; set; } = "Nexora";
        public string QuoteNumber { get; set; } = string.Empty;
        public string RfqNumber { get; set; } = string.Empty;
        public string TotalAmount { get; set; } = string.Empty;
        public string ValidUntil { get; set; } = string.Empty;
        public string Message { get; set; } = "Please review the attached quotation and let us know if you'd like to proceed.";
    }

    /// <summary>Confirmation that an order was placed / accepted.</summary>
    public sealed class OrderConfirmationNotification : NotificationRequestBase
    {
        public string CustomerName { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public string OrderDate { get; set; } = string.Empty;
        public string ItemSummary { get; set; } = string.Empty;
        public string TotalAmount { get; set; } = string.Empty;
        public string EstimatedDelivery { get; set; } = "To be confirmed";
        public string Message { get; set; } = "Thank you for your business. We'll notify you as your order progresses.";
    }
}
