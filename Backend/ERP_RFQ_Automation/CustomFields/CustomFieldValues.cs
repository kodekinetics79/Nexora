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
    public long Version { get; private set; }

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
            UpdatedOn = CustomFieldDefinition.RequireUtc(updatedOn, nameof(updatedOn)),
            Version = 1
        };
    }

    public void Update(
        CustomFieldVersion version,
        CustomFieldValueInput input,
        string updatedBy,
        DateTime updatedOn,
        long expectedVersion)
    {
        if (Version != expectedVersion)
            throw new CustomFieldDomainException("Custom-field value changed since it was loaded. Refresh and retry.");
        CustomFieldValueValidator.Validate(version, input);
        DefinitionVersion = version.VersionNumber;
        TextValue = input.Text;
        IntegerValue = input.Integer;
        DecimalValue = input.Decimal;
        BooleanValue = input.Boolean;
        DateValue = input.Date;
        TimestampValue = input.Timestamp;
        JsonValue = input.Json;
        ReferenceType = input.ReferenceType;
        ReferenceId = input.ReferenceId;
        UpdatedBy = CustomFieldDefinition.Require(updatedBy, nameof(updatedBy), 200);
        UpdatedOn = CustomFieldDefinition.RequireUtc(updatedOn, nameof(updatedOn));
        Version++;
    }
}

// ============================================================================================
// CustomFieldValueHistory was REMOVED here as part of closing FR-MDM-05 / register item E44.
//
// It was a fully built control that could never hold a row. The class was declared, mapped to
// custom_field_value_history, tenant-filtered, indexed, given a unique idempotency key and
// protected from UPDATE and DELETE by both the governance interceptor and a database trigger —
// and its Create method had ZERO call sites. Nothing in the product ever wrote to it.
//
// That is worse than an absent table, and worse in a specific way. An auditor testing "were
// custom-field values ever changed without a record?" queries the history, finds no exceptions,
// and concludes the control works. It reports clean because it is EMPTY. An absent table at
// least fails the test honestly.
//
// Wiring it was not an option. The relational value model it hangs off is not the live storage:
// production writes tenant-defined values into a jsonb bag on the entity itself
// (Customer.CustomFieldsJson, Supplier.CustomFieldsJson, LeadItem.CustomFieldsJson — see
// CustomFieldBagService), and CustomFieldValue.Create is likewise called from nowhere but a
// test. A history keyed by CustomFieldValueId cannot record a change to a value that has no
// CustomFieldValue row. Wiring it would have meant migrating the storage model, which is not
// this change and is not authorised by the BRD.
//
// The control it pretended to provide is now actually delivered, by a different mechanism:
// CustomFieldsJson is an ordinary audited property of Customer and Supplier, so every change to
// a tenant-defined custom field on those two entities is captured with its before and after
// value by MasterDataAuditInterceptor. LeadItem.CustomFieldsJson is NOT covered — a lead line is
// not master data and sits outside FR-MDM-05.
//
// The table, its trigger and its indexes still exist in the database. Dropping them is a
// migration, and migration authorship is reserved to the integration owner; the exact delta is
// stated in the change report accompanying this work.
// ============================================================================================
