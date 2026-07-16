using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// Intent-level notification API for the RFQ → quote → order flow. Each method
    /// renders the appropriate template and dispatches it through the configured
    /// <see cref="IEmailSender"/>.
    ///
    /// Every method is resilient: a send failure is caught and logged and NEVER
    /// propagates, so a notification problem can never break the surrounding
    /// business transaction. Methods return <c>true</c> when the email was
    /// dispatched, <c>false</c> when it failed (and was swallowed).
    /// </summary>
    public interface INotificationService
    {
        /// <summary>Internal alert that an extracted document needs human review.</summary>
        Task<bool> NotifyLeadNeedsReviewAsync(LeadNeedsReviewNotification request, CancellationToken ct = default);

        /// <summary>Sends an RFQ invitation to a supplier.</summary>
        Task<bool> SendRfqToSupplierAsync(RfqToSupplierNotification request, CancellationToken ct = default);

        /// <summary>Delivers a prepared quotation to a buyer.</summary>
        Task<bool> SendQuoteToBuyerAsync(QuoteToBuyerNotification request, CancellationToken ct = default);

        /// <summary>Sends an order-confirmation email to a customer.</summary>
        Task<bool> SendOrderConfirmationAsync(OrderConfirmationNotification request, CancellationToken ct = default);
    }
}
