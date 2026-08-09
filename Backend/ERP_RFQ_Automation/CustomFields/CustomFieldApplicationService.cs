using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomFields;

public interface ICustomFieldApplicationService
{
    Task<IReadOnlyList<CustomFieldDefinitionResponse>> ListDefinitionsAsync(
        long businessUnitId, string? entityType, CancellationToken ct);
    Task<string> GetDefinitionEntityTypeAsync(long businessUnitId, long definitionId, CancellationToken ct);
    Task<CustomFieldDefinitionResponse> CreateDefinitionAsync(
        long businessUnitId, CreateCustomFieldDefinitionCommand command, string actor, CancellationToken ct);
    Task<CustomFieldDefinitionResponse> AddVersionAsync(
        long businessUnitId, long definitionId, AddCustomFieldVersionCommand command, string actor, CancellationToken ct);
    Task<CustomFieldDefinitionResponse> ActivateVersionAsync(
        long businessUnitId, long definitionId, int versionNumber, CancellationToken ct);
    Task<CustomFieldDefinitionResponse> RetireDefinitionAsync(
        long businessUnitId, long definitionId, RetireCustomFieldDefinitionCommand command,
        string actor, CancellationToken ct);
    Task<CustomFieldDefinitionResponse> ReactivateDefinitionAsync(
        long businessUnitId, long definitionId, CancellationToken ct);
    Task<IReadOnlyList<CustomFieldDefinitionResponse>> ReorderDefinitionsAsync(
        long businessUnitId, ReorderCustomFieldsCommand command, CancellationToken ct);
    Task<CustomFieldEntitySchemaResponse> GetEntitySchemaAsync(
        long businessUnitId, string entityType, long entityId, bool managerOrAdmin, CancellationToken ct);

    /// <summary>RETIRED — always throws <see cref="CustomFieldWritePathRetiredException"/>.</summary>
    Task<CustomFieldValueResponse> UpsertValueAsync(
        long businessUnitId, string entityType, long entityId, string stableKey,
        UpsertCustomFieldValueCommand command, string actor, bool managerOrAdmin, CancellationToken ct);
}

public sealed class CustomFieldApplicationService : ICustomFieldApplicationService
{
    private const int MaximumDefinitionsPerEntity = 100;
    private const int MaximumVersionsPerDefinition = 20;
    private const int MaximumOptionsPerVersion = 500;
    private const int MaximumRulesPerVersion = 50;
    private const int MaximumDependenciesPerVersion = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ErpRfqAutomationContext _db;

    public CustomFieldApplicationService(ErpRfqAutomationContext db) => _db = db;

