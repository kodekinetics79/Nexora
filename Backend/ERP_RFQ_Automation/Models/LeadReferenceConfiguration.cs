namespace ERP_RFQ_Automation.Models;

public sealed class LeadReferenceConfiguration
{
    public long BusinessUnitId { get; set; }
    public string Prefix { get; set; } = "NXR";
    public string Format { get; set; } = "{PREFIX}-{YEAR}-{SEQUENCE}";
    public int SequencePadding { get; set; } = 6;
    public int FinancialYearStartMonth { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }

    public BusinessUnit BusinessUnit { get; set; } = null!;
}
