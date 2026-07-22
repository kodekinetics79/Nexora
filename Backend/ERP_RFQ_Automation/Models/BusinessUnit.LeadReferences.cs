namespace ERP_RFQ_Automation.Models;

public partial class BusinessUnit
{
    public virtual LeadReferenceConfiguration? LeadReferenceConfiguration { get; set; }

    public virtual ICollection<CommercialCase> CommercialCases { get; set; } = new List<CommercialCase>();

    public virtual ICollection<LeadStatusHistory> LeadStatusHistories { get; set; } = new List<LeadStatusHistory>();
}
