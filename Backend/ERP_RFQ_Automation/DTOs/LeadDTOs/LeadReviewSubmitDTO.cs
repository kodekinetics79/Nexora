using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Lead
{
    // Payload for the review workbench PUT api/Lead/{id}/review.
    // "save"    -> persist corrections and clear the NeedsReview flag, leaving the lead new.
    // "approve" -> same, and additionally mark the lead Accepted (LeadStatusId = 24).
    public class LeadReviewSubmitDTO
    {
        [Required]
        [RegularExpression("^(save|approve)$", ErrorMessage = "action must be 'save' or 'approve'.")]
        public string Action { get; set; } = null!;

        public LeadReviewHeaderDTO Header { get; set; } = new LeadReviewHeaderDTO();

        public List<LeadItemReviewDTO> Items { get; set; } = new List<LeadItemReviewDTO>();
    }
}
