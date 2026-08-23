using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public static partial class LifecyclePolicy
{
    public const string Version = "2026-07-22.v1";
    private static readonly IReadOnlyDictionary<string, string> LeadAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ACCEPTED"] = "QUALIFIED",
        ["LEAD_ACCEPTED"] = "QUALIFIED",
        ["REJECTED"] = "DISQUALIFIED",
        ["LEAD_REJECTED"] = "DISQUALIFIED",
        ["CONVERTED"] = "CONVERTED_TO_RFQ",
        ["PARTIAL_AWARD"] = "PARTIALLY_AWARDED",
        ["CLOSED"] = "COMPLETED",
        ["DUPLICATE"] = "DUPLICATED"
    };

    private static readonly IReadOnlyDictionary<string, string> RfqAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SUBMITTED"] = "READY_FOR_SALES",
        ["APPROVED"] = "READY_FOR_SALES",
        ["PENDING_APPROVAL"] = "QUOTE_PREPARATION",
        ["SENT"] = "QUOTE_SENT",
        ["PARTIAL_AWARD"] = "PARTIALLY_AWARDED",
        ["CLOSED"] = "COMPLETED"
    };

    private static readonly IReadOnlyDictionary<string, string> QuoteAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["APPROVED_AND_SENT"] = "SENT",
        ["WON"] = "ACCEPTED",
        ["LOST"] = "REJECTED",
        ["CANCELLED"] = "REJECTED"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> LeadTransitions = Graph(
        Edge("RECEIVED", "PENDING_IDENTIFICATION", "QUALIFIED", "CANCELLED"),
        Edge("PENDING_IDENTIFICATION", "UNASSIGNED", "ASSIGNED", "QUALIFIED", "CANCELLED"),
        Edge("UNASSIGNED", "ASSIGNED", "QUALIFIED", "CANCELLED"),
        Edge("ASSIGNED", "UNDER_REVIEW", "UNASSIGNED", "QUALIFIED", "CANCELLED"),
        Edge("UNDER_REVIEW", "QUALIFIED", "DISQUALIFIED", "CANCELLED", "DUPLICATED"),
        Edge("QUALIFIED", "CONVERTED_TO_RFQ", "UNDER_REVIEW", "CANCELLED"),
        Edge("CONVERTED_TO_RFQ", "QUOTED", "CANCELLED"),
        Edge("QUOTED", "NEGOTIATION", "AWARDED", "PARTIALLY_AWARDED", "LOST"),
        Edge("NEGOTIATION", "QUOTED", "AWARDED", "PARTIALLY_AWARDED", "LOST"),
        Edge("PARTIALLY_AWARDED", "AWARDED", "COMPLETED"),
        Edge("AWARDED", "COMPLETED"));

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RfqTransitions = Graph(
        Edge("DRAFT", "VALIDATING", "NEEDS_REVIEW", "CANCELLED"),
        Edge("VALIDATING", "NEEDS_REVIEW", "READY_FOR_SALES", "CANCELLED"),
        Edge("NEEDS_REVIEW", "VALIDATING", "CANCELLED"),
        Edge("READY_FOR_SALES", "INVENTORY_CHECK", "SUPPLIER_SOURCING", "PRICING_COMPLETE", "CANCELLED"),
        Edge("INVENTORY_CHECK", "SUPPLIER_SOURCING", "PRICING_COMPLETE", "NEEDS_REVIEW", "CANCELLED"),
        Edge("SUPPLIER_SOURCING", "AWAITING_SUPPLIER_RESPONSE", "PRICING_COMPLETE", "CANCELLED"),
        Edge("AWAITING_SUPPLIER_RESPONSE", "SUPPLIER_SOURCING", "PRICING_COMPLETE", "EXPIRED", "CANCELLED"),
        Edge("PRICING_COMPLETE", "QUOTE_PREPARATION", "SUPPLIER_SOURCING", "NEEDS_REVIEW", "CANCELLED"),
        Edge("QUOTE_PREPARATION", "QUOTE_SENT", "PRICING_COMPLETE", "CANCELLED"),
        Edge("QUOTE_SENT", "CUSTOMER_FOLLOW_UP", "REVISION_REQUESTED", "AWARDED", "PARTIALLY_AWARDED", "LOST", "EXPIRED", "CANCELLED"),
        Edge("CUSTOMER_FOLLOW_UP", "REVISION_REQUESTED", "AWARDED", "PARTIALLY_AWARDED", "LOST", "EXPIRED", "CANCELLED"),
        Edge("REVISION_REQUESTED", "VALIDATING", "CANCELLED"),
        Edge("PARTIALLY_AWARDED", "AWARDED"));

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> QuoteTransitions = Graph(
        Edge("DRAFT", "SENT", "REJECTED"),
        Edge("SENT", "ACCEPTED", "REJECTED", "EXPIRED"),
        Edge("ACCEPTED", "ORDERED"));

    private static readonly IReadOnlySet<string> LeadTerminal = Set("DISQUALIFIED", "LOST", "CANCELLED", "COMPLETED", "DUPLICATED");

    /// <summary>
    /// The lead terminals that mean "this inquiry was lost or abandoned". These are the states a
    /// case can reach BEFORE a quotation exists, so they are the ones that must carry a governed
    /// outcome reason — otherwise the loss leaves the pipeline unexplained and drops out of
    /// win/loss reporting. COMPLETED (won through) and DUPLICATED (merged, never a loss) do not.
    /// </summary>
    private static readonly IReadOnlySet<string> LeadLossTerminal = Set("DISQUALIFIED", "LOST", "CANCELLED");
    private static readonly IReadOnlySet<string> RfqTerminal = Set("AWARDED", "LOST", "EXPIRED", "CANCELLED", "COMPLETED");
    private static readonly IReadOnlySet<string> QuoteTerminal = Set("REJECTED", "EXPIRED", "ORDERED");
    private static readonly IReadOnlySet<string> LeadReopenable = Set("DISQUALIFIED", "LOST", "CANCELLED");
    private static readonly IReadOnlySet<string> RfqReopenable = Set("LOST", "EXPIRED", "CANCELLED");
    private static readonly IReadOnlySet<string> QuoteReopenable = Set();
    private static readonly IReadOnlySet<string> ElevatedTargets = Set(
        "CONVERTED_TO_RFQ", "DISQUALIFIED", "CANCELLED", "AWARDED", "PARTIALLY_AWARDED", "LOST", "EXPIRED", "COMPLETED");

    public static string Canonicalize(string aggregateType, string? code, string? value = null)
    {
        var normalized = Normalize(string.IsNullOrWhiteSpace(code) ? value : code);
        if (normalized.Length == 0) return aggregateType == "Lead" ? "RECEIVED" : "DRAFT";
        var aliases = aggregateType switch
        {
            "Lead" => LeadAliases,
            "Rfq" => RfqAliases,
            "Quote" => QuoteAliases,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        };
        return aliases.TryGetValue(normalized, out var canonical) ? canonical : normalized;
    }

    public static IReadOnlySet<string> AllowedTargets(string aggregateType, string currentCode)
    {
        var graph = aggregateType switch
        {
            "Lead" => LeadTransitions,
            "Rfq" => RfqTransitions,
            "Quote" => QuoteTransitions,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        };
        return graph.TryGetValue(currentCode, out var targets) ? targets : Set();
    }

    public static bool IsTerminal(string aggregateType, string code)
        => (aggregateType switch
        {
            "Lead" => LeadTerminal,
            "Rfq" => RfqTerminal,
            "Quote" => QuoteTerminal,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        }).Contains(code);

    public static string ReopenTarget(string aggregateType)
        => aggregateType switch
        {
            "Lead" => "UNDER_REVIEW",
            "Rfq" => "NEEDS_REVIEW",
            "Quote" => "DRAFT",
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        };

    public static bool IsReopenable(string aggregateType, string code)
        => (aggregateType switch
        {
            "Lead" => LeadReopenable,
            "Rfq" => RfqReopenable,
            "Quote" => QuoteReopenable,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        }).Contains(code);

    public static bool RequiresElevatedAuthorization(string targetCode) => ElevatedTargets.Contains(targetCode);

    public static bool RequiresReason(string aggregateType, string targetCode, bool reopen)
        => reopen || IsTerminal(aggregateType, targetCode) || targetCode is "AWARDED" or "PARTIALLY_AWARDED";

    /// <summary>
    /// True when the transition records a lead-stage loss, i.e. the inquiry ends without a
    /// quotation ever deciding it. Such a transition must name a reason from the governed
    /// outcome-reason picklist — the same one a quote outcome uses.
    /// </summary>
    public static bool RecordsLeadLoss(string aggregateType, string targetCode)
        => aggregateType == "Lead" && LeadLossTerminal.Contains(targetCode);

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Graph(params (string From, string[] To)[] rows)
        => rows.ToDictionary(row => row.From, row => (IReadOnlySet<string>)Set(row.To), StringComparer.Ordinal);

    private static (string From, string[] To) Edge(string from, params string[] to) => (from, to);

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static string Normalize(string? value)
        => Separator().Replace((value ?? string.Empty).Trim().ToUpperInvariant(), "_").Trim('_');

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex Separator();
}
