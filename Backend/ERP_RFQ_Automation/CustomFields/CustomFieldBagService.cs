using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomFields;

/// <summary>One custom field as the client sees it on a record, with its current value.</summary>
public sealed record CustomFieldBagItem(
    string StableKey,
    string Label,
    CustomFieldDataType DataType,
    bool IsRequired,
    int DisplayOrder,
    IReadOnlyList<CustomFieldOptionResponse> Options,
    JsonElement? Value,
    string? DisplayValue,
    bool RequiresManagerAccess = false);

public sealed record CustomFieldBagResponse(
    string EntityType,
    long EntityId,
    IReadOnlyList<CustomFieldBagItem> Fields);

/// <summary>Set (or clear, with a JSON null) one or more custom-field values on a record.</summary>
/// <param name="Values">Key to value. A JSON null clears the key.</param>
/// <param name="EnforceRequired">
/// True for a full-record save from the value editor: every required field must end up with a
/// value, enforced HERE, at the boundary that persists — not in the browser. False for a
/// partial patch of a record that predates a newly-required field, so an unrelated edit is not
/// blocked by it.
/// </param>
public sealed record UpdateCustomFieldBagCommand(
    IReadOnlyDictionary<string, JsonElement> Values,
    bool EnforceRequired = true);

public interface ICustomFieldBagService
{
    /// <summary>The tenant's active custom fields for this record, with current values.</summary>
    /// <param name="managerOrAdmin">
    /// False hides fields the tenant declared sensitive (ViewAccess = ManagerOrAdmin), matching
    /// the gate the retired EAV path applied.
    /// </param>
    Task<CustomFieldBagResponse> GetAsync(
        long businessUnitId, string entityType, long entityId, bool managerOrAdmin, CancellationToken ct);

    /// <summary>Validates against declared types and persists. Throws on any type violation.</summary>
    Task<CustomFieldBagResponse> UpdateAsync(
        long businessUnitId, string entityType, long entityId,
        UpdateCustomFieldBagCommand command, bool managerOrAdmin, CancellationToken ct);

    /// <summary>Active definitions for an entity type, ordered for display. Used by list projections.</summary>
    Task<IReadOnlyList<CustomFieldBagItem>> ActiveFieldsAsync(
        long businessUnitId, string entityType, CancellationToken ct);
}

/// <summary>
/// Reads and writes the jsonb custom-field bag on the entities that carry one.
///
/// Attach points, and why these three (see the report / AA-01):
///   • LeadItem  — the lead/RFQ line. LeadItem.ExtraFields already proves that customer
///                 documents routinely carry per-line columns Nexora cannot map; those are
///                 exactly the columns a Sales Engineer asks for. This is the line grid the
///                 product owner was describing.
///   • Customer  — buyer-specific attributes (their vendor code for us, portal id, framework
///                 agreement reference) that a rep needs on every enquiry from that buyer.
///   • Supplier  — the mirror of the above on the sourcing side.
///
/// Every read and write goes through the tenant-filtered DbContext. The explicit
/// businessUnitId predicate is belt-and-braces on top of the global query filter, not a
/// substitute for it.
/// </summary>
public sealed class CustomFieldBagService : ICustomFieldBagService
{
    private readonly ErpRfqAutomationContext _context;

    public CustomFieldBagService(ErpRfqAutomationContext context) => _context = context;

    /// <summary>Entity types that carry a jsonb bag today.</summary>
    public static readonly IReadOnlySet<string> SupportedEntityTypes =
        new HashSet<string>(StringComparer.Ordinal) { "Customer", "Supplier", "LeadItem" };

    public async Task<CustomFieldBagResponse> GetAsync(
        long businessUnitId, string entityType, long entityId, bool managerOrAdmin, CancellationToken ct)
    {
        var canonical = RequireSupported(entityType);
        var definitions = await ActiveDefinitionsAsync(businessUnitId, canonical, ct);
        var bag = CustomFieldBag.Read(await LoadBagAsync(businessUnitId, canonical, entityId, ct));
        return new CustomFieldBagResponse(canonical, entityId, Project(definitions, bag, managerOrAdmin));
    }

