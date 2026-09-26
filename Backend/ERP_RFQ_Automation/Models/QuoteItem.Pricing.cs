namespace ERP_RFQ_Automation.Models;

public partial class QuoteItem
{
    /// <summary>
    /// How this line is offered when not simply priced (null): ESTIMATE (priced, "subject to
    /// confirmation"), TO_FOLLOW (no price yet, "Price to follow"), NOT_QUOTED ("Not quoted" with
    /// a reason). Lets a complete quote go out before the bid closes while suppliers are late.
    /// </summary>
    public string? PricingStatus { get; set; }

    /// <summary>What the customer reads under the line: the reason it is not quoted, or when the price follows.</summary>
    public string? PricingNote { get; set; }
}

/// <summary>The line states a quote can go out with besides a plain price.</summary>
public static class QuoteLinePricing
{
    public const string Estimate = "ESTIMATE";
    public const string ToFollow = "TO_FOLLOW";
    public const string NotQuoted = "NOT_QUOTED";
    public const int MaxNote = 300;

    /// <summary>A line deliberately sent without a price: never taxed, floored, awarded or ordered.</summary>
    public static bool IsUnpricedByChoice(string? status) => status is ToFollow or NotQuoted;

    public static bool IsKnown(string? status) => status is null or Estimate or ToFollow or NotQuoted;
}
