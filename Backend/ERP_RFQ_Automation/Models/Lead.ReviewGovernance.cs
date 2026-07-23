namespace ERP_RFQ_Automation.Models;

public partial class Lead
{
    public long ReviewVersion { get; set; } = 1;
    public bool RequiresCommercialReview { get; set; }
    public bool CommercialFactsVerified { get; set; }
    public string? ReviewApprovedBy { get; set; }
    public DateTime? ReviewApprovedOn { get; set; }
    public ICollection<LeadReviewAudit> ReviewAudits { get; set; } = new List<LeadReviewAudit>();
}

public sealed class LeadReviewAudit
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long LeadId { get; set; }
    public long FromVersion { get; set; }
    public long ToVersion { get; set; }
    public string Action { get; set; } = null!;
    public string ReviewedBy { get; set; } = null!;
    public string? Reason { get; set; }
    public string BeforeJson { get; set; } = null!;
    public string AfterJson { get; set; } = null!;
    public DateTime ReviewedOn { get; set; }
    public Lead Lead { get; set; } = null!;
}
