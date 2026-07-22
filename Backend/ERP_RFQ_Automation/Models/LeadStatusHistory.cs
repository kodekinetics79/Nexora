namespace ERP_RFQ_Automation.Models;

public sealed class LeadStatusHistory
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public long CommercialCaseId { get; set; }
    public long BusinessUnitId { get; set; }
    public long? PreviousStatusId { get; set; }
    public long? NewStatusId { get; set; }
    public string EventType { get; set; } = null!;
    public string? ChangedBy { get; set; }
    public string ActorSource { get; set; } = null!;
    public DateTime ChangedOn { get; set; }
    public string? Reason { get; set; }
    public string CommercialCaseReference { get; set; } = null!;

    public Lead Lead { get; set; } = null!;
    public CommercialCase CommercialCase { get; set; } = null!;
    public BusinessUnit BusinessUnit { get; set; } = null!;
}