    public async Task<CustomFieldBagResponse> UpdateAsync(
        long businessUnitId, string entityType, long entityId,
        UpdateCustomFieldBagCommand command, bool managerOrAdmin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var canonical = RequireSupported(entityType);
        var definitions = await ActiveDefinitionsAsync(businessUnitId, canonical, ct);

        // Sensitive-field gating, carried over from the retired EAV write path: a tenant user
        // must not be able to set a field the tenant declared manager-only just because the
        // browser sent the key.
        if (!managerOrAdmin)
        {
            foreach (var key in command.Values.Keys)
            {
                var target = definitions.FirstOrDefault(d => d.StableKey == key);
                var version = target?.Versions.FirstOrDefault(v => v.VersionNumber == target.ActiveVersionNumber);
                if (version is not null && version.EditAccess == CustomFieldAccessLevel.ManagerOrAdmin)
                    throw new CustomFieldConflictException(
                        $"Custom field '{key}' requires manager/admin access.");
            }
        }

        var updated = CustomFieldBagValidator.ValidateAndMerge(
            definitions,
            await LoadBagAsync(businessUnitId, canonical, entityId, ct),
            command.Values,
            command.EnforceRequired);

        await StoreBagAsync(businessUnitId, canonical, entityId, updated, ct);
        await _context.SaveChangesAsync(ct);

        return new CustomFieldBagResponse(
            canonical, entityId, Project(definitions, CustomFieldBag.Read(updated), managerOrAdmin));
    }

    public async Task<IReadOnlyList<CustomFieldBagItem>> ActiveFieldsAsync(
        long businessUnitId, string entityType, CancellationToken ct)
    {
        var canonical = RequireSupported(entityType);
        return Project(await ActiveDefinitionsAsync(businessUnitId, canonical, ct), CustomFieldBag.Read(null), true);
    }

    // -----------------------------------------------------------------------------------

