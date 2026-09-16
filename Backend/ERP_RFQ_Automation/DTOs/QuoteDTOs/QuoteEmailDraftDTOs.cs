namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    /// <summary>
    /// The e-mail the customer would receive if the rep sent this quote right now, in plain text
    /// the rep can edit. Exactly the default <c>SendQuoteEmailAsync</c> composes when no custom
    /// subject or body is supplied — one composer, two readers — so the preview cannot drift from
    /// the send.
    /// </summary>
    public sealed class QuoteEmailDraftDTO
    {
        public long QuoteId { get; set; }
        public string QuoteNo { get; set; } = string.Empty;

        /// <summary>The customer's contact address on record, or null when there is none.</summary>
        public string? RecipientEmail { get; set; }

        public string Subject { get; set; } = string.Empty;

        /// <summary>Plain text; blank lines separate paragraphs. Rendered to HTML at send time.</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>The PDF that rides along, named so the rep knows what the customer opens.</summary>
        public string AttachmentFileName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Optional edits to the default e-mail, posted with the send. Absent or blank fields mean
    /// "use the default". The e2e and API callers that post no body at all keep working.
    /// </summary>
    public sealed class QuoteSendEmailRequestDTO
    {
        public string? CustomSubject { get; set; }
        public string? CustomBody { get; set; }
    }
}
