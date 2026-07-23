namespace ERP_RFQ_Automation.OrderToCash;

public static class CustomerPurchaseOrderStatuses
{
    public const string Draft = "DRAFT";
    public const string Confirmed = "CONFIRMED";
    public const string PartiallyAwarded = "PARTIALLY_AWARDED";
    public const string FullyAwarded = "FULLY_AWARDED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, Confirmed, PartiallyAwarded, FullyAwarded, Closed, Cancelled
    };
}

public static class CustomerAwardStatuses
{
    public const string Draft = "DRAFT";
    public const string Confirmed = "CONFIRMED";
    public const string Ordered = "ORDERED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, Confirmed, Ordered, Cancelled
    };
}

public static class OrderToCashDocumentTypes
{
    public const string CustomerPurchaseOrder = "CPO";
    public const string CustomerAward = "AWD";
    public const string SalesOrder = "SO";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        CustomerPurchaseOrder, CustomerAward, SalesOrder
    };
}

public static class OrderToCashAggregateTypes
{
    public const string CustomerPurchaseOrder = "CUSTOMER_PURCHASE_ORDER";
    public const string CustomerAward = "CUSTOMER_AWARD";
}

public static class OrderToCashCommands
{
    public const string CreatePurchaseOrder = "CREATE_PURCHASE_ORDER";
    public const string UpdatePurchaseOrder = "UPDATE_PURCHASE_ORDER";
    public const string ConfirmPurchaseOrder = "CONFIRM_PURCHASE_ORDER";
    public const string CancelPurchaseOrder = "CANCEL_PURCHASE_ORDER";
    public const string CreateAward = "CREATE_AWARD";
    public const string UpdateAward = "UPDATE_AWARD";
    public const string ConfirmAward = "CONFIRM_AWARD";
    public const string CancelAward = "CANCEL_AWARD";
    public const string ConvertAwardToOrder = "CONVERT_AWARD_TO_ORDER";
}

public static class ExternalPurchaseOrderNumber
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }
}
