namespace ERP_RFQ_Automation.Search;

/// <summary>
/// The entity families FR-DSH-04's quick search spans. The names are the wire values a client
/// sends in <c>entities=</c> and receives back on every hit, so they are stable identifiers rather
/// than display labels.
/// </summary>
public static class SearchEntities
{
    public const string Customer = "customer";
    public const string Supplier = "supplier";
    public const string Product = "product";
    public const string Lead = "lead";
    public const string Rfq = "rfq";
    public const string Quote = "quote";
    public const string Order = "order";
    public const string Shipment = "shipment";

    public static readonly string[] All =
        [Customer, Supplier, Product, Lead, Rfq, Quote, Order, Shipment];

    /// <summary>
    /// The module permission each family is read under. A caller who cannot open the Suppliers
    /// module does not get supplier hits — a quick search that ignores module permissions is a
    /// permission bypass with a magnifying-glass icon on it.
    /// </summary>
    public static string ModuleFor(string entity) => entity switch
    {
        Customer => "Customers",
        Supplier => "Suppliers",
        Product => "Products",
        Lead => "Leads",
        Rfq => "RFQ Management",
        Quote => "Quotations",
        Order => "Orders",
        Shipment => "Shipments",
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, "Unknown search entity.")
    };
}

/// <summary>One row of a quick-search answer.</summary>
/// <param name="Entity">One of <see cref="SearchEntities"/>.</param>
/// <param name="Id">The record's own key, for the drill-down route.</param>
/// <param name="Title">What the record is called — the document number or the name.</param>
/// <param name="Subtitle">Secondary identification (the customer on a quote, the part number on a
/// product). Null when the record genuinely carries none; the client renders nothing rather than
/// inventing filler.</param>
/// <param name="Status">The record's own status wording, or null when the entity has no status
/// concept at all. Null is NOT rendered as "Unknown": see <see cref="GlobalSearchResponse.Notes"/>.</param>
/// <param name="OccurredOn">The date the date-range filter matched on, so a caller can see WHICH
/// date they filtered — the entities do not share one (a quote has a quote date, an order an order
/// date, a customer only a creation date).</param>
/// <param name="DateField">The name of the column <paramref name="OccurredOn"/> came from.</param>
/// <param name="MatchedOn">Which field the term hit, so a result that looks unrelated to the typed
/// text can be explained rather than distrusted.</param>
public sealed record SearchHit(
    string Entity,
    long Id,
    string Title,
    string? Subtitle,
    string? Status,
    DateTime? OccurredOn,
    string DateField,
    string MatchedOn);

/// <summary>
/// A quick-search answer, including what it could NOT answer.
/// </summary>
/// <param name="Query">The canonicalised term actually searched.</param>
/// <param name="Hits">Results, most recent first within each family.</param>
/// <param name="SearchedEntities">The families actually searched — which is the requested set MINUS
/// the ones the caller has no permission to read.</param>
/// <param name="DeniedEntities">Families the caller asked for and may not read. Stated rather than
/// silently dropped: a search that quietly returns fewer families looks like "no results".</param>
/// <param name="Truncated">Families where the limit was reached, so "10 results" is not mistaken
/// for "10 matching records exist".</param>
/// <param name="Notes">Stated gaps. This is where "RFQs carry no status column, so the status
/// filter excluded them" is said out loud instead of being invisible.</param>
public sealed record GlobalSearchResponse(
    string Query,
    IReadOnlyList<SearchHit> Hits,
    IReadOnlyList<string> SearchedEntities,
    IReadOnlyList<string> DeniedEntities,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Notes);
