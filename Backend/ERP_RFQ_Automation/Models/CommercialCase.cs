namespace ERP_RFQ_Automation.Models;

public sealed class CommercialCase
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long AllocationNumber { get; private set; }
    public string MasterReference { get; private set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;

    public BusinessUnit BusinessUnit { get; set; } = null!;
    public Lead Lead { get; set; } = null!;
    public ICollection<LeadStatusHistory> LeadStatusHistory { get; set; } = new List<LeadStatusHistory>();

    internal void AssignIdentity(long allocationNumber, string masterReference)
    {
        if (AllocationNumber != 0 || !string.IsNullOrEmpty(MasterReference))
            throw new InvalidOperationException("A commercial-case identity cannot be replaced.");

        AllocationNumber = allocationNumber;
        MasterReference = masterReference;
    }
}
