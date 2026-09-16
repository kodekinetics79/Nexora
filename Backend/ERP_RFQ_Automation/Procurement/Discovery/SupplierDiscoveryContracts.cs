using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>
/// One line of an internet search: what the page calls itself, where it is, and the excerpt the
/// search engine chose. This is the whole of what the provider hands over; everything the rep
/// sees is derived from these three strings inside this process.
/// </summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>
/// The seam between "search the internet" and everything else. One implementation today
/// (<see cref="OllamaWebSearchProvider"/>); the tests stub it so no test ever reaches the internet.
/// </summary>
public interface ISupplierWebSearchProvider
{
    /// <summary>
    /// The origin every query is posted to, described the way the AI allow-list describes an
    /// inference endpoint, so the tenant's consent for this exact host can be checked before a
    /// single part number leaves the box.
    /// </summary>
    AiProviderDescriptor Destination { get; }

    /// <exception cref="SupplierWebSearchException">The provider refused or did not answer.</exception>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>Raised by a provider for anything short of an answer: a non-2xx status, a timeout, a body it cannot read.</summary>
public sealed class SupplierWebSearchException : Exception
{
    public SupplierWebSearchException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What the discover endpoint answers with. The word is the whole story for the screen.</summary>
public static class SupplierDiscoveryStatuses
{
    /// <summary>There are hits to tick.</summary>
    public const string Ready = "Ready";

    /// <summary>The deployment has no search key. An administrator's job, not the rep's.</summary>
    public const string NotConfigured = "NotConfigured";

    /// <summary>This company has not switched internet search on under AI governance.</summary>

    /// <summary>The internet was searched and nothing usable came back.</summary>
    public const string NoResults = "NoResults";

    /// <summary>The search provider failed. The detail is in the log, never in the message.</summary>
    public const string Error = "Error";
}

/// <summary>What the search was about, echoed so the rep can see the system searched for the right thing.</summary>
public sealed record SupplierDiscoverySearchedFor(
    string? Maker,
    string? PartNumber,
    string Description,
    IReadOnlyList<string> AcceptableMakers);

/// <summary>One company the internet search found, in the rep's words.</summary>
public sealed record SupplierDiscoveryHit(
    string Id,
    string Name,
    string Website,
    string Domain,
    string Role,
    string? Country,
    string Why,
    string? ContactEmail,
    long? ExistingSupplierId);

public sealed record SupplierDiscoveryResult(
    string Status,
    string Message,
    SupplierDiscoverySearchedFor SearchedFor,
    int Total,
    int Offset,
    int Limit,
    bool FromCache,
    DateTime? SearchedAtUtc,
    IReadOnlyList<SupplierDiscoveryHit> Hits);

public sealed record AdoptedDiscoveredSupplier(
    string HitId,
    long SupplierId,
    string SupplierName,
    string? ContactEmail,
    bool NeedsContactEmail,
    bool AlreadyExisted);

public sealed record AdoptDiscoveredSuppliersResult(IReadOnlyList<AdoptedDiscoveredSupplier> Adopted);

public sealed record DiscoverSuppliersCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    int Offset,
    int Limit,
    string Actor);

public sealed record AdoptDiscoveredSuppliersCommand(
    long BusinessUnitId,
    long SourcingCaseId,
    IReadOnlyCollection<string> HitIds,
    string Actor,
    string CorrelationId);

public interface ISupplierDiscoveryService
{
    /// <summary>Searches the internet for suppliers of the case's part and maker, ranked and paged.</summary>
    Task<SupplierDiscoveryResult> DiscoverAsync(DiscoverSuppliersCommand command, CancellationToken ct = default);

    /// <summary>Turns the ticked hits into suppliers on this company's list and refreshes the case's candidates.</summary>
    Task<AdoptDiscoveredSuppliersResult> AdoptAsync(AdoptDiscoveredSuppliersCommand command, CancellationToken ct = default);

    /// <summary>The same search from a free-text query — the supplier page's "search the internet" box.</summary>
    Task<SupplierDiscoveryResult> SearchAsync(long businessUnitId, string query, int offset, int limit, CancellationToken ct = default);
}
