using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Lead;

public sealed class LeadClarificationRequestDTO
{
    [Range(1, long.MaxValue)]
    public long ExpectedReviewVersion { get; set; }

    [Required]
    [StringLength(1000, MinimumLength = 3)]
    public string Note { get; set; } = null!;
}
