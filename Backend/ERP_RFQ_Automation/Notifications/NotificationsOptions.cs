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
        /// Non-production containment for outbound mail. Defaults to Live (no containment), so
        /// binding this section changes nothing for an existing deployment. See
        /// <see cref="OutboundEmailGuardOptions"/> — set Mode to Redirect or AllowListOnly for
        /// any rehearsal that runs against real supplier records.
        /// </summary>
        public OutboundEmailGuardOptions OutboundGuard { get; set; } = new();

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

                warnings.AddRange(ValidateSenderAlignment());
            }
            else if (IsSendGrid)
            {
                if (string.IsNullOrWhiteSpace(SendGrid.ApiKey))
                    warnings.Add("Notifications:Provider is 'sendgrid' but Notifications:SendGrid:ApiKey is empty. SendGrid sends will fail.");
            }

            warnings.AddRange(OutboundGuard.Validate());

            // The dangerous combination, stated plainly: a real transport with no containment.
            // Non-fatal by design — this is exactly the production configuration — but it must
            // never be the silent default of a rehearsal run.
            if ((IsSmtp || IsSendGrid) && OutboundGuard.ResolvedMode == OutboundEmailMode.Live)
                warnings.Add(
                    $"Notifications:Provider is '{Provider}' with OutboundGuard:Mode=Live — mail will reach REAL recipients. " +
                    "For any pilot or rehearsal set Notifications:OutboundGuard:Mode to Redirect or AllowListOnly.");

            return warnings;
        }

        /// <summary>
        /// The From address and the authenticated mailbox must be the same address on a
        /// mailbox-hosting provider.
        ///
        /// <para><b>Why this is a warning and not a footnote.</b> Sending activation links as
        /// <c>info@</c> while authenticating as <c>zack@</c> is the natural thing to configure —
        /// one is the address customers should see, the other is the mailbox whose password you
        /// have. GoDaddy, Microsoft 365, Google Workspace and Zoho all refuse it, and the refusal
        /// arrives as a bare SMTP 5xx during the send, long after the settings screen said
        /// everything was fine. Every provisioned tenant would report success and no founding
        /// administrator would receive anything.</para>
        ///
        /// <para><b>Only for hosts the catalogue recognises as mailbox providers.</b> A relay is
        /// SUPPOSED to send as addresses other than its username — SendGrid's username is the
        /// literal string "apikey" — so warning there would be wrong, and warning on an
        /// unrecognised host would be a guess. Both would teach the operator to dismiss the
        /// banner, which costs more than the warning is worth.</para>
        /// </summary>
        private IReadOnlyList<string> ValidateSenderAlignment()
        {
            if (string.IsNullOrWhiteSpace(FromAddress) || string.IsNullOrWhiteSpace(Smtp.Username))
                return [];
            if (string.Equals(FromAddress.Trim(), Smtp.Username.Trim(), System.StringComparison.OrdinalIgnoreCase))
                return [];

            var provider = Email.EmailProviderCatalog.Find(
                Email.EmailProviderCatalog.InferKeyFromHost(Smtp.Host));
            if (provider is not { RequiresSenderMatchesMailbox: true })
                return [];

            return
            [
                $"The From address ({FromAddress.Trim()}) is not the mailbox being signed in as "
                + $"({Smtp.Username.Trim()}). {provider.DisplayName} hosts mailboxes rather than "
                + "relaying for a domain, so it will reject a message claiming to be from a "
                + "different address — the connection will test clean and every send will still "
                + "fail. Either send as the mailbox you authenticate as, or set that mailbox up as "
                + "an alias of the From address at the provider first."
            ];
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
