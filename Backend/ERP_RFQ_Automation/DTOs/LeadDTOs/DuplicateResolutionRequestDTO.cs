using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.LeadDTOs
{
    /// <summary>
    /// Body for POST /api/Lead/{id}/duplicate-resolution (WP-A3).
    /// Action: "not_duplicate" clears the conversion block; "confirm" keeps it.
    /// </summary>
    public class DuplicateResolutionRequestDTO
    {
        [Required]
        [RegularExpression("^(not_duplicate|confirm)$",
            ErrorMessage = "Action must be \"not_duplicate\" or \"confirm\".")]
        public string Action { get; set; } = string.Empty;
    }
}
