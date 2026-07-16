using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// Provider-agnostic transport for a single email. Implementations wrap a
    /// concrete delivery mechanism (SMTP, SendGrid HTTP API, console/log). The
    /// active implementation is selected by <c>Notifications:Provider</c>.
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>
        /// Delivers <paramref name="message"/>. Implementations should throw on a
        /// hard failure; the resilient wrapping (catch/log so a notification never
        /// breaks a business transaction) lives in <see cref="INotificationService"/>.
        /// </summary>
        Task SendAsync(EmailMessage message, CancellationToken ct = default);
    }
}
