namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>
/// One internet search this company ran, kept for 30 days so the next case for the same part
/// costs nothing and answers instantly. Tenant-scoped like every other business table (EF query
/// filter plus the <c>nexora_tenant_isolation</c> RLS policy the SupplierDiscovery migration adds).
///
/// <para>What is stored is the classified hit list, not the ranking: whether a hit is already a
/// known supplier is decided on every read against the live supplier list.</para>
/// </summary>
public sealed class SupplierDiscoverySearch
{
    public const int CacheDays = 30;

    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>SHA-256 of the normalised makers, part numbers and description — see <see cref="SupplierDiscoveryIdentity.Key"/>.</summary>
    public string IdentityKey { get; set; } = null!;

    /// <summary>"LV431831 (Schneider Electric)" — so an operator reading the table knows what was searched.</summary>
    public string Subject { get; set; } = null!;

    /// <summary>Provider label, e.g. <c>Ollama</c>.</summary>
    public string Provider { get; set; } = null!;

    /// <summary>The queries that were posted, as a JSON array of strings.</summary>
    public string QueriesJson { get; set; } = "[]";

    /// <summary>The classified hits, as a JSON array of <see cref="SupplierDiscoveryStoredHit"/>.</summary>
    public string HitsJson { get; set; } = "[]";

    public int HitCount { get; set; }
    public DateTime SearchedAtUtc { get; set; }
    public string SearchedBy { get; set; } = null!;

    public bool IsFresh(DateTime now) => SearchedAtUtc > now.AddDays(-CacheDays);
}
