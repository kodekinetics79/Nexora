namespace ERP_RFQ_Automation.Models;

public partial class Lead
{
    public int CurrentRevisionNumber { get; set; } = 1;
    public long? CurrentRevisionId { get; set; }
    public string? CurrentInquiryFingerprint { get; set; }
    public string? CurrentOccurrenceClassification { get; set; }
    public DateTimeOffset? SourceReceivedAtUtc { get; set; }
    public DateTimeOffset? IngestedAtUtc { get; set; }
    public int IdentityVersion { get; set; } = 1;
    public virtual ICollection<ERP_RFQ_Automation.LeadIdentity.LeadRevision> Revisions { get; set; } = new List<ERP_RFQ_Automation.LeadIdentity.LeadRevision>();
    public virtual ICollection<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence> IngestionOccurrences { get; set; } = new List<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>();
}
