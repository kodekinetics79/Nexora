using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Lead
{
    /// <summary>
    /// Payload for PUT api/Lead/{id}/client — a human deciding WHICH CLIENT ORGANISATION
    /// an enquiry came from.
    ///
    /// <para>This is deliberately a separate command from the extraction-review submit
    /// (<see cref="LeadReviewSubmitDTO"/>) rather than another optional header field on it.
    /// Correcting an extracted figure and naming the buyer are two different decisions with
    /// two different lifetimes: extraction review ENDS, and the need to name the buyer does
    /// not. Bundling them made the second decision inherit the first one's closing time —
    /// see the note on <c>LeadRepository.LinkClientAsync</c> for what that cost.</para>
    /// </summary>
    public class LeadClientLinkRequestDTO
    {
        /// <summary>The client organisation this enquiry came from. Must exist and be active in the caller's tenant.</summary>
        [Required]
        [Range(1, long.MaxValue, ErrorMessage = "A customer is required.")]
        public long? CustomerId { get; set; }

        /// <summary>The buyer contact at that organisation. Optional — a client with an
        /// unknown buyer is a complete, usable answer; a guessed buyer is not.</summary>
        public long? ContactId { get; set; }

        /// <summary>
        /// Optimistic-concurrency guard, OPTIONAL by design.
        ///
        /// <para>The review submit requires it because it rewrites the whole line-item set from
        /// a snapshot the client is holding, so a stale submit silently discards someone else's
        /// edits. This command writes exactly two scalar fields and echoes nothing back, so
        /// there is no lost-update hazard to protect against — and the callers that most need
        /// it (a lead grid row, the deadline board) do not carry a review version at all.
        /// When a caller DOES supply one it is enforced, so the review workbench keeps its
        /// guarantee.</para>
        /// </summary>
        [Range(1, long.MaxValue)]
        public long? ExpectedVersion { get; set; }

        /// <summary>Why, in the operator's words. Recorded on the immutable audit row.</summary>
        [StringLength(1000)]
        public string? Reason { get; set; }
    }
}
