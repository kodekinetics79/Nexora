using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Lead
{
    /// <summary>
    /// Payload for PUT api/Lead/{id}/closing-date — a person answering "Closes 8 Sep or 9 Aug?"
    /// (or setting the closing date outright). Writes the closing date and, when the document's
    /// received date was ambiguous in the same way, the received date read in the same order.
    /// </summary>
    public class LeadClosingDateAnswerDTO
    {
        /// <summary>The closing date and time the person chose, as the document's own clock reads it.</summary>
        [Required]
        public DateTime? BidClosingDate { get; set; }

        /// <summary>Optional optimistic-concurrency guard, as on the client link.</summary>
        [Range(1, long.MaxValue)]
        public long? ExpectedVersion { get; set; }
    }
}
