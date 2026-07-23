using ERP_RFQ_Automation.OrderToCash;

namespace ERP_RFQ_Automation.Models;

public partial class Order
{
    public string SourceType { get; set; } = OrderSourceTypes.Manual;
    public long? CustomerAwardId { get; set; }
    public CustomerAward? CustomerAward { get; set; }
}

public partial class OrderItem
{
    public long? CustomerAwardLineAllocationId { get; set; }
    public CustomerAwardLineAllocation? CustomerAwardLineAllocation { get; set; }
}

public static class OrderSourceTypes
{
    public const string Manual = "MANUAL";
    public const string LegacyQuote = "LEGACY_QUOTE";
    public const string CustomerAward = "CUSTOMER_AWARD";
}