    private async Task<IReadOnlyList<CustomFieldDefinition>> ActiveDefinitionsAsync(
        long businessUnitId, string entityType, CancellationToken ct) =>
        await _context.Set<CustomFieldDefinition>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && x.EntityType == entityType
                        && x.Status == CustomFieldDefinitionStatus.Active
                        && x.ActiveVersionNumber != null)
            .Include(x => x.Versions).ThenInclude(x => x.Options)
            .ToListAsync(ct);

    private async Task<string?> LoadBagAsync(
        long businessUnitId, string entityType, long entityId, CancellationToken ct) => entityType switch
    {
        "Customer" => await _context.Customers.AsNoTracking()
            .Where(x => x.Id == entityId && x.Buid == businessUnitId)
            .Select(x => x.CustomFieldsJson).FirstOrDefaultAsync(ct)
            ?? await EnsureExistsAsync(_context.Customers.AsNoTracking()
                .AnyAsync(x => x.Id == entityId && x.Buid == businessUnitId, ct), entityType, entityId),

        "Supplier" => await _context.Suppliers.AsNoTracking()
            .Where(x => x.Id == entityId && x.Buid == businessUnitId)
            .Select(x => x.CustomFieldsJson).FirstOrDefaultAsync(ct)
            ?? await EnsureExistsAsync(_context.Suppliers.AsNoTracking()
                .AnyAsync(x => x.Id == entityId && x.Buid == businessUnitId, ct), entityType, entityId),

        "LeadItem" => await _context.LeadItems.AsNoTracking()
            .Where(x => x.Id == entityId && x.Lead.BusinessUnitId == businessUnitId)
            .Select(x => x.CustomFieldsJson).FirstOrDefaultAsync(ct)
            ?? await EnsureExistsAsync(_context.LeadItems.AsNoTracking()
                .AnyAsync(x => x.Id == entityId && x.Lead.BusinessUnitId == businessUnitId, ct), entityType, entityId),

        _ => throw new CustomFieldDomainException($"'{entityType}' does not carry custom-field values.")
    };

    private async Task StoreBagAsync(
        long businessUnitId, string entityType, long entityId, string? json, CancellationToken ct)
    {
        switch (entityType)
        {
            case "Customer":
            {
                var row = await _context.Customers.FirstOrDefaultAsync(
                              x => x.Id == entityId && x.Buid == businessUnitId, ct)
                          ?? throw NotFound(entityType, entityId);
                row.CustomFieldsJson = json;
                return;
            }
            case "Supplier":
            {
                var row = await _context.Suppliers.FirstOrDefaultAsync(
                              x => x.Id == entityId && x.Buid == businessUnitId, ct)
                          ?? throw NotFound(entityType, entityId);
                row.CustomFieldsJson = json;
                return;
            }
            case "LeadItem":
            {
                var row = await _context.LeadItems.FirstOrDefaultAsync(
                              x => x.Id == entityId && x.Lead.BusinessUnitId == businessUnitId, ct)
                          ?? throw NotFound(entityType, entityId);
                row.CustomFieldsJson = json;
                return;
            }
            default:
                throw new CustomFieldDomainException($"'{entityType}' does not carry custom-field values.");
        }
    }

    private static IReadOnlyList<CustomFieldBagItem> Project(
        IReadOnlyList<CustomFieldDefinition> definitions,
        IReadOnlyDictionary<string, JsonElement> bag,
        bool managerOrAdmin)
    {
        var items = new List<CustomFieldBagItem>();
        foreach (var definition in definitions)
        {
            var version = definition.Versions.FirstOrDefault(v => v.VersionNumber == definition.ActiveVersionNumber);
            if (version is null) continue;
            // A sensitive field is not merely disabled for a tenant user — its VALUE never
            // leaves the server, because a disabled input still ships the data to the browser.
            if (!managerOrAdmin && version.ViewAccess == CustomFieldAccessLevel.ManagerOrAdmin) continue;
            JsonElement? value = bag.TryGetValue(definition.StableKey, out var stored) ? stored : null;
            items.Add(new CustomFieldBagItem(
                definition.StableKey,
                version.Label,
                version.DataType,
                version.IsRequired,
                definition.DisplayOrder,
                version.Options.OrderBy(o => o.DisplayOrder)
                    .Select(o => new CustomFieldOptionResponse(o.StableKey, o.Label, o.DisplayOrder)).ToArray(),
                value,
                value.HasValue ? CustomFieldBag.Display(value.Value) : null,
                version.EditAccess == CustomFieldAccessLevel.ManagerOrAdmin));
        }
        return items
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string RequireSupported(string entityType)
    {
        var canonical = CustomFieldApplicationService.CanonicalEntityType(entityType);
        if (!SupportedEntityTypes.Contains(canonical))
            throw new CustomFieldDomainException(
                $"'{canonical}' does not carry custom-field values yet.");
        return canonical;
    }

    /// <summary>
    /// Distinguishes "row exists, bag is null" (a legitimate empty result) from "row does not
    /// exist or belongs to another tenant" (a 404). Without this, a cross-tenant id would
    /// silently read as an empty bag rather than being refused.
    /// </summary>
    private static async Task<string?> EnsureExistsAsync(Task<bool> exists, string entityType, long entityId) =>
        await exists ? null : throw NotFound(entityType, entityId);

    private static CustomFieldNotFoundException NotFound(string entityType, long entityId) =>
        new($"{entityType} {entityId} was not found in this business unit.");

    /// <summary>
    /// True when at least one record already holds a value for this custom field.
    ///
    /// Checks both stores: the jsonb bag on the owning entity (the ratified design) and the
    /// legacy EAV <c>custom_field_values</c> table, which is read-only now but may still hold
    /// rows written before the write path was closed. Used to decide whether a data-type
    /// change is safe.
    ///
    /// The bag side is evaluated in memory over the bag column only, because a portable
    /// predicate on jsonb does not exist across both PostgreSQL and the SQLite test provider
    /// (PostgreSQL has no LIKE operator for jsonb). It reads one narrow column of the attach
    /// table and runs only on an administrator changing a field's type — a rare action — but
    /// it IS a scan, and that is a deliberate, documented trade rather than an oversight.
    /// </summary>
    public static async Task<bool> AnyStoredValueAsync(
        ErpRfqAutomationContext context, long businessUnitId, string canonicalEntityType,
        long definitionId, string stableKey, CancellationToken ct)
    {
        if (await context.Set<CustomFieldValue>()
                .AnyAsync(x => x.BusinessUnitId == businessUnitId && x.DefinitionId == definitionId, ct))
            return true;

        IQueryable<string?> bags = canonicalEntityType switch
        {
            "Customer" => context.Customers.AsNoTracking()
                .Where(x => x.Buid == businessUnitId).Select(x => x.CustomFieldsJson),
            "Supplier" => context.Suppliers.AsNoTracking()
                .Where(x => x.Buid == businessUnitId).Select(x => x.CustomFieldsJson),
            "LeadItem" => context.LeadItems.AsNoTracking()
                .Where(x => x.Lead.BusinessUnitId == businessUnitId).Select(x => x.CustomFieldsJson),
            // No bag on this entity type yet, so the EAV check above is the whole answer.
            _ => null!
        };
        if (bags is null) return false;

        foreach (var json in await bags.Where(x => x != null).ToListAsync(ct))
            if (CustomFieldBag.Read(json).ContainsKey(stableKey)) return true;

        return false;
    }
}
