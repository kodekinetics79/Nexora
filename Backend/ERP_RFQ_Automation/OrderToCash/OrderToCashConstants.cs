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

    /// <summary>
    /// FR-COM-04. A named person taking responsibility for converting an award whose buyer PO
    /// disagrees with the quotation on price, part or unit. See
    /// <see cref="CustomerPurchaseOrderDifferences.BlocksOrderConversion"/>.
    /// </summary>
    public const string AcceptPurchaseOrderDifferences = "ACCEPT_PO_DIFFERENCES";
}

/// <summary>
/// FR-COM-04. The vocabulary of the customer-PO discrepancy report.
///
/// <para>These were seven bare string literals inside one private method, which is why nothing
/// outside that method could act on them. They live next to the statuses they belong beside so
/// that a guard, a screen and a report all name the same difference the same way.</para>
/// </summary>
public static class CustomerPurchaseOrderDifferences
{
    /// <summary>No award allocation points at this buyer line — nobody has matched it yet.</summary>
    public const string UnquotedOrUnmatchedLine = "UNQUOTED_OR_UNMATCHED_LINE";

    /// <summary>We accepted a different quantity from the one the buyer ordered.</summary>
    public const string QuantityDiscrepancy = "QUANTITY_DISCREPANCY";

    /// <summary>Quantity is left open on the quotation. A decision, not a disagreement.</summary>
    public const string PartialQuoteAward = "PARTIAL_QUOTE_AWARD";

    /// <summary>The buyer named a part we did not quote.</summary>
    public const string PartDiscrepancy = "PART_DISCREPANCY";

    /// <summary>The buyer ordered in a different unit from the one we quoted in.</summary>
    public const string UomDiscrepancy = "UOM_DISCREPANCY";

    /// <summary>The buyer's document states no unit price, so no price comparison was possible.</summary>
    public const string PoPriceNotProvided = "PO_PRICE_NOT_PROVIDED";

    /// <summary>The buyer's unit price is outside the tenant's configured price tolerance.</summary>
    public const string PriceDiscrepancy = "PRICE_DISCREPANCY";

    /// <summary>
    /// The differences that describe a DIFFERENT COMMERCIAL DEAL from the one we quoted, and
    /// therefore refuse to become a sales order until a named person accepts them.
    ///
    /// <para>The three positive disagreements — price, part, unit — plus the case where the buyer
    /// stated no price at all, because "we could not compare" is not the same as "they agree" and
    /// the sales order would still be raised at OUR price.</para>
    ///
    /// <para><see cref="QuantityDiscrepancy"/> and <see cref="PartialQuoteAward"/> are deliberately
    /// NOT here: both are the operator's own award decision, typed on the capture screen, so
    /// blocking on them would ask someone to accept what they had just chosen.
    /// <see cref="UnquotedOrUnmatchedLine"/> cannot arise for a line this award allocates.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> BlocksOrderConversion = new HashSet<string>(StringComparer.Ordinal)
    {
        PriceDiscrepancy, PoPriceNotProvided, PartDiscrepancy, UomDiscrepancy
    };
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
