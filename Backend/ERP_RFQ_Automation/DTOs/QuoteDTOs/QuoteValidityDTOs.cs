using System;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    /// <summary>
    /// What a rep submits to hold an already-issued quote's price open for longer
    /// (Decision Register R7).
    /// </summary>
    public sealed class QuoteExtendValidityRequestDTO
    {
        /// <summary>The new date the customer is being held to. Must be in the future and
        /// later than the validity date the quote currently carries.</summary>
        [Required]
        public DateTime? ValidUntil { get; set; }

        /// <summary>
        /// Why. Mandatory free text — R7 requires the reason to be recorded and readable,
        /// so a blank or whitespace-only reason is refused rather than silently stored.
        /// </summary>
        [Required]
        [StringLength(500, MinimumLength = 1)]
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>One recorded validity move, as shown to the rep and to anyone reviewing the bid.</summary>
    public sealed class QuoteValidityExtensionDTO
    {
        public long Id { get; set; }
        public long QuoteId { get; set; }
        public DateTime? PreviousValidUntil { get; set; }
        public DateTime NewValidUntil { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string ExtendedBy { get; set; } = string.Empty;
        public DateTime ExtendedOn { get; set; }
    }

    /// <summary>
    /// Result of an extend command: the quote's validity facts after it, plus the row that
    /// was recorded. <see cref="RevisionNo"/> is echoed deliberately — extending must never
    /// bump it, and a caller (or a test) can prove that from the response alone.
    /// </summary>
    public sealed class QuoteValidityExtensionResultDTO
    {
        public long QuoteId { get; set; }
        public string QuoteNo { get; set; } = string.Empty;
        public DateTime? ValidUntil { get; set; }
        public DateTime? ValidityExtendedOn { get; set; }

        /// <summary>Unchanged by this operation. The commercial offer is the same offer.</summary>
        public int RevisionNo { get; set; }

        /// <summary>true when the command was a replay of one already recorded under the same key.</summary>
        public bool Replayed { get; set; }

        public QuoteValidityExtensionDTO? Extension { get; set; }
    }
}