    public async Task<IReadOnlyList<CustomFieldDefinitionResponse>> ListDefinitionsAsync(
        long businessUnitId, string? entityType, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        var query = DefinitionGraph().AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId);
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var canonical = CanonicalEntityType(entityType);
            query = query.Where(x => x.EntityType == canonical);
        }

        return (await query.OrderBy(x => x.EntityType).ThenBy(x => x.DisplayOrder)
                .ThenBy(x => x.StableKey).ToListAsync(ct))
            .Select(ToResponse).ToArray();
    }

    public async Task<string> GetDefinitionEntityTypeAsync(
        long businessUnitId, long definitionId, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        return await _db.Set<CustomFieldDefinition>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == definitionId)
            .Select(x => x.EntityType).SingleOrDefaultAsync(ct)
            ?? throw new CustomFieldNotFoundException($"Custom-field definition {definitionId} was not found.");
    }

    public async Task<CustomFieldDefinitionResponse> CreateDefinitionAsync(
        long businessUnitId, CreateCustomFieldDefinitionCommand command, string actor, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        actor = CustomFieldDefinition.Require(actor, nameof(actor), 200);
        var entityType = CanonicalEntityType(command.EntityType);
        var stableKey = CustomFieldGovernance.NormalizeAndValidateStableKey(command.StableKey);
        if (await _db.Set<CustomFieldDefinition>().CountAsync(
                x => x.BusinessUnitId == businessUnitId && x.EntityType == entityType, ct) >= MaximumDefinitionsPerEntity)
            throw new CustomFieldConflictException(
                $"An entity cannot have more than {MaximumDefinitionsPerEntity} custom-field definitions.");
        if (await _db.Set<CustomFieldDefinition>().AnyAsync(
                x => x.BusinessUnitId == businessUnitId && x.EntityType == entityType && x.StableKey == stableKey, ct))
            throw new CustomFieldConflictException($"Custom field '{stableKey}' already exists for {entityType}.");

        try
        {
            return await InTransactionAsync(async () =>
            {
                var definition = CustomFieldDefinition.Create(
                    businessUnitId, entityType, stableKey, actor, DateTime.UtcNow);
                definition.SetDisplayOrder(command.DisplayOrder);
                var version = definition.AddVersion(command.Version, actor, DateTime.UtcNow);
                await PopulateVersionAsync(businessUnitId, entityType, version, command.Options, command.Rules,
                    command.DependencyDefinitionIds, ct);
                _db.Add(definition);
                await _db.SaveChangesAsync(ct);

                if (command.Activate)
                {
                    await ValidateActivationAsync(businessUnitId, definition.Id, version.VersionNumber, ct);
                    definition.ActivateVersion(version.VersionNumber);
                    await _db.SaveChangesAsync(ct);
                }
                return ToResponse(definition);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                "Custom-field definition changed since it was loaded. Refresh and retry.");
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                $"Custom field '{stableKey}' was created concurrently. Refresh and retry.");
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<CustomFieldDefinitionResponse> AddVersionAsync(
        long businessUnitId, long definitionId, AddCustomFieldVersionCommand command,
        string actor, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        actor = CustomFieldDefinition.Require(actor, nameof(actor), 200);
        try
        {
            return await InTransactionAsync(async () =>
            {
                var definition = await DefinitionGraph().SingleOrDefaultAsync(
                    x => x.BusinessUnitId == businessUnitId && x.Id == definitionId, ct)
                    ?? throw new CustomFieldNotFoundException($"Custom-field definition {definitionId} was not found.");
                if (definition.Versions.Count >= MaximumVersionsPerDefinition)
                    throw new CustomFieldConflictException(
                        $"A custom field cannot have more than {MaximumVersionsPerDefinition} versions.");

                await EnsureDataTypeChangeIsSafeAsync(businessUnitId, definition, command.Version.DataType, ct);

                var version = definition.AddVersion(command.Version, actor, DateTime.UtcNow);
                await PopulateVersionAsync(businessUnitId, definition.EntityType, version, command.Options, command.Rules,
                    command.DependencyDefinitionIds, ct);
                await _db.SaveChangesAsync(ct);
                if (command.Activate)
                {
                    await ValidateActivationAsync(businessUnitId, definition.Id, version.VersionNumber, ct);
                    definition.ActivateVersion(version.VersionNumber);
                    await _db.SaveChangesAsync(ct);
                }
                return ToResponse(definition);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                "Custom-field definition changed since it was loaded. Refresh and retry.");
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<CustomFieldDefinitionResponse> ActivateVersionAsync(
        long businessUnitId, long definitionId, int versionNumber, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        try
        {
            return await InTransactionAsync(async () =>
            {
                var definition = await DefinitionGraph().SingleOrDefaultAsync(
                    x => x.BusinessUnitId == businessUnitId && x.Id == definitionId, ct)
                    ?? throw new CustomFieldNotFoundException($"Custom-field definition {definitionId} was not found.");
                await ValidateActivationAsync(businessUnitId, definitionId, versionNumber, ct);
                definition.ActivateVersion(versionNumber);
                await _db.SaveChangesAsync(ct);
                return ToResponse(definition);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                "Custom-field definition changed since it was loaded. Refresh and retry.");
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<CustomFieldDefinitionResponse> RetireDefinitionAsync(
        long businessUnitId, long definitionId, RetireCustomFieldDefinitionCommand command,
        string actor, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        try
        {
            return await InTransactionAsync(async () =>
            {
                var definition = await DefinitionGraph().SingleOrDefaultAsync(
                    x => x.BusinessUnitId == businessUnitId && x.Id == definitionId, ct)
                    ?? throw new CustomFieldNotFoundException($"Custom-field definition {definitionId} was not found.");
                var hasActiveDependents = await _db.Set<CustomFieldDependency>().AnyAsync(x =>
                    x.DependsOnDefinitionId == definitionId &&
                    x.Version.Definition.BusinessUnitId == businessUnitId &&
                    x.Version.Definition.Status == CustomFieldDefinitionStatus.Active &&
                    x.Version.Definition.ActiveVersionNumber == x.Version.VersionNumber, ct);
                if (hasActiveDependents)
                    throw new CustomFieldConflictException(
                        "The custom field cannot be retired while another active field depends on it.");
                definition.Retire(actor, command.Reason, DateTime.UtcNow);
                await _db.SaveChangesAsync(ct);
                return ToResponse(definition);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                "Custom-field definition changed since it was loaded. Refresh and retry.");
        }
    }

    /// <summary>
    /// AA-01 · reactivation. Brings a retired field back at its last active version so a
    /// manager can undo a withdrawal without inventing a second field with a near-identical
    /// key (which would fragment the data for good).
    /// </summary>
    public async Task<CustomFieldDefinitionResponse> ReactivateDefinitionAsync(
        long businessUnitId, long definitionId, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        try
        {
            return await InTransactionAsync(async () =>
            {
                var definition = await DefinitionGraph().SingleOrDefaultAsync(
                    x => x.BusinessUnitId == businessUnitId && x.Id == definitionId, ct)
                    ?? throw new CustomFieldNotFoundException($"Custom-field definition {definitionId} was not found.");
                definition.Reactivate();
                await _db.SaveChangesAsync(ct);
                return ToResponse(definition);
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                "Custom-field definition changed since it was loaded. Refresh and retry.");
        }
    }

    /// <summary>
    /// AA-01 · repositions a whole entity's custom fields in one call.
    ///
    /// A batch rather than one call per arrow click, and unversioned, so dragging the admin
    /// list into shape does not consume the 20-version-per-field budget that exists to keep
    /// LABEL and TYPE history intact.
    /// </summary>
    public async Task<IReadOnlyList<CustomFieldDefinitionResponse>> ReorderDefinitionsAsync(
        long businessUnitId, ReorderCustomFieldsCommand command, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        ArgumentNullException.ThrowIfNull(command);
        var entityType = CanonicalEntityType(command.EntityType);

        try
        {
            return await InTransactionAsync(async () =>
            {
                var definitions = await DefinitionGraph()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.EntityType == entityType)
                    .ToListAsync(ct);
                var byId = definitions.ToDictionary(x => x.Id);

                foreach (var entry in command.Order ?? [])
                {
                    // An id this tenant does not own simply is not in the map — a reorder can
                    // never be used to probe for, or touch, another tenant's definitions.
                    if (!byId.TryGetValue(entry.DefinitionId, out var definition)) continue;
                    if (definition.Status == CustomFieldDefinitionStatus.Retired) continue;
                    definition.SetDisplayOrder(entry.DisplayOrder);
                }

                await _db.SaveChangesAsync(ct);
                return (IReadOnlyList<CustomFieldDefinitionResponse>)definitions
                    .OrderBy(x => x.DisplayOrder).ThenBy(x => x.StableKey)
                    .Select(ToResponse).ToArray();
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CustomFieldConflictException(
                "Custom-field definitions changed since they were loaded. Refresh and retry.");
        }
    }

    /// <summary>
    /// Refuses a data-type change on a field that already holds values.
    ///
    /// Silent coercion is the failure mode being prevented: a Text field holding "1,200 SAR"
    /// re-declared as Decimal would either throw on every subsequent read or quietly become
    /// unreadable. Neither is acceptable for data a customer typed. The field must be retired
    /// and replaced, which keeps the old values readable under the old declaration.
    /// </summary>
    private async Task EnsureDataTypeChangeIsSafeAsync(
        long businessUnitId, CustomFieldDefinition definition, CustomFieldDataType requestedType, CancellationToken ct)
    {
        var current = definition.ActiveVersionNumber.HasValue
            ? definition.Versions.SingleOrDefault(x => x.VersionNumber == definition.ActiveVersionNumber.Value)
            : null;
        if (current is null || current.DataType == requestedType) return;

        if (await CustomFieldBagService.AnyStoredValueAsync(
                _db, businessUnitId, definition.EntityType, definition.Id, definition.StableKey, ct))
            throw new CustomFieldConflictException(
                $"'{definition.StableKey}' already holds values, so its type cannot change from " +
                $"{current.DataType} to {requestedType}. Existing values would stop matching their own " +
                "declaration and Nexora will not silently convert them. Retire this field and create a " +
                "replacement instead — retiring keeps every value already captured readable.");
    }

    public async Task<CustomFieldEntitySchemaResponse> GetEntitySchemaAsync(
        long businessUnitId, string entityType, long entityId, bool managerOrAdmin, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        var canonical = CanonicalEntityType(entityType);
        await EnsureEntityExistsAsync(businessUnitId, canonical, entityId, ct);
        var definitions = await DefinitionGraph().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.EntityType == canonical &&
                        x.Status == CustomFieldDefinitionStatus.Active)
            .OrderBy(x => x.StableKey).ToListAsync(ct);
        var allDefinitionIds = definitions.Select(x => x.Id).ToArray();
        var allValues = await _db.Set<CustomFieldValue>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && allDefinitionIds.Contains(x.DefinitionId) &&
                        x.Record.EntityType == canonical && x.Record.EntityId == entityId)
            .ToDictionaryAsync(x => x.DefinitionId, ct);
        var valuesByKey = definitions.Where(x => allValues.ContainsKey(x.Id)).ToDictionary(
            x => x.StableKey, x => ToInput(allValues[x.Id]), StringComparer.OrdinalIgnoreCase);
        var visible = definitions.Where(x =>
        {
            var version = ActiveVersion(x);
            var rules = ConditionalRuleEvaluator.Evaluate(version, valuesByKey);
            return rules.IsVisible &&
                   (managerOrAdmin || version.ViewAccess == CustomFieldAccessLevel.TenantUser);
        }).ToArray();

        return new CustomFieldEntitySchemaResponse(canonical, entityId, visible.Select(definition =>
        {
            var version = ActiveVersion(definition);
            var state = ConditionalRuleEvaluator.Evaluate(version, valuesByKey);
            allValues.TryGetValue(definition.Id, out var value);
            return new CustomFieldSchemaItemResponse(
                definition.Id, definition.StableKey, ToResponse(version),
                value == null ? null : ToResponse(value, definition.StableKey),
                state.IsRequired, state.IsReadOnly);
        }).ToArray());
    }

    /// <summary>
    /// RETIRED WRITE PATH — fails closed.
    ///
    /// AA-01 ratified ONE storage shape for custom-field values: a single jsonb bag on the
    /// owning row, validated against the tenant's field definitions. This method wrote the
    /// separate EAV <c>custom_field_values</c> table. Two unsynchronised stores for one
    /// concept diverge; the only question is when. The bag is the survivor.
    ///
    /// The method is kept and fails loudly rather than being deleted so that any caller —
    /// including an integration written against the old route — gets a message naming the
    /// replacement instead of a 404 that reads like a deployment problem.
    ///
    /// The READ path (<see cref="GetEntitySchemaAsync"/>) is untouched: rows already in
    /// <c>custom_field_values</c> stay readable. The table itself is not dropped here.
    ///
    /// Behaviour NOT carried over to the bag path, stated plainly rather than implied:
    /// per-value optimistic concurrency, idempotency keys, and
    /// <c>custom_field_value_history</c> audit rows. Conditional show/hide/require rules
    /// (<see cref="ConditionalRuleEvaluator"/>) are likewise not evaluated on bag writes.
    /// Sensitive-field (manager/admin) gating IS carried over — see
    /// <see cref="CustomFieldBagService"/>.
    /// </summary>
    public Task<CustomFieldValueResponse> UpsertValueAsync(
        long businessUnitId, string entityType, long entityId, string stableKey,
        UpsertCustomFieldValueCommand command, string actor, bool managerOrAdmin, CancellationToken ct) =>
        throw new CustomFieldWritePathRetiredException(
            "This custom-field value write path has been retired. Custom-field values are stored in the " +
            "jsonb bag on the record itself. Use PUT /api/custom-fields/records/{entityType}/{entityId} " +
            "instead. Values already written here remain readable.");

    private async Task PopulateVersionAsync(
        long businessUnitId, string entityType, CustomFieldVersion version,
        IReadOnlyList<CustomFieldOptionDraft>? options, IReadOnlyList<CustomFieldRuleDraft>? rules,
        IReadOnlyList<long>? dependencyIds, CancellationToken ct)
    {
        if (!Enum.IsDefined(version.ViewAccess) || !Enum.IsDefined(version.EditAccess))
            throw new CustomFieldDomainException("Custom-field access level is invalid.");
        if ((options?.Count ?? 0) > MaximumOptionsPerVersion)
            throw new CustomFieldDomainException($"A version cannot have more than {MaximumOptionsPerVersion} options.");
        if ((rules?.Count ?? 0) > MaximumRulesPerVersion)
            throw new CustomFieldDomainException($"A version cannot have more than {MaximumRulesPerVersion} rules.");
        if ((dependencyIds?.Count ?? 0) > MaximumDependenciesPerVersion)
            throw new CustomFieldDomainException(
                $"A version cannot have more than {MaximumDependenciesPerVersion} dependencies.");
        foreach (var option in options ?? Array.Empty<CustomFieldOptionDraft>())
            version.AddOption(option.StableKey, option.Label, option.DisplayOrder);
        foreach (var rule in rules ?? Array.Empty<CustomFieldRuleDraft>())
            version.AddRule(rule.Effect, rule.Condition);

        var ids = (dependencyIds ?? Array.Empty<long>()).Distinct().ToArray();
        if (ids.Length != (dependencyIds?.Count ?? 0))
            throw new CustomFieldDomainException("Dependency definitions cannot contain duplicates.");
        if (ids.Length == 0) return;
        var validIds = await _db.Set<CustomFieldDefinition>().Where(x =>
                x.BusinessUnitId == businessUnitId && x.EntityType == entityType && ids.Contains(x.Id) &&
                x.Status != CustomFieldDefinitionStatus.Retired)
            .Select(x => x.Id).ToArrayAsync(ct);
        if (validIds.Length != ids.Length)
            throw new CustomFieldDomainException("Every dependency must be a non-retired definition for the same tenant and entity type.");
        foreach (var id in ids) version.AddDependency(id);
    }

    private async Task ValidateActivationAsync(
        long businessUnitId, long definitionId, int versionNumber, CancellationToken ct)
    {
        var definitions = await DefinitionGraph().Where(x => x.BusinessUnitId == businessUnitId).ToListAsync(ct);
        var definition = definitions.SingleOrDefault(x => x.Id == definitionId)
            ?? throw new CustomFieldNotFoundException($"Custom-field definition {definitionId} was not found.");
        var candidate = definition.Versions.SingleOrDefault(x => x.VersionNumber == versionNumber)
            ?? throw new CustomFieldNotFoundException($"Custom-field version {versionNumber} was not found.");
        if (candidate.DataType is CustomFieldDataType.Option or CustomFieldDataType.MultiOption && candidate.Options.Count == 0)
            throw new CustomFieldDomainException("Option fields require at least one option before activation.");
        var existingValues = await _db.Set<CustomFieldValue>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.DefinitionId == definitionId)
            .ToListAsync(ct);
        foreach (var existingValue in existingValues)
        {
            try { CustomFieldValueValidator.Validate(candidate, ToInput(existingValue)); }
            catch (CustomFieldDomainException ex)
            {
                throw new CustomFieldConflictException(
                    $"Version {versionNumber} is incompatible with existing value {existingValue.Id}: {ex.Message}");
            }
        }

        var entityDefinitions = definitions.Where(x => x.EntityType == definition.EntityType &&
                                                        x.Status != CustomFieldDefinitionStatus.Retired).ToArray();
        var byKey = entityDefinitions.ToDictionary(x => x.StableKey, StringComparer.OrdinalIgnoreCase);
        var dependencyIds = candidate.Dependencies.Select(x => x.DependsOnDefinitionId).ToHashSet();
        foreach (var rule in candidate.Rules)
        {
            foreach (var key in ConditionalRuleValidator.ReferencedFieldKeys(rule.Condition))
            {
                if (!byKey.TryGetValue(key, out var referenced))
                    throw new CustomFieldDomainException($"Conditional rule references unknown field '{key}'.");
                if (referenced.Id != definition.Id && !dependencyIds.Contains(referenced.Id))
                    throw new CustomFieldDomainException($"Conditional rule field '{key}' must be declared as a dependency.");
            }
        }
        if (candidate.Dependencies.Any(x => entityDefinitions.Single(d => d.Id == x.DependsOnDefinitionId).Status !=
                                            CustomFieldDefinitionStatus.Active))
            throw new CustomFieldDomainException("Dependency definitions must be active before this version can be activated.");

        var edges = new List<(long DefinitionId, long DependsOnDefinitionId)>();
        foreach (var current in entityDefinitions)
        {
            var selected = current.Id == definitionId
                ? candidate
                : current.ActiveVersionNumber.HasValue
                    ? current.Versions.SingleOrDefault(x => x.VersionNumber == current.ActiveVersionNumber.Value)
                    : null;
            if (selected != null)
                edges.AddRange(selected.Dependencies.Select(x => (current.Id, x.DependsOnDefinitionId)));
        }
        CustomFieldDependencyGraph.EnsureAcyclic(edges);
    }


    private async Task EnsureEntityExistsAsync(
        long businessUnitId, string entityType, long entityId, CancellationToken ct)
    {
        if (entityId <= 0) throw new CustomFieldNotFoundException("A persisted entity ID is required.");
        var exists = entityType switch
        {
            "CommercialCase" => await _db.CommercialCases.AnyAsync(x => x.Id == entityId && x.BusinessUnitId == businessUnitId, ct),
            "Lead" => await _db.Leads.AnyAsync(x => x.Id == entityId && x.BusinessUnitId == businessUnitId, ct),
            "Rfq" => await _db.Rfqs.AnyAsync(x => x.Id == entityId && x.BusinessUnitId == businessUnitId, ct),
            "Quote" => await _db.Quotes.AnyAsync(x => x.Id == entityId && x.BusinessUnitId == businessUnitId, ct),
            "Order" => await _db.Orders.AnyAsync(x => x.Id == entityId && x.BusinessUnitId == businessUnitId, ct),
            "Shipment" => await _db.Shipments.AnyAsync(x => x.Id == entityId && x.BusinessUnitId == businessUnitId, ct),
            "Customer" => await _db.Customers.AnyAsync(x => x.Id == entityId && x.Buid == businessUnitId, ct),
            "Supplier" => await _db.Suppliers.AnyAsync(x => x.Id == entityId && x.Buid == businessUnitId, ct),
            "Product" => await _db.Products.AnyAsync(x => x.Id == entityId && x.Buid == businessUnitId, ct),
            _ => false
        };
        if (!exists) throw new CustomFieldNotFoundException($"{entityType} {entityId} was not found in this tenant.");
    }


    private IQueryable<CustomFieldDefinition> DefinitionGraph() => _db.Set<CustomFieldDefinition>()
        .Include(x => x.Versions).ThenInclude(x => x.Options)
        .Include(x => x.Versions).ThenInclude(x => x.Rules)
        .Include(x => x.Versions).ThenInclude(x => x.Dependencies);




    private static CustomFieldValueInput ToInput(CustomFieldValue value) => new(
        value.TextValue, value.IntegerValue, value.DecimalValue, value.BooleanValue,
        value.DateValue, value.TimestampValue, value.JsonValue, value.ReferenceType, value.ReferenceId);

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private static CustomFieldVersion ActiveVersion(CustomFieldDefinition definition) =>
        definition.ActiveVersionNumber.HasValue
            ? definition.Versions.Single(x => x.VersionNumber == definition.ActiveVersionNumber.Value)
            : throw new CustomFieldDomainException($"Active definition '{definition.StableKey}' has no active version.");

    private static CustomFieldDefinitionResponse ToResponse(CustomFieldDefinition definition) => new(
        definition.Id, definition.EntityType, definition.StableKey, definition.Status,
        definition.ActiveVersionNumber, definition.Versions.OrderBy(x => x.VersionNumber).Select(ToResponse).ToArray(),
        definition.CreatedOn, definition.CreatedBy, definition.RetiredOn, definition.RetiredBy,
        definition.RetirementReason, definition.Version, definition.DisplayOrder);

    private static CustomFieldVersionResponse ToResponse(CustomFieldVersion version) => new(
        version.VersionNumber, version.Label, version.HelpText, version.DataType, version.IsRequired,
        version.MinimumLength, version.MaximumLength, version.MinimumValue, version.MaximumValue,
        version.DefaultValueJson, version.IsSensitive, version.IsSearchable, version.ViewAccess, version.EditAccess,
        version.Options.OrderBy(x => x.DisplayOrder).ThenBy(x => x.StableKey)
            .Select(x => new CustomFieldOptionResponse(x.StableKey, x.Label, x.DisplayOrder)).ToArray(),
        version.Rules.Select(x => new CustomFieldRuleResponse(x.Effect, x.Condition)).ToArray(),
        version.Dependencies.Select(x => x.DependsOnDefinitionId).Order().ToArray(),
        version.CreatedOn, version.CreatedBy);

    private static CustomFieldValueResponse ToResponse(CustomFieldValue value, string stableKey) => new(
        value.Id, value.DefinitionId, stableKey, value.DefinitionVersion,
        ToInput(value),
        value.Version, value.UpdatedOn, value.UpdatedBy);

    public static string CanonicalEntityType(string entityType)
    {
        var normalized = CustomFieldGovernance.ValidateEntityType(entityType);
        return normalized.ToLowerInvariant() switch
        {
            "commercialcase" or "commercial_case" => "CommercialCase",
            "lead" => "Lead",
            // AA-01: the lead/RFQ LINE, distinct from the lead header. This is the grid the
            // product owner was pointing at when he asked for configurable columns.
            "leaditem" or "lead_item" or "leadline" => "LeadItem",
            "rfq" => "Rfq",
            "quote" or "quotation" => "Quote",
            "order" => "Order",
            "shipment" => "Shipment",
            "customer" => "Customer",
            "supplier" => "Supplier",
            "product" => "Product",
            _ => throw new CustomFieldDomainException($"Entity type '{normalized}' does not support governed custom fields.")
        };
    }



    private static void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0) throw new CustomFieldConflictException("Business Unit ID is required.");
    }
}
