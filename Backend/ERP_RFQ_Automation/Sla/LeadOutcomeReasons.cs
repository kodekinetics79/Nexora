using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// The lead-stage view of the SAME governed outcome-reason picklist the quote path uses
/// (<c>SetupMaster</c> rows with <c>SetupType = "QuoteOutcomeReason"</c>).
///
/// <para>There is deliberately no second vocabulary and no second seeder: the default rows and
/// their labels live in exactly one place (<see cref="QuoteOutcomeService"/>), and this type reuses
/// them through <see cref="IQuoteOutcomeService.GetOutcomeReasonsAsync"/>, which seeds a business
/// unit's list idempotently on first use. Reporting therefore never has to reconcile a lead loss
/// reason against a quote loss reason — they are the same rows, including any reason a tenant added
/// for itself.</para>
///
/// <para>Tenant isolation: every read is filtered by <c>BusinessUnitId</c>. Unlike the quote path
/// this type has NO business-unit-agnostic fallback — a lead outcome may only ever point at a
/// reason row owned by the lead's own tenant.</para>
/// </summary>
public interface ILeadOutcomeReasons
{
    /// <summary>The governed picklist for a business unit, seeding the defaults on first use.</summary>
    Task<IReadOnlyList<OutcomeReasonDto>> GetAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a reason code to its <c>SetupMaster.SetupId</c> inside the tenant, seeding the
    /// governed defaults if the tenant has never used the list. Returns null when the code is not
    /// part of this tenant's governed vocabulary.
    /// </summary>
    Task<long?> ResolveAsync(long businessUnitId, string? code, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class LeadOutcomeReasons : ILeadOutcomeReasons
{
    public const string SetupType = "QuoteOutcomeReason";

    private readonly ErpRfqAutomationContext _context;
    private readonly IServiceProvider? _services;

    /// <param name="services">
    /// Optional. Used to obtain <see cref="IQuoteOutcomeService"/> — the sole owner of the default
    /// reason rows — LAZILY, at the moment a seed is actually needed. Taking it by constructor
    /// injection instead would close a dependency loop (lifecycle -> reasons -> quote outcome ->
    /// lifecycle) that the container cannot build. When it is absent (lightweight direct
    /// constructions such as <c>new LifecycleApplicationService(db)</c>) the already-persisted rows
    /// are read as-is; nothing is invented locally.
    /// </param>
    public LeadOutcomeReasons(ErpRfqAutomationContext context, IServiceProvider? services = null)
    {
        _context = context;
        _services = services;
    }

    public async Task<IReadOnlyList<OutcomeReasonDto>> GetAsync(long businessUnitId, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var governedList = GovernedList();
        if (governedList is not null)
            return await governedList.GetOutcomeReasonsAsync(businessUnitId, ct);
        return await ReadAsync(businessUnitId, ct);
    }

    public async Task<long?> ResolveAsync(long businessUnitId, string? code, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        var wanted = code?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;

        // Fast path: the row already exists (this also covers reasons a tenant added itself).
        var existing = await ReadAsync(businessUnitId, ct);
        var match = existing.FirstOrDefault(r => string.Equals(r.Code, wanted, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Id;

        // The tenant has never opened the list; seed it through the one owner of the defaults.
        var governedList = GovernedList();
        if (governedList is null) return null;
        var seeded = await governedList.GetOutcomeReasonsAsync(businessUnitId, ct);
        return seeded.FirstOrDefault(r => string.Equals(r.Code, wanted, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private IQuoteOutcomeService? GovernedList() => _services?.GetService<IQuoteOutcomeService>();

    // IgnoreQueryFilters is paired with an explicit BusinessUnitId predicate: lead outcomes are also
    // recorded on tenant-less paths (workers, seeds), and a governed reason must still never be read
    // from — or resolved against — another tenant's rows.
    private async Task<IReadOnlyList<OutcomeReasonDto>> ReadAsync(long businessUnitId, CancellationToken ct) =>
        await _context.SetupMasters.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.SetupType == SetupType
                        && s.BusinessUnitId == businessUnitId
                        && (s.IsActive == true || s.IsActive == null))
            .OrderBy(s => s.SetupId)
            .Select(s => new OutcomeReasonDto
            {
                Id = s.SetupId,
                Code = s.SetupCode ?? "",
                Label = s.Description ?? s.SetupValue
            })
            .ToListAsync(ct);
}
