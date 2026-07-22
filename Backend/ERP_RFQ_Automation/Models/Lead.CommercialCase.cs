namespace ERP_RFQ_Automation.Models;

public partial class Lead
{
    public long CommercialCaseId { get; private set; }
    public string CommercialCaseReference { get; private set; } = null!;

    public virtual CommercialCase CommercialCase { get; private set; } = null!;

    public virtual ICollection<LeadStatusHistory> StatusHistory { get; set; } = new List<LeadStatusHistory>();

    internal void AssignCommercialCase(CommercialCase commercialCase)
    {
        if (CommercialCaseId != 0 || CommercialCase != null || !string.IsNullOrEmpty(CommercialCaseReference))
            throw new InvalidOperationException("A commercial-case reference cannot be replaced.");

        CommercialCase = commercialCase;
        CommercialCaseReference = commercialCase.MasterReference;
    }
}
