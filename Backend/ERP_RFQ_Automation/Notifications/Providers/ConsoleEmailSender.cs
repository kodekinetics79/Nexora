using System.Linq;
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

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            var to = string.Join(", ", message.To.Select(a => a.ToString()));
            var cc = string.Join(", ", message.Cc.Select(a => a.ToString()));
            var bcc = string.Join(", ", message.Bcc.Select(a => a.ToString()));

            _logger.LogInformation(
                "[ConsoleEmailSender] Email NOT sent (console provider). " +
                "Tenant={TenantId} BU={BusinessUnitId} From={From} To={To} Cc={Cc} Bcc={Bcc} " +
                "Subject=\"{Subject}\" Attachments={AttachmentCount}\n--- TEXT BODY ---\n{TextBody}",
                message.TenantId ?? "-",
                message.BusinessUnitId ?? "-",
                message.From?.ToString() ?? "(default)",
                string.IsNullOrEmpty(to) ? "-" : to,
                string.IsNullOrEmpty(cc) ? "-" : cc,
                string.IsNullOrEmpty(bcc) ? "-" : bcc,
                message.Subject,
                message.Attachments.Count,
                string.IsNullOrWhiteSpace(message.TextBody)
                    ? "(html only — see HtmlBody)"
                    : message.TextBody);

            return Task.CompletedTask;
        }
    }
}
