namespace ERP_RFQ_Automation.Models;

public partial class Order
{
    public long? CommercialCaseId { get; set; }
    public string? NexoraSerial { get; set; }
    public long? ContactId { get; set; }
}
