using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications.Providers
{
    /// <summary>
    /// SendGrid sender implemented as a raw call to the v3 <c>/v3/mail/send</c> JSON
    /// endpoint over <see cref="HttpClient"/> (from <see cref="IHttpClientFactory"/>),
    /// so NO SendGrid NuGet package is required. API key from configuration.
    /// </summary>
    public sealed class SendGridEmailSender : IEmailSender
    {
        /// <summary>Named <see cref="HttpClient"/> registered in AddNotifications.</summary>
        public const string HttpClientName = "NotificationsSendGrid";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly NotificationsOptions _options;
        private readonly ILogger<SendGridEmailSender> _logger;

        public SendGridEmailSender(
            IHttpClientFactory httpClientFactory,
            IOptions<NotificationsOptions> options,
            ILogger<SendGridEmailSender> logger)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            var sg = _options.SendGrid;
            if (string.IsNullOrWhiteSpace(sg.ApiKey))
                throw new InvalidOperationException(
                    "SendGrid API key is not configured (Notifications:SendGrid:ApiKey). Cannot send email.");

            var payload = BuildPayload(message);
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var baseUrl = string.IsNullOrWhiteSpace(sg.ApiBaseUrl) ? "https://api.sendgrid.com" : sg.ApiBaseUrl.TrimEnd('/');

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/mail/send")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sg.ApiKey);
            var requestReference = Guid.NewGuid().ToString("N");
            request.Headers.TryAddWithoutValidation("X-Nexora-Dispatch-Id", requestReference);

            _logger.LogInformation(
                "[SendGridEmailSender] Sending email Tenant={TenantId} BU={BusinessUnitId} Subject=\"{Subject}\"",
                message.TenantId ?? "-", message.BusinessUnitId ?? "-", message.Subject);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"SendGrid send failed with status {(int)response.StatusCode} {response.ReasonPhrase}.");

            _logger.LogInformation(
                "[SendGridEmailSender] Email accepted by SendGrid (status {Status}) Tenant={TenantId} Subject=\"{Subject}\"",
                (int)response.StatusCode, message.TenantId ?? "-", message.Subject);
            var providerReference = response.Headers.TryGetValues("X-Message-Id", out var messageIds)
                ? messageIds.FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(providerReference))
            {
                _logger.LogWarning(
                    "[SendGridEmailSender] SendGrid accepted the request but returned no X-Message-Id. Tenant={TenantId}",
                    message.TenantId ?? "-");
                return null;
            }
            return new EmailDeliveryReceipt(
                "sendgrid",
                providerReference,
                DateTimeOffset.UtcNow);
        }

        private object BuildPayload(EmailMessage message)
        {
            var fromAddress = message.From?.Address ?? _options.FromAddress;
            var fromName = message.From?.DisplayName ?? _options.FromName;

            var personalization = new Dictionary<string, object>
            {
                ["to"] = message.To.Select(ToRecipient).ToArray()
            };
            if (message.Cc.Count > 0)
                personalization["cc"] = message.Cc.Select(ToRecipient).ToArray();
            if (message.Bcc.Count > 0)
                personalization["bcc"] = message.Bcc.Select(ToRecipient).ToArray();

            var content = new List<object>();
            if (!string.IsNullOrWhiteSpace(message.TextBody))
                content.Add(new { type = "text/plain", value = message.TextBody });
            if (!string.IsNullOrWhiteSpace(message.HtmlBody))
                content.Add(new { type = "text/html", value = message.HtmlBody });
            if (content.Count == 0)
                content.Add(new { type = "text/plain", value = string.Empty });

            var payload = new Dictionary<string, object?>
            {
                ["personalizations"] = new[] { personalization },
                ["from"] = ToRecipient(new EmailAddress(fromAddress, fromName)),
                ["subject"] = message.Subject,
                ["content"] = content
            };

            var replyTo = message.ReplyTo
                ?? (string.IsNullOrWhiteSpace(_options.ReplyToAddress) ? null : new EmailAddress(_options.ReplyToAddress!));
            if (replyTo is not null)
                payload["reply_to"] = ToRecipient(replyTo);

            if (message.Attachments.Count > 0)
            {
                payload["attachments"] = message.Attachments.Select(a => new
                {
                    content = Convert.ToBase64String(a.Content),
                    filename = a.FileName,
                    type = a.ContentType,
                    disposition = "attachment"
                }).ToArray();
            }

            return payload;
        }

        private static object ToRecipient(EmailAddress address) =>
            string.IsNullOrWhiteSpace(address.DisplayName)
                ? new { email = address.Address }
                : new { email = address.Address, name = address.DisplayName };
    }
}
