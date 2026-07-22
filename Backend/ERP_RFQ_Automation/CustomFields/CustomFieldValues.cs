namespace ERP_RFQ_Automation.CustomFields;

public sealed class CustomFieldRecord
{
    private readonly List<CustomFieldValue> _values = new();
    private CustomFieldRecord() { }

    private CustomFieldRecord(long businessUnitId, string entityType, long entityId, DateTime createdOn)
    {
        if (businessUnitId <= 0 || entityId <= 0)
            throw new CustomFieldDomainException("A business unit and persisted entity are required.");
        BusinessUnitId = businessUnitId;
        EntityType = CustomFieldGovernance.ValidateEntityType(entityType);
        EntityId = entityId;
        CreatedOn = CustomFieldDefinition.RequireUtc(createdOn, nameof(createdOn));
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public string EntityType { get; private set; } = null!;
    public long EntityId { get; private set; }
    public DateTime CreatedOn { get; private set; }
    public IReadOnlyCollection<CustomFieldValue> Values => _values.AsReadOnly();

    public static CustomFieldRecord Create(long businessUnitId, string entityType, long entityId, DateTime createdOn) =>
        new(businessUnitId, entityType, entityId, createdOn);
}

public sealed record CustomFieldValueInput(
    string? Text = null,
    long? Integer = null,
    decimal? Decimal = null,
    bool? Boolean = null,
    DateOnly? Date = null,
    DateTime? Timestamp = null,
    string? Json = null,
    string? ReferenceType = null,
    long? ReferenceId = null);

public sealed class CustomFieldValue
{
    private CustomFieldValue() { }
    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long RecordId { get; private set; }
    public CustomFieldRecord Record { get; private set; } = null!;
    public long DefinitionId { get; private set; }
    public int DefinitionVersion { get; private set; }
    public string? TextValue { get; private set; }
    public long? IntegerValue { get; private set; }
    public decimal? DecimalValue { get; private set; }
    public bool? BooleanValue { get; private set; }
    public DateOnly? DateValue { get; private set; }
    public DateTime? TimestampValue { get; private set; }
    public string? JsonValue { get; private set; }
    public string? ReferenceType { get; private set; }
    public long? ReferenceId { get; private set; }
    public DateTime UpdatedOn { get; private set; }
    public string UpdatedBy { get; private set; } = null!;

    public static CustomFieldValue Create(
        long businessUnitId,
        long recordId,
        long definitionId,
        CustomFieldVersion version,
        CustomFieldValueInput input,
        string updatedBy,
        DateTime updatedOn)
    {
        if (businessUnitId <= 0 || recordId <= 0 || definitionId <= 0)
            throw new CustomFieldDomainException("Persisted tenant, record, and definition identifiers are required.");
        CustomFieldValueValidator.Validate(version, input);
        return new CustomFieldValue
        {
            BusinessUnitId = businessUnitId,
            RecordId = recordId,
            DefinitionId = definitionId,
            DefinitionVersion = version.VersionNumber,
            TextValue = input.Text,
            IntegerValue = input.Integer,
            DecimalValue = input.Decimal,
            BooleanValue = input.Boolean,
            DateValue = input.Date,
            TimestampValue = input.Timestamp,
            JsonValue = input.Json,
            ReferenceType = input.ReferenceType,
            ReferenceId = input.ReferenceId,
            UpdatedBy = CustomFieldDefinition.Require(updatedBy, nameof(updatedBy), 200),
            UpdatedOn = CustomFieldDefinition.RequireUtc(updatedOn, nameof(updatedOn))
        };
    }
}

public sealed class CustomFieldValueHistory
{
    private CustomFieldValueHistory() { }
    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public long CustomFieldValueId { get; private set; }
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
    public string ChangeType { get; private set; } = null!;
    public string ChangedBy { get; private set; } = null!;
    public DateTime ChangedOn { get; private set; }
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = null!;

    public static CustomFieldValueHistory Create(
        long businessUnitId, long valueId, string changeType, string? beforeJson, string? afterJson,
        string changedBy, DateTime changedOn, string correlationId, string? reason = null) =>
        new()
        {
            BusinessUnitId = businessUnitId > 0 ? businessUnitId : throw new CustomFieldDomainException("A business unit is required."),
            CustomFieldValueId = valueId > 0 ? valueId : throw new CustomFieldDomainException("A persisted value is required."),
            ChangeType = CustomFieldDefinition.Require(changeType, nameof(changeType), 30),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            ChangedBy = CustomFieldDefinition.Require(changedBy, nameof(changedBy), 200),
            ChangedOn = CustomFieldDefinition.RequireUtc(changedOn, nameof(changedOn)),
            CorrelationId = CustomFieldDefinition.Require(correlationId, nameof(correlationId), 100),
            Reason = reason
        };
}
