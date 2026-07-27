using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Notifications.Providers
{
    /// <summary>
    /// The safe DEFAULT sender. Instead of transmitting anything, it logs the
    /// fully-rendered email. Use this in dev/pilot until the owner provisions a
    /// real provider (SMTP or SendGrid) and sets <c>Notifications:Provider</c>.
    /// </summary>
    public sealed class ConsoleEmailSender : IEmailSender
    {
        private readonly ILogger<ConsoleEmailSender> _logger;

        public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
        {
            _logger = logger;
        }

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            _logger.LogInformation(
                "[ConsoleEmailSender] Email NOT sent (console provider). " +
                "Tenant={TenantId} BU={BusinessUnitId} ToCount={ToCount} CcCount={CcCount} " +
                "BccCount={BccCount} Attachments={AttachmentCount}",
                message.TenantId ?? "-",
                message.BusinessUnitId ?? "-",
                message.To.Count,
                message.Cc.Count,
                message.Bcc.Count,
                message.Attachments.Count);

            return Task.FromResult<EmailDeliveryReceipt?>(null);
        }
    }
}
