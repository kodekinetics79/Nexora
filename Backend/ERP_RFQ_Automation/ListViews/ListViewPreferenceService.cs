using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.ListViews;

/// <summary>
/// Resolves the column layout a given user should see on a given list view, and persists
/// their changes.
///
/// Three rules govern every path in here, and the tests pin all three:
///
/// 1. <b>Defaults live in code.</b> A user with no stored row gets the catalog definition
///    verbatim. Nothing is written to the database to establish a default.
/// 2. <b>Stale keys degrade, never throw.</b> A stored preference naming a column that no
///    longer exists (renamed grid field, retired custom field) is silently dropped on read
///    and pruned on the next write. Views evolve; a user's grid must not break when they do.
/// 3. <b>New columns appear.</b> Anything in the catalog or in the tenant's active custom
///    fields that the stored layout does not mention is appended in declared order using
///    its declared default visibility — so shipping a new column does not require touching
///    every user's saved row.
///
/// Every query runs through the tenant-filtered <see cref="ErpRfqAutomationContext"/>; there
/// is no IgnoreQueryFilters anywhere in this file, and the user id is an additional
/// predicate on top of that filter rather than a replacement for it.
/// </summary>
public sealed class ListViewPreferenceService : IListViewPreferenceService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly TimeProvider _clock;

    public ListViewPreferenceService(ErpRfqAutomationContext context, TimeProvider? clock = null)
    {
        _context = context;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ListViewColumnsResponse> GetAsync(
        long businessUnitId, long userId, string viewKey, CancellationToken ct)
    {
        var view = Require(viewKey);
        EnsureScope(businessUnitId, userId);

        var available = await AvailableColumnsAsync(businessUnitId, view, ct);
        var stored = await FindPreferenceAsync(businessUnitId, userId, view.ViewKey, ct);
        return Merge(view, available, stored?.Columns(), stored is not null);
    }

    public async Task<ListViewColumnsResponse> SaveAsync(
        long businessUnitId, long userId, string viewKey,
        SaveColumnPreferenceCommand command, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var view = Require(viewKey);
        EnsureScope(businessUnitId, userId);

        var available = await AvailableColumnsAsync(businessUnitId, view, ct);
        var byKey = available.ToDictionary(x => x.Key, StringComparer.Ordinal);

        // Prune on write as well as on read: an unknown key is dropped rather than stored,
        // so a client that posts a column we removed cannot grow the row indefinitely.
        var sanitized = new List<StoredColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requested in (command.Columns ?? []).Take(ListViewCatalog.MaximumStoredColumns))
        {
            if (requested is null || string.IsNullOrWhiteSpace(requested.Key)) continue;
            var key = requested.Key.Trim();
            if (!byKey.TryGetValue(key, out var column)) continue;
            if (!seen.Add(key)) continue;
            sanitized.Add(new StoredColumn(key, column.Locked || requested.Visible));
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await FindPreferenceAsync(businessUnitId, userId, view.ViewKey, ct);
        if (existing is null)
        {
            existing = ColumnPreference.Create(businessUnitId, userId, view.ViewKey, sanitized, actor, now);
            _context.Set<ColumnPreference>().Add(existing);
        }
        else
        {
            existing.Replace(sanitized, actor, now);
        }

        await _context.SaveChangesAsync(ct);
        return Merge(view, available, sanitized, isCustomised: true);
    }

    public async Task<ListViewColumnsResponse> ResetAsync(
        long businessUnitId, long userId, string viewKey, CancellationToken ct)
    {
        var view = Require(viewKey);
        EnsureScope(businessUnitId, userId);

        var existing = await FindPreferenceAsync(businessUnitId, userId, view.ViewKey, ct);
        if (existing is not null)
        {
            _context.Set<ColumnPreference>().Remove(existing);
            await _context.SaveChangesAsync(ct);
        }

        var available = await AvailableColumnsAsync(businessUnitId, view, ct);
        return Merge(view, available, stored: null, isCustomised: false);
    }

    // -----------------------------------------------------------------------------------

    private Task<ColumnPreference?> FindPreferenceAsync(
        long businessUnitId, long userId, string viewKey, CancellationToken ct) =>
        _context.Set<ColumnPreference>()
            .FirstOrDefaultAsync(
                x => x.BusinessUnitId == businessUnitId && x.UserId == userId && x.ViewKey == viewKey, ct);

    /// <summary>
    /// The full set of columns this tenant can choose from on this view: the code catalog,
    /// then the tenant's own ACTIVE custom fields for the view's attach entity, ordered by
    /// the display order on their active version.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedColumn>> AvailableColumnsAsync(
        long businessUnitId, ListViewDefinition view, CancellationToken ct)
    {
        var columns = view.Columns
            .Select(x => new ResolvedColumn(x.Key, x.Label, x.DefaultVisible || x.Locked, x.Locked, "catalog"))
            .ToList();

        if (string.IsNullOrWhiteSpace(view.CustomFieldEntityType)) return columns;

        var entityType = view.CustomFieldEntityType;
        var definitions = await _context.Set<CustomFieldDefinition>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && x.EntityType == entityType
                        && x.Status == CustomFieldDefinitionStatus.Active
                        && x.ActiveVersionNumber != null)
            .Include(x => x.Versions)
            .ToListAsync(ct);

        var custom = new List<ResolvedColumn>();
        foreach (var definition in definitions)
        {
            var active = definition.Versions
                .FirstOrDefault(v => v.VersionNumber == definition.ActiveVersionNumber);
            if (active is null) continue;
            custom.Add(new ResolvedColumn(
                ListViewCatalog.CustomFieldColumnKey(definition.StableKey),
                active.Label,
                // Tenant-defined fields start hidden: a tenant adding a field must not
                // silently rearrange every user's grid. The user opts in from the picker.
                Visible: false,
                Locked: false,
                Source: "customField",
                DataType: active.DataType,
                StableKey: definition.StableKey));
        }

        columns.AddRange(custom
            .OrderBy(x => definitions.First(d => d.StableKey == x.StableKey).DisplayOrder)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase));
        return columns;
    }

    /// <summary>
    /// Applies a stored layout over the available columns.
    /// Stored order wins; unknown stored keys are dropped; unmentioned available columns are
    /// appended in declared order at their declared default visibility; locked columns are
    /// forced visible regardless of what was stored.
    /// </summary>
    private static ListViewColumnsResponse Merge(
        ListViewDefinition view,
        IReadOnlyList<ResolvedColumn> available,
        IReadOnlyList<StoredColumn>? stored,
        bool isCustomised)
    {
        var supportsCustomFields = !string.IsNullOrWhiteSpace(view.CustomFieldEntityType);
        if (stored is null || stored.Count == 0)
            return new ListViewColumnsResponse(view.ViewKey, available, isCustomised, supportsCustomFields);

        var byKey = available.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var ordered = new List<ResolvedColumn>(available.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in stored)
        {
            // Rule 2: a stored key that no longer resolves is ignored, not an error.
            if (!byKey.TryGetValue(entry.Key, out var column)) continue;
            if (!placed.Add(entry.Key)) continue;
            ordered.Add(column with { Visible = column.Locked || entry.Visible });
        }

        // Rule 3: columns the stored layout never mentioned keep their declared default.
        foreach (var column in available)
        {
            if (placed.Contains(column.Key)) continue;
            ordered.Add(column);
        }

        return new ListViewColumnsResponse(view.ViewKey, ordered, isCustomised, supportsCustomFields);
    }

    private static ListViewDefinition Require(string viewKey) =>
        ListViewCatalog.Find(viewKey)
        ?? throw new ListViewNotFoundException($"'{viewKey}' is not a list view that supports column preferences.");

    private static void EnsureScope(long businessUnitId, long userId)
    {
        if (businessUnitId <= 0) throw new ListViewNotFoundException("A business unit is required.");
        if (userId <= 0) throw new ListViewNotFoundException("An authenticated user is required.");
    }
}
