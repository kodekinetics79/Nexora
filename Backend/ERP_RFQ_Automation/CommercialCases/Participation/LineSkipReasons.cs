using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

/// <summary>One reason a line on a request is left out of the quote.</summary>
public sealed record LineSkipReason(string Code, string Label);

/// <summary>
/// The tenant's list of reasons a LINE is not quoted — its own reference list, separate from the
/// quote-outcome list.
///
/// <para>The "Why skip line N" picker used to be fed from the quote-outcome list (<c>SetupMaster</c>
/// type <c>QuoteOutcomeReason</c>), so a rep skipping one valve was offered "Lost to competitor",
/// "Customer cancelled", "No response" and "Expired automatically" — words that describe how a
/// quote ENDED, not why one line is left out before a quote exists. Rather than hard-code which
/// outcome codes to hide, the line decision gets its own list (<see cref="SetupType"/>), seeded
/// from <see cref="TenantBaselineCatalog.ReferenceLists"/> like every other reference list, so a
/// tenant can rename, trim or extend it in Setup exactly as it can the others.</para>
///
/// <para>Tenant isolation: every read is filtered by <c>BusinessUnitId</c>. A tenant that has no
/// rows yet (the startup reconciler has not reached it) sees the baseline list read-only; nothing
/// is written from here.</para>
/// </summary>
public interface ILineSkipReasons
{
    Task<IReadOnlyList<LineSkipReason>> GetAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>True when <paramref name="code"/> is one of this tenant's line-skip reasons.</summary>
    Task<bool> IsGovernedAsync(long businessUnitId, string? code, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class LineSkipReasons(ErpRfqAutomationContext db) : ILineSkipReasons
{
    public const string SetupType = "LineSkipReason";

    /// <summary>The list every tenant starts with, as the baseline catalogue defines it.</summary>
    public static IReadOnlyList<LineSkipReason> Baseline { get; } = TenantBaselineCatalog.ReferenceLists
        .Single(list => list.SetupType == SetupType).Entries
        .Select(entry => new LineSkipReason(entry.Code, entry.Label))
        .ToArray();

    public async Task<IReadOnlyList<LineSkipReason>> GetAsync(long businessUnitId, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        // IgnoreQueryFilters is paired with an explicit BusinessUnitId predicate, as the outcome
        // list does: the participation commit runs on tenant-less paths too, and a reason must
        // still never be read from another tenant's rows.
        var rows = await db.SetupMasters.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.SetupType == SetupType
                        && s.BusinessUnitId == businessUnitId
                        && (s.IsActive == true || s.IsActive == null))
            .OrderBy(s => s.SetupId)
            .Select(s => new LineSkipReason(s.SetupCode ?? "", s.Description ?? s.SetupValue))
            .ToListAsync(ct);
        return rows.Count > 0 ? rows : Baseline;
    }

    public async Task<bool> IsGovernedAsync(long businessUnitId, string? code, CancellationToken ct = default)
    {
        var wanted = code?.Trim();
        if (string.IsNullOrEmpty(wanted)) return false;
        var reasons = await GetAsync(businessUnitId, ct);
        return reasons.Any(reason => string.Equals(reason.Code, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
