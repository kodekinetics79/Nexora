using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Lead
{
    // Payload for the review workbench PUT api/Lead/{id}/review.
    // "save"    -> persist corrections and keep the lead in NeedsReview.
    // "approve" -> persist corrections and clear the extraction-review flag. Lifecycle
    // qualification remains a separate, governed command.
    public class LeadReviewSubmitDTO
    {
        [Required]
        [Range(1, long.MaxValue)]
        public long? ExpectedVersion { get; set; }

        [Required]
        [RegularExpression("^(save|approve)$", ErrorMessage = "action must be 'save' or 'approve'.")]
        public string Action { get; set; } = null!;

        [StringLength(1000)]
        public string? Reason { get; set; }

        public LeadReviewHeaderDTO Header { get; set; } = new LeadReviewHeaderDTO();

        public List<LeadItemReviewDTO> Items { get; set; } = new List<LeadItemReviewDTO>();
    }
}
