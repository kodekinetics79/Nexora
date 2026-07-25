namespace ERP_RFQ_Automation.Models;

public partial class Inventory
{
    public long? ProductId { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal QuarantineQuantity { get; set; }
    public decimal DamagedQuantity { get; set; }
    public decimal ExpiredQuantity { get; set; }
    public decimal SafetyStockQuantity { get; set; }
}
