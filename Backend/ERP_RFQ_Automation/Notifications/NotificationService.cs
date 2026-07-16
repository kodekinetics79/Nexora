using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Notifications.Templating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// Default <see cref="INotificationService"/>. Renders the relevant embedded
    /// template, builds an <see cref="EmailMessage"/>, and dispatches it through the
    /// configured <see cref="IEmailSender"/>. All sends are wrapped in try/catch so a
    /// notification failure is logged but never breaks the caller's transaction.
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        private readonly IEmailSender _emailSender;
        private readonly IEmailTemplateRenderer _renderer;
        private readonly NotificationsOptions _options;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            IEmailSender emailSender,
            IEmailTemplateRenderer renderer,
            IOptions<NotificationsOptions> options,
            ILogger<NotificationService> logger)
        {
            _emailSender = emailSender;
            _renderer = renderer;
            _options = options.Value;
            _logger = logger;
        }

        public Task<bool> NotifyLeadNeedsReviewAsync(LeadNeedsReviewNotification request, CancellationToken ct = default)
        {
            var model = new Dictionary<string, string?>
            {
                ["reviewerName"] = request.ReviewerName,
                ["documentName"] = request.DocumentName,
                ["source"] = request.Source,
                ["receivedAt"] = request.ReceivedAt,
                ["confidence"] = request.Confidence,
                ["summary"] = request.Summary,
                ["ctaUrl"] = ResolveCta(request.CtaPath),
                ["ctaLabel"] = "Review document"
            };
            return DispatchAsync(EmailTemplates.LeadNeedsReview, request, model, ct);
        }

        public Task<bool> SendRfqToSupplierAsync(RfqToSupplierNotification request, CancellationToken ct = default)
        {
            var model = new Dictionary<string, string?>
            {
                ["supplierName"] = request.SupplierName,
                ["buyerCompany"] = request.BuyerCompany,
                ["rfqNumber"] = request.RfqNumber,
                ["rfqTitle"] = request.RfqTitle,
                ["itemSummary"] = request.ItemSummary,
                ["dueDate"] = request.DueDate,
                ["message"] = request.Message,
                ["ctaUrl"] = ResolveCta(request.CtaPath),
                ["ctaLabel"] = "Submit quotation"
            };
            return DispatchAsync(EmailTemplates.RfqToSupplier, request, model, ct);
        }

        public Task<bool> SendQuoteToBuyerAsync(QuoteToBuyerNotification request, CancellationToken ct = default)
        {
            var model = new Dictionary<string, string?>
            {
                ["buyerName"] = request.BuyerName,
                ["supplierCompany"] = request.SupplierCompany,
                ["quoteNumber"] = request.QuoteNumber,
                ["rfqNumber"] = request.RfqNumber,
                ["totalAmount"] = request.TotalAmount,
                ["validUntil"] = request.ValidUntil,
                ["message"] = request.Message,
                ["ctaUrl"] = ResolveCta(request.CtaPath),
                ["ctaLabel"] = "View quotation"
            };
            return DispatchAsync(EmailTemplates.QuoteToBuyer, request, model, ct);
        }

        public Task<bool> SendOrderConfirmationAsync(OrderConfirmationNotification request, CancellationToken ct = default)
        {
            var model = new Dictionary<string, string?>
            {
                ["customerName"] = request.CustomerName,
                ["orderNumber"] = request.OrderNumber,
                ["orderDate"] = request.OrderDate,
                ["itemSummary"] = request.ItemSummary,
                ["totalAmount"] = request.TotalAmount,
                ["estimatedDelivery"] = request.EstimatedDelivery,
                ["message"] = request.Message,
                ["ctaUrl"] = ResolveCta(request.CtaPath),
                ["ctaLabel"] = "Track order"
            };
            return DispatchAsync(EmailTemplates.OrderConfirmation, request, model, ct);
        }

        /// <summary>
        /// Renders, builds, and sends. Any exception (render or transport) is logged
        /// and swallowed so the calling business transaction is never affected.
        /// </summary>
        private async Task<bool> DispatchAsync(
            string templateName,
            NotificationRequestBase request,
            IDictionary<string, string?> model,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.ToEmail))
            {
                _logger.LogWarning(
                    "[NotificationService] Skipping '{Template}' — no recipient email. Tenant={TenantId} BU={BusinessUnitId}",
                    templateName, request.TenantId ?? "-", request.BusinessUnitId ?? "-");
                return false;
            }

            try
            {
                var rendered = _renderer.Render(templateName, model);

                var message = new EmailMessage
                {
                    Subject = rendered.Subject,
                    HtmlBody = rendered.HtmlBody,
                    TextBody = rendered.TextBody,
                    TenantId = request.TenantId,
                    BusinessUnitId = request.BusinessUnitId,
                    Attachments = request.Attachments
                };
                message.AddTo(request.ToEmail, request.ToName);

                await _emailSender.SendAsync(message, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "[NotificationService] Dispatched '{Template}' to {To}. Tenant={TenantId} BU={BusinessUnitId}",
                    templateName, request.ToEmail, request.TenantId ?? "-", request.BusinessUnitId ?? "-");
                return true;
            }
            catch (Exception ex)
            {
                // Never rethrow: a notification failure must not break the business flow.
                _logger.LogError(ex,
                    "[NotificationService] Failed to send '{Template}' to {To}. Tenant={TenantId} BU={BusinessUnitId}. Notification suppressed.",
                    templateName, request.ToEmail, request.TenantId ?? "-", request.BusinessUnitId ?? "-");
                return false;
            }
        }

        /// <summary>
        /// Builds an absolute CTA URL. Absolute inputs pass through unchanged; a
        /// relative path is combined with <c>Notifications:AppBaseUrl</c>. When no
        /// path is supplied the app base URL is used.
        /// </summary>
        private string ResolveCta(string? ctaPath)
        {
            var baseUrl = string.IsNullOrWhiteSpace(_options.AppBaseUrl)
                ? "https://app.nexora.local"
                : _options.AppBaseUrl.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(ctaPath))
                return baseUrl;

            if (Uri.TryCreate(ctaPath, UriKind.Absolute, out var abs)
                && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                return ctaPath;

            return $"{baseUrl}/{ctaPath.TrimStart('/')}";
        }
    }
}
