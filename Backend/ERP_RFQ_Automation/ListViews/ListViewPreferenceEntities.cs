using System.Text.Json;

namespace ERP_RFQ_Automation.ListViews;

/// <summary>
/// One user's saved column layout for one list view inside one business unit.
///
/// Scoped by (BusinessUnitId, UserId, ViewKey) and never by tenant alone: the product
/// owner's requirement is explicitly "as per user preference not a fixed one", so a
/// manager reordering their grid must not move anybody else's columns. The business unit
/// is part of the key because the same user account can be attached to more than one
/// business unit and the two grids are separate working contexts.
///
/// The layout is stored as a jsonb array of {"key","visible"} objects in declared display
/// order. It is intentionally NOT a foreign key to anything: column keys come from a code
/// catalog and from tenant-defined custom fields, both of which evolve independently of
/// this row. A stale key is dropped on read, never enforced by the database.
/// </summary>
public sealed class ColumnPreference
{
    private ColumnPreference() { }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long UserId { get; private set; }
    public string ViewKey { get; private set; } = null!;

    /// <summary>jsonb array of <see cref="StoredColumn"/>. Never null; "[]" means "hide everything hideable".</summary>
    public string ColumnsJson { get; private set; } = "[]";

    public DateTime UpdatedOn { get; private set; }
    public string UpdatedBy { get; private set; } = null!;

    public static ColumnPreference Create(
        long businessUnitId, long userId, string viewKey,
        IReadOnlyList<StoredColumn> columns, string updatedBy, DateTime updatedOn)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (string.IsNullOrWhiteSpace(viewKey)) throw new ArgumentException("A view key is required.", nameof(viewKey));
        if (updatedOn.Kind != DateTimeKind.Utc) throw new ArgumentException("UpdatedOn must be UTC.", nameof(updatedOn));

        return new ColumnPreference
        {
            BusinessUnitId = businessUnitId,
            UserId = userId,
            ViewKey = viewKey.Trim(),
            ColumnsJson = StoredColumn.Serialize(columns),
            UpdatedBy = Actor(updatedBy),
            UpdatedOn = updatedOn
        };
    }

    public void Replace(IReadOnlyList<StoredColumn> columns, string updatedBy, DateTime updatedOn)
    {
        if (updatedOn.Kind != DateTimeKind.Utc) throw new ArgumentException("UpdatedOn must be UTC.", nameof(updatedOn));
        ColumnsJson = StoredColumn.Serialize(columns);
        UpdatedBy = Actor(updatedBy);
        UpdatedOn = updatedOn;
    }

    /// <summary>Tolerant read of the stored layout. Malformed payloads yield an empty list, never an exception.</summary>
    public IReadOnlyList<StoredColumn> Columns() => StoredColumn.Deserialize(ColumnsJson);

    private static string Actor(string? value)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.Length == 0) throw new ArgumentException("An actor is required.", nameof(value));
        return result.Length > 200 ? result[..200] : result;
    }
}

/// <summary>A single entry in a stored layout: which column, and whether it is shown.</summary>
public sealed record StoredColumn(string Key, bool Visible)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<StoredColumn> columns) =>
        JsonSerializer.Serialize(columns, SerializerOptions);

    /// <summary>
    /// Tolerant parse. Malformed JSON, a non-array payload, null entries or blank keys all
    /// degrade to "no stored preference" rather than throwing — a corrupt row must never be
    /// able to take a user's grid offline.
    /// </summary>
    public static IReadOnlyList<StoredColumn> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<StoredColumn?>>(json, SerializerOptions);
            if (parsed is null) return [];
            return parsed
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => x! with { Key = x!.Key.Trim() })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
