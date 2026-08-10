namespace ERP_RFQ_Automation.CustomFields;

public sealed record CustomFieldOptionDraft(string StableKey, string Label, int DisplayOrder);
public sealed record CustomFieldRuleDraft(CustomFieldRuleEffect Effect, ConditionalRuleNode Condition);

public sealed record CreateCustomFieldDefinitionCommand(
    string EntityType,
    string StableKey,
    CustomFieldVersionDraft Version,
    IReadOnlyList<CustomFieldOptionDraft>? Options = null,
    IReadOnlyList<CustomFieldRuleDraft>? Rules = null,
    IReadOnlyList<long>? DependencyDefinitionIds = null,
    bool Activate = false,
    int DisplayOrder = 0);

public sealed record AddCustomFieldVersionCommand(
    CustomFieldVersionDraft Version,
    IReadOnlyList<CustomFieldOptionDraft>? Options = null,
    IReadOnlyList<CustomFieldRuleDraft>? Rules = null,
    IReadOnlyList<long>? DependencyDefinitionIds = null,
    bool Activate = false);

public sealed record RetireCustomFieldDefinitionCommand(string Reason);

/// <summary>One field's new position. Sent as a batch so a whole reorder is a single call.</summary>
public sealed record CustomFieldOrderEntry(long DefinitionId, int DisplayOrder);

public sealed record ReorderCustomFieldsCommand(
    string EntityType, IReadOnlyList<CustomFieldOrderEntry> Order);

public sealed record UpsertCustomFieldValueCommand(
    CustomFieldValueInput Value,
    long? ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    string? Reason = null);

public sealed record CustomFieldOptionResponse(string StableKey, string Label, int DisplayOrder);
public sealed record CustomFieldRuleResponse(CustomFieldRuleEffect Effect, ConditionalRuleNode Condition);

public sealed record CustomFieldVersionResponse(
    int VersionNumber,
    string Label,
    string? HelpText,
    CustomFieldDataType DataType,
    bool IsRequired,
    int? MinimumLength,
    int? MaximumLength,
    decimal? MinimumValue,
    decimal? MaximumValue,
    string? DefaultValueJson,
    bool IsSensitive,
    bool IsSearchable,
    CustomFieldAccessLevel ViewAccess,
    CustomFieldAccessLevel EditAccess,
    IReadOnlyList<CustomFieldOptionResponse> Options,
    IReadOnlyList<CustomFieldRuleResponse> Rules,
    IReadOnlyList<long> DependencyDefinitionIds,
    DateTime CreatedOn,
    string CreatedBy);

public sealed record CustomFieldDefinitionResponse(
    long Id,
    string EntityType,
    string StableKey,
    CustomFieldDefinitionStatus Status,
    int? ActiveVersionNumber,
    IReadOnlyList<CustomFieldVersionResponse> Versions,
    DateTime CreatedOn,
    string CreatedBy,
    DateTime? RetiredOn,
    string? RetiredBy,
    string? RetirementReason,
    long Version,
    int DisplayOrder = 0);

public sealed record CustomFieldValueResponse(
    long Id,
    long DefinitionId,
    string StableKey,
    int DefinitionVersion,
    CustomFieldValueInput Value,
    long Version,
    DateTime UpdatedOn,
    string UpdatedBy);

public sealed record CustomFieldSchemaItemResponse(
    long DefinitionId,
    string StableKey,
    CustomFieldVersionResponse Version,
    CustomFieldValueResponse? Value,
    bool IsRequired,
    bool IsReadOnly);

public sealed record CustomFieldEntitySchemaResponse(
    string EntityType,
    long EntityId,
    IReadOnlyList<CustomFieldSchemaItemResponse> Fields);

public sealed class CustomFieldNotFoundException(string message) : Exception(message);
public sealed class CustomFieldConflictException(string message) : Exception(message);

/// <summary>
/// Thrown by the retired EAV value-write path. Surfaced as 410 Gone so a caller can tell
/// "this endpoint is deliberately gone, here is the replacement" apart from "this endpoint
/// is missing because the deployment is broken".
/// </summary>
public sealed class CustomFieldWritePathRetiredException(string message) : Exception(message);
