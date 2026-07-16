using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications.Providers
{
    /// <summary>
    /// SMTP sender built on the framework's <see cref="System.Net.Mail.SmtpClient"/>
    /// so it works today against any SMTP host with NO extra NuGet dependency.
    /// Host / port / credentials / from are read from <see cref="NotificationsOptions"/>.
    /// </summary>
    public sealed class SmtpEmailSender : IEmailSender
    {
        private readonly NotificationsOptions _options;
        private readonly ILogger<SmtpEmailSender> _logger;

        public SmtpEmailSender(IOptions<NotificationsOptions> options, ILogger<SmtpEmailSender> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            var smtp = _options.Smtp;
            if (string.IsNullOrWhiteSpace(smtp.Host))
                throw new InvalidOperationException(
                    "SMTP host is not configured (Notifications:Smtp:Host). Cannot send email.");

            using var mail = BuildMailMessage(message);

            using var client = new SmtpClient(smtp.Host, smtp.Port > 0 ? smtp.Port : 587)
            {
                EnableSsl = smtp.EnableSsl,
                Timeout = smtp.TimeoutMs > 0 ? smtp.TimeoutMs : 30000,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(smtp.Username))
                client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);

            _logger.LogInformation(
                "[SmtpEmailSender] Sending email via {Host}:{Port} Tenant={TenantId} BU={BusinessUnitId} Subject=\"{Subject}\"",
                smtp.Host, client.Port, message.TenantId ?? "-", message.BusinessUnitId ?? "-", message.Subject);

            // SmtpClient.SendMailAsync does not accept a CancellationToken on net8;
            // honor cancellation cooperatively before the (potentially blocking) send.
            ct.ThrowIfCancellationRequested();
            await client.SendMailAsync(mail).ConfigureAwait(false);

            _logger.LogInformation(
                "[SmtpEmailSender] Email sent Tenant={TenantId} Subject=\"{Subject}\"",
                message.TenantId ?? "-", message.Subject);
        }

        private MailMessage BuildMailMessage(EmailMessage message)
        {
            var from = message.From is not null
                ? new MailAddress(message.From.Address, message.From.DisplayName)
                : new MailAddress(_options.FromAddress, _options.FromName);

            var mail = new MailMessage
            {
                From = from,
                Subject = message.Subject,
                Body = string.IsNullOrWhiteSpace(message.HtmlBody) ? (message.TextBody ?? string.Empty) : message.HtmlBody,
                IsBodyHtml = !string.IsNullOrWhiteSpace(message.HtmlBody)
            };

            foreach (var to in message.To)
                mail.To.Add(new MailAddress(to.Address, to.DisplayName));
            foreach (var cc in message.Cc)
                mail.CC.Add(new MailAddress(cc.Address, cc.DisplayName));
            foreach (var bcc in message.Bcc)
                mail.Bcc.Add(new MailAddress(bcc.Address, bcc.DisplayName));

            var replyTo = message.ReplyTo
                ?? (string.IsNullOrWhiteSpace(_options.ReplyToAddress) ? null : new EmailAddress(_options.ReplyToAddress!));
            if (replyTo is not null)
                mail.ReplyToList.Add(new MailAddress(replyTo.Address, replyTo.DisplayName));

            // Provide both HTML and plain-text alternate views when both are present,
            // so non-HTML clients get the readable fallback.
            if (!string.IsNullOrWhiteSpace(message.HtmlBody) && !string.IsNullOrWhiteSpace(message.TextBody))
            {
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    message.TextBody, null, "text/plain"));
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    message.HtmlBody, null, "text/html"));
            }

            foreach (var att in message.Attachments)
            {
                var stream = new MemoryStream(att.Content);
                mail.Attachments.Add(new Attachment(stream, att.FileName, att.ContentType));
            }

            return mail;
        }
    }
}
