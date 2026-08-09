using ERP_RFQ_Automation.CustomFields;

namespace ERP_RFQ_Automation.ListViews;

/// <summary>A resolved column: what the client should render, in the order it should render it.</summary>
/// <param name="Key">Stable column key. <c>cf:&lt;stableKey&gt;</c> for tenant-defined custom fields.</param>
/// <param name="Label">Display label. For custom fields this is the tenant's own label.</param>
/// <param name="Visible">Whether this user wants it shown.</param>
/// <param name="Locked">Cannot be hidden (may still be reordered).</param>
/// <param name="Source">"catalog" for a built-in column, "customField" for a tenant-defined one.</param>
/// <param name="DataType">Declared type of the backing custom field; null for catalog columns.</param>
/// <param name="StableKey">Custom-field stable key without the prefix; null for catalog columns.</param>
public sealed record ResolvedColumn(
    string Key,
    string Label,
    bool Visible,
    bool Locked,
    string Source,
    CustomFieldDataType? DataType = null,
    string? StableKey = null);

/// <summary>The full column contract for one user on one view.</summary>
/// <param name="IsCustomised">
/// True when this user has a stored preference for this view. Drives whether the client
/// offers "Reset to default".
/// </param>
/// <param name="SupportsCustomFields">
/// False when this view has no custom-field attach point. The client uses it to decide whether
/// to say "fields your organisation adds appear here" — a promise it must not make on a grid
/// where no such field can ever appear.
/// </param>
public sealed record ListViewColumnsResponse(
    string ViewKey,
    IReadOnlyList<ResolvedColumn> Columns,
    bool IsCustomised,
    bool SupportsCustomFields);

/// <summary>Save payload: the complete ordered layout the user now wants.</summary>
public sealed record SaveColumnPreferenceCommand(IReadOnlyList<StoredColumn> Columns);

public sealed class ListViewNotFoundException(string message) : Exception(message);

public interface IListViewPreferenceService
{
    Task<ListViewColumnsResponse> GetAsync(long businessUnitId, long userId, string viewKey, CancellationToken ct);

    Task<ListViewColumnsResponse> SaveAsync(
        long businessUnitId, long userId, string viewKey,
        SaveColumnPreferenceCommand command, string actor, CancellationToken ct);

    /// <summary>Deletes the user's stored layout for this view and returns the declared default.</summary>
    Task<ListViewColumnsResponse> ResetAsync(long businessUnitId, long userId, string viewKey, CancellationToken ct);
}
