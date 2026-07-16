using System.Collections.Generic;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// Strongly-typed configuration for the notifications module, bound from the
    /// <c>Notifications</c> configuration section.
    /// </summary>
    public sealed class NotificationsOptions
    {
        public const string SectionName = "Notifications";

        /// <summary>
        /// Active provider: "console" (default, safe for dev/pilot — logs instead of
        /// sending), "smtp", or "sendgrid".
        /// </summary>
        public string Provider { get; set; } = "console";

        /// <summary>Default sender address used when a message does not specify From.</summary>
        public string FromAddress { get; set; } = "no-reply@nexora.local";

        /// <summary>Default sender display name.</summary>
        public string FromName { get; set; } = "Nexora";

        /// <summary>Optional default Reply-To address applied when a message sets none.</summary>
        public string? ReplyToAddress { get; set; }

        /// <summary>
        /// Base URL used to build absolute CTA links inside templates
        /// (e.g. "https://app.nexora.com"). No trailing slash required.
        /// </summary>
        public string AppBaseUrl { get; set; } = "https://app.nexora.local";

        public SmtpOptions Smtp { get; set; } = new();

        public SendGridOptions SendGrid { get; set; } = new();

        /// <summary>
        /// True when the configured provider is "smtp" (case-insensitive).
        /// </summary>
        public bool IsSmtp => string.Equals(Provider, "smtp", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when the configured provider is "sendgrid" (case-insensitive).
        /// </summary>
        public bool IsSendGrid => string.Equals(Provider, "sendgrid", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Validates the bound options and returns human-readable warnings for a
        /// misconfigured-but-non-fatal state (e.g. provider=smtp with no host). An
        /// empty list means the configuration looks complete for the chosen provider.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(FromAddress))
                warnings.Add("Notifications:FromAddress is empty; using a placeholder sender.");

            if (IsSmtp)
            {
                if (string.IsNullOrWhiteSpace(Smtp.Host))
                    warnings.Add("Notifications:Provider is 'smtp' but Notifications:Smtp:Host is empty. SMTP sends will fail.");
                if (Smtp.Port <= 0)
                    warnings.Add("Notifications:Smtp:Port is not set; defaulting to 587.");
            }
            else if (IsSendGrid)
            {
                if (string.IsNullOrWhiteSpace(SendGrid.ApiKey))
                    warnings.Add("Notifications:Provider is 'sendgrid' but Notifications:SendGrid:ApiKey is empty. SendGrid sends will fail.");
            }

            return warnings;
        }
    }

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 587;
        public string? Username { get; set; }
        public string? Password { get; set; }

        /// <summary>Enable STARTTLS / SSL. Recommended true for any real host.</summary>
        public bool EnableSsl { get; set; } = true;

        /// <summary>Send timeout in milliseconds.</summary>
        public int TimeoutMs { get; set; } = 30000;
    }

    public sealed class SendGridOptions
    {
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>v3 mail/send endpoint. Overridable for EU data residency.</summary>
        public string ApiBaseUrl { get; set; } = "https://api.sendgrid.com";
    }
}
