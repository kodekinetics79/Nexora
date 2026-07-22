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
    Task<CustomFieldEntitySchemaResponse> GetEntitySchemaAsync(
        long businessUnitId, string entityType, long entityId, bool managerOrAdmin, CancellationToken ct);
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

        return (await query.OrderBy(x => x.EntityType).ThenBy(x => x.StableKey).ToListAsync(ct))
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

    public async Task<CustomFieldValueResponse> UpsertValueAsync(
        long businessUnitId, string entityType, long entityId, string stableKey,
        UpsertCustomFieldValueCommand command, string actor, bool managerOrAdmin, CancellationToken ct)
    {
        EnsureTenant(businessUnitId);
        actor = CustomFieldDefinition.Require(actor, nameof(actor), 200);
        var canonical = CanonicalEntityType(entityType);
        stableKey = CustomFieldGovernance.NormalizeAndValidateStableKey(stableKey);
        ValidateMutationMetadata(command);
        var requestHash = HashRequest(command);

        await EnsureEntityExistsAsync(businessUnitId, canonical, entityId, ct);
        var authorizedDefinition = await DefinitionGraph().AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.EntityType == canonical && x.StableKey == stableKey &&
            x.Status == CustomFieldDefinitionStatus.Active, ct)
            ?? throw new CustomFieldNotFoundException($"Active custom field '{stableKey}' was not found for {canonical}.");
        var authorizedVersion = ActiveVersion(authorizedDefinition);
        if (!managerOrAdmin && authorizedVersion.EditAccess == CustomFieldAccessLevel.ManagerOrAdmin)
            throw new CustomFieldConflictException($"Custom field '{stableKey}' requires manager/admin access.");
        var authorizedRuleState = await GetRuleStateAsync(
            businessUnitId, canonical, entityId, authorizedDefinition, ct);
        EnforceRuleState(stableKey, command.Value, authorizedRuleState);
        await ValidateReferenceAsync(businessUnitId, command.Value, ct);
        var replay = await ReplayAsync(
            businessUnitId, command.IdempotencyKey, canonical, entityId, authorizedDefinition.Id,
            actor, requestHash, ct);
        if (replay != null) return replay;

        try
        {
            return await InTransactionAsync(async () =>
            {
                await EnsureEntityExistsAsync(businessUnitId, canonical, entityId, ct);
                var definition = await DefinitionGraph().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.EntityType == canonical && x.StableKey == stableKey &&
                x.Status == CustomFieldDefinitionStatus.Active, ct)
                ?? throw new CustomFieldNotFoundException($"Active custom field '{stableKey}' was not found for {canonical}.");
            var version = ActiveVersion(definition);
            if (!managerOrAdmin && version.EditAccess == CustomFieldAccessLevel.ManagerOrAdmin)
                throw new CustomFieldConflictException($"Custom field '{stableKey}' requires manager/admin access.");
            var ruleState = await GetRuleStateAsync(businessUnitId, canonical, entityId, definition, ct);
            EnforceRuleState(stableKey, command.Value, ruleState);
            await ValidateReferenceAsync(businessUnitId, command.Value, ct);

            var record = await _db.Set<CustomFieldRecord>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.EntityType == canonical && x.EntityId == entityId, ct);
            if (record == null)
            {
                record = CustomFieldRecord.Create(businessUnitId, canonical, entityId, DateTime.UtcNow);
                _db.Add(record);
                await _db.SaveChangesAsync(ct);
            }

            var value = await _db.Set<CustomFieldValue>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.RecordId == record.Id && x.DefinitionId == definition.Id, ct);
            CustomFieldValueResponse? before = null;
            string changeType;
            if (value == null)
            {
                if (command.ExpectedVersion.HasValue)
                    throw new CustomFieldConflictException("The custom-field value does not exist; expectedVersion must be omitted.");
                value = CustomFieldValue.Create(businessUnitId, record.Id, definition.Id, version,
                    command.Value, actor, DateTime.UtcNow);
                _db.Add(value);
                await _db.SaveChangesAsync(ct);
                changeType = "Created";
            }
            else
            {
                if (!command.ExpectedVersion.HasValue)
                    throw new CustomFieldConflictException("expectedVersion is required when updating a custom-field value.");
                if (value.Version != command.ExpectedVersion.Value)
                    throw new CustomFieldConflictException(
                        "Custom-field value changed since it was loaded. Refresh and retry.");
                before = ToResponse(value, definition.StableKey);
                value.Update(version, command.Value, actor, DateTime.UtcNow, command.ExpectedVersion.Value);
                changeType = "Updated";
            }

            var after = ToResponse(value, definition.StableKey);
            _db.Add(CustomFieldValueHistory.Create(
                businessUnitId, value.Id, changeType,
                before == null ? null : JsonSerializer.Serialize(before, JsonOptions),
                JsonSerializer.Serialize(after, JsonOptions), actor, DateTime.UtcNow,
                command.CorrelationId, command.IdempotencyKey, requestHash, command.Reason));
                await _db.SaveChangesAsync(ct);
                return after;
            }, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CustomFieldConflictException(
                "Custom-field value changed since it was loaded. Refresh and retry.");
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrentReplay = await ReplayAsync(
                businessUnitId, command.IdempotencyKey, canonical, entityId, authorizedDefinition.Id,
                actor, requestHash, ct);
            if (concurrentReplay != null) return concurrentReplay;
            throw;
        }
    }

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

    private async Task ValidateReferenceAsync(long businessUnitId, CustomFieldValueInput input, CancellationToken ct)
    {
        if (!input.ReferenceId.HasValue) return;
        var referenceType = CanonicalEntityType(input.ReferenceType!);
        await EnsureEntityExistsAsync(businessUnitId, referenceType, input.ReferenceId.Value, ct);
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

    private async Task<CustomFieldValueResponse?> ReplayAsync(
        long businessUnitId, string idempotencyKey, string entityType, long entityId,
        long definitionId, string actor, string requestHash, CancellationToken ct)
    {
        var history = await (
            from item in _db.Set<CustomFieldValueHistory>().AsNoTracking()
            join value in _db.Set<CustomFieldValue>().AsNoTracking()
                on item.CustomFieldValueId equals value.Id
            join record in _db.Set<CustomFieldRecord>().AsNoTracking()
                on value.RecordId equals record.Id
            where item.BusinessUnitId == businessUnitId && item.IdempotencyKey == idempotencyKey
            select new
            {
                item.AfterJson,
                item.ChangedBy,
                item.RequestHash,
                value.DefinitionId,
                record.EntityType,
                record.EntityId
            }).SingleOrDefaultAsync(ct);
        if (history == null) return null;
        if (history.DefinitionId != definitionId || history.EntityType != entityType || history.EntityId != entityId)
            throw new CustomFieldConflictException(
                "The idempotency key was already used for a different custom-field operation.");
        if (!string.Equals(history.ChangedBy, actor, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(history.RequestHash, requestHash, StringComparison.Ordinal))
            throw new CustomFieldConflictException(
                "The idempotency key was already used by a different actor or request payload.");
        return history.AfterJson == null
            ? null
            : JsonSerializer.Deserialize<CustomFieldValueResponse>(history.AfterJson, JsonOptions)
              ?? throw new CustomFieldDomainException("Stored custom-field history is invalid.");
    }

    private IQueryable<CustomFieldDefinition> DefinitionGraph() => _db.Set<CustomFieldDefinition>()
        .Include(x => x.Versions).ThenInclude(x => x.Options)
        .Include(x => x.Versions).ThenInclude(x => x.Rules)
        .Include(x => x.Versions).ThenInclude(x => x.Dependencies);

    private async Task<CustomFieldRuleState> GetRuleStateAsync(
        long businessUnitId, string entityType, long entityId,
        CustomFieldDefinition target, CancellationToken ct)
    {
        var definitions = await DefinitionGraph().AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.EntityType == entityType &&
            x.Status == CustomFieldDefinitionStatus.Active).ToListAsync(ct);
        var ids = definitions.Select(x => x.Id).ToArray();
        var values = await _db.Set<CustomFieldValue>().AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && ids.Contains(x.DefinitionId) &&
            x.Record.EntityType == entityType && x.Record.EntityId == entityId).ToListAsync(ct);
        var byDefinition = values.ToDictionary(x => x.DefinitionId);
        var byKey = definitions.Where(x => byDefinition.ContainsKey(x.Id)).ToDictionary(
            x => x.StableKey, x => ToInput(byDefinition[x.Id]), StringComparer.OrdinalIgnoreCase);
        return ConditionalRuleEvaluator.Evaluate(ActiveVersion(target), byKey);
    }

    private static void EnforceRuleState(
        string stableKey, CustomFieldValueInput input, CustomFieldRuleState state)
    {
        if (!state.IsVisible)
            throw new CustomFieldConflictException($"Custom field '{stableKey}' is not currently visible.");
        if (state.IsReadOnly)
            throw new CustomFieldConflictException($"Custom field '{stableKey}' is currently read-only.");
        if (state.IsRequired && IsEmpty(input))
            throw new CustomFieldDomainException($"Custom field '{stableKey}' is required in the current context.");
    }

    private static bool IsEmpty(CustomFieldValueInput input) =>
        input.Text == null && !input.Integer.HasValue && !input.Decimal.HasValue &&
        !input.Boolean.HasValue && !input.Date.HasValue && !input.Timestamp.HasValue &&
        input.Json == null && !input.ReferenceId.HasValue;

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
        definition.RetirementReason, definition.Version);

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

    private static void ValidateMutationMetadata(UpsertCustomFieldValueCommand command)
    {
        CustomFieldDefinition.Require(command.IdempotencyKey, nameof(command.IdempotencyKey), 160);
        if (command.IdempotencyKey.StartsWith("legacy:", StringComparison.OrdinalIgnoreCase))
            throw new CustomFieldDomainException("The legacy idempotency-key prefix is reserved.");
        CustomFieldDefinition.Require(command.CorrelationId, nameof(command.CorrelationId), 100);
        if (command.Reason?.Length > 1000)
            throw new CustomFieldDomainException("Reason cannot exceed 1000 characters.");
        if (command.ExpectedVersion is <= 0)
            throw new CustomFieldDomainException("expectedVersion must be positive when supplied.");
    }

    private static string HashRequest(UpsertCustomFieldValueCommand command)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            command.Value,
            command.ExpectedVersion,
            command.Reason
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0) throw new CustomFieldConflictException("Business Unit ID is required.");
    }
}
