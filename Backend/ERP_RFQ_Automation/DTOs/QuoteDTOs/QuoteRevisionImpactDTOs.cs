using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    /// <summary>
    /// An open customer revision on a quote, in the words the rep needs: which revision arrived,
    /// which one the quote was built from, and what changed on each line.
    ///
    /// <para>Until this existed the quote detail carried only <see cref="QuoteResponseDTO.RevisionImpact"/>
    /// (the impact type string), so the screen could say "stale" and nothing else — it printed the
    /// revision the quote was BUILT from as if it were the new one, and offered a single "Mark
    /// review complete" that let the draft proceed with the old quantities. The per-line diff has
    /// been in <c>LeadRevisionDifferences</c> since the identity spine shipped; this is it, projected
    /// once, for the one screen that has to act on it.</para>
    /// </summary>
    public sealed class QuoteRevisionImpactDTO
    {
        public long ImpactId { get; set; }

        /// <summary>DRAFT_STALE_REVIEW_REQUIRED (draft), QUOTE_REVISION_REQUIRED (sent), INVENTORY_REVALIDATION_REQUIRED.</summary>
        public string ImpactType { get; set; } = string.Empty;

        /// <summary>The lead revision the quote was prepared from.</summary>
        public int FromRevision { get; set; }

        /// <summary>The lead revision that arrived and made the quote stale.</summary>
        public int ToRevision { get; set; }

        /// <summary>Line-level changes only, in the buyer's own line numbers. Empty when the
        /// revision changed header facts but no line.</summary>
        public List<QuoteRevisionLineChangeDTO> Changes { get; set; } = new();
    }

    /// <summary>One changed fact on one line of the customer's document.</summary>
    public sealed class QuoteRevisionLineChangeDTO
    {
        /// <summary>The buyer's line reference ("10", "00020", "OPT-3"), never our ordinal.</summary>
        public string Line { get; set; } = string.Empty;

        /// <summary>quantity · unit · part · description · added · removed.</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>Previous value as printed on the old document; null for an added line.</summary>
        public string? From { get; set; }

        /// <summary>New value as printed on the new document; null for a removed line.</summary>
        public string? To { get; set; }
    }

    /// <summary>What "Apply the new quantities" did to the draft.</summary>
    public sealed class QuoteRevisionApplyResultDTO
    {
        public long QuoteId { get; set; }
        public int FromRevision { get; set; }
        public int ToRevision { get; set; }

        /// <summary>Draft lines whose quantity now matches the new revision.</summary>
        public int LinesUpdated { get; set; }

        /// <summary>The quantity moves that were made, one per updated line.</summary>
        public List<QuoteRevisionLineChangeDTO> Applied { get; set; } = new();

        /// <summary>Lines the new revision carries that this draft does not. Nothing is invented for
        /// them; the rep adds them by hand if they are to be quoted.</summary>
        public List<string> LinesNotOnQuote { get; set; } = new();

        /// <summary>The re-totalled quote total after the change.</summary>
        public decimal? TotalAmount { get; set; }
    }
}
