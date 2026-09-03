using System.Collections.Generic;

namespace ERP_RFQ_Automation.Notifications
{
    /// <summary>
    /// A single email recipient (address + optional display name).
    /// </summary>
    public sealed class EmailAddress
    {
        public EmailAddress() { }

        public EmailAddress(string address, string? displayName = null)
        {
            Address = address;
            DisplayName = displayName;
        }

        /// <summary>RFC 5322 email address (e.g. "buyer@acme.com").</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Optional friendly name (e.g. "Acme Purchasing").</summary>
        public string? DisplayName { get; set; }

        public override string ToString() =>
            string.IsNullOrWhiteSpace(DisplayName) ? Address : $"{DisplayName} <{Address}>";
    }

    /// <summary>
    /// An in-memory file attachment. Content is held as bytes so the module has
    /// no dependency on the filesystem or a particular storage provider.
    /// </summary>
    public sealed class EmailAttachment
    {
        public EmailAttachment() { }

        public EmailAttachment(string fileName, byte[] content, string contentType = "application/octet-stream")
        {
            FileName = fileName;
            Content = content;
            ContentType = contentType;
        }

        public string FileName { get; set; } = string.Empty;
        public byte[] Content { get; set; } = System.Array.Empty<byte>();
        public string ContentType { get; set; } = "application/octet-stream";
    }

    /// <summary>
    /// Provider-agnostic representation of a single outbound email. Constructed by
    /// <see cref="INotificationService"/> (via the template renderer) and handed to
    /// whichever <see cref="IEmailSender"/> the configuration selected.
    /// </summary>
    public sealed class EmailMessage
    {
        /// <summary>Primary recipients. At least one is required for a real send.</summary>
        public List<EmailAddress> To { get; set; } = new();

        public List<EmailAddress> Cc { get; set; } = new();

        public List<EmailAddress> Bcc { get; set; } = new();

        /// <summary>
        /// Optional explicit sender. When null, the provider falls back to the
        /// configured default From address (<see cref="NotificationsOptions.FromAddress"/>).
        /// </summary>
        public EmailAddress? From { get; set; }

        /// <summary>Optional Reply-To override.</summary>
        public EmailAddress? ReplyTo { get; set; }

        public string Subject { get; set; } = string.Empty;

        /// <summary>Rendered HTML body (primary content).</summary>
        public string HtmlBody { get; set; } = string.Empty;

        /// <summary>Plain-text fallback body for clients that do not render HTML.</summary>
        public string? TextBody { get; set; }

        public List<EmailAttachment> Attachments { get; set; } = new();

        /// <summary>
        /// Tenant identifier for logging / correlation. Never affects delivery;
        /// purely to make sent mail traceable per tenant in a multi-tenant deploy.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>Business-unit identifier for logging / correlation.</summary>
        public string? BusinessUnitId { get; set; }

        /// <summary>
        /// The tenant whose VERIFIED outbound sender this message must leave from, or null for
        /// system mail (password resets, platform-issued invitations) that leaves from the
        /// platform address.
        ///
        /// <para>Deliberately a separate field from <see cref="BusinessUnitId"/>. That one is
        /// documented as "never affects delivery" and every caller wrote it on that basis; making
        /// it silently choose the sender would change the meaning of a correlation id under
        /// every existing call site. A caller that forgets to set this one gets the platform
        /// sender, which is the safe direction — never another tenant's mailbox.</para>
        ///
        /// <para>Resolved by <c>IOutboundSenderResolver</c>: the tenant's active SMTP row in
        /// <c>Email_Configurations</c> when it has one, the platform configuration otherwise.
        /// Issue #54.</para>
        /// </summary>
        public long? OwningBusinessUnitId { get; set; }

        public EmailMessage AddTo(string address, string? displayName = null)
        {
            if (!string.IsNullOrWhiteSpace(address))
                To.Add(new EmailAddress(address, displayName));
            return this;
        }
    }
}
