namespace ERP_RFQ_Automation.CustomFields;

public sealed class CustomFieldDefinition
{
    private readonly List<CustomFieldVersion> _versions = new();

    private CustomFieldDefinition() { }

    private CustomFieldDefinition(long businessUnitId, string entityType, string stableKey, string createdBy, DateTime createdOn)
    {
        if (businessUnitId <= 0) throw new CustomFieldDomainException("A business unit is required.");
        BusinessUnitId = businessUnitId;
        EntityType = CustomFieldGovernance.ValidateEntityType(entityType);
        StableKey = CustomFieldGovernance.NormalizeAndValidateStableKey(stableKey);
        CreatedBy = Require(createdBy, nameof(createdBy), 200);
        CreatedOn = RequireUtc(createdOn, nameof(createdOn));
        Status = CustomFieldDefinitionStatus.Draft;
        Version = 0;
    }

    public long Id { get; private set; }
    public long BusinessUnitId { get; private set; }
    public string EntityType { get; private set; } = null!;
    public string StableKey { get; private set; } = null!;
    public CustomFieldDefinitionStatus Status { get; private set; }
    public int? ActiveVersionNumber { get; private set; }
    public DateTime CreatedOn { get; private set; }
    public string CreatedBy { get; private set; } = null!;
    public DateTime? RetiredOn { get; private set; }
    public string? RetiredBy { get; private set; }
    public string? RetirementReason { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyCollection<CustomFieldVersion> Versions => _versions.AsReadOnly();

    public static CustomFieldDefinition Create(
        long businessUnitId, string entityType, string stableKey, string createdBy, DateTime createdOn) =>
        new(businessUnitId, entityType, stableKey, createdBy, createdOn);

    public CustomFieldVersion AddVersion(CustomFieldVersionDraft draft, string createdBy, DateTime createdOn)
    {
        EnsureNotRetired();
        var nextVersion = _versions.Count == 0 ? 1 : _versions.Max(x => x.VersionNumber) + 1;
        var version = CustomFieldVersion.Create(this, nextVersion, draft, createdBy, createdOn);
        _versions.Add(version);
        Version++;
        return version;
    }

    public void ActivateVersion(int versionNumber)
    {
        EnsureNotRetired();
        if (_versions.All(x => x.VersionNumber != versionNumber))
            throw new CustomFieldDomainException($"Version {versionNumber} does not belong to this definition.");
        ActiveVersionNumber = versionNumber;
        Status = CustomFieldDefinitionStatus.Active;
        Version++;
    }

    public void Retire(string retiredBy, string reason, DateTime retiredOn)
    {
        if (Status == CustomFieldDefinitionStatus.Retired)
            throw new CustomFieldDomainException("The custom field is already retired.");
        RetiredBy = Require(retiredBy, nameof(retiredBy), 200);
        RetirementReason = Require(reason, nameof(reason), 1000);
        RetiredOn = RequireUtc(retiredOn, nameof(retiredOn));
        Status = CustomFieldDefinitionStatus.Retired;
        Version++;
    }

    private void EnsureNotRetired()
    {
        if (Status == CustomFieldDefinitionStatus.Retired)
            throw new CustomFieldDomainException("Retired custom fields cannot be changed or reactivated.");
    }

    internal static string Require(string? value, string name, int maximumLength)
    {
        var result = (value ?? string.Empty).Trim();
        if (result.Length == 0 || result.Length > maximumLength)
            throw new CustomFieldDomainException($"{name} is required and cannot exceed {maximumLength} characters.");
        return result;
    }

    internal static DateTime RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new CustomFieldDomainException($"{name} must be UTC.");
        return value;
    }
}

public sealed record CustomFieldVersionDraft(
    string Label,
    CustomFieldDataType DataType,
    bool IsRequired = false,
    int? MinimumLength = null,
    int? MaximumLength = null,
    decimal? MinimumValue = null,
    decimal? MaximumValue = null,
    string? HelpText = null,
    string? DefaultValueJson = null,
    bool IsSensitive = false,
    bool IsSearchable = false,
    CustomFieldAccessLevel ViewAccess = CustomFieldAccessLevel.TenantUser,
    CustomFieldAccessLevel EditAccess = CustomFieldAccessLevel.TenantUser);

public sealed class CustomFieldVersion
{
    private readonly List<CustomFieldOption> _options = new();
    private readonly List<CustomFieldRule> _rules = new();
    private readonly List<CustomFieldDependency> _dependencies = new();

    private CustomFieldVersion() { }

    public long Id { get; private set; }
    public long DefinitionId { get; private set; }
    public CustomFieldDefinition Definition { get; private set; } = null!;
    public int VersionNumber { get; private set; }
    public string Label { get; private set; } = null!;
    public string? HelpText { get; private set; }
    public CustomFieldDataType DataType { get; private set; }
    public bool IsRequired { get; private set; }
    public int? MinimumLength { get; private set; }
    public int? MaximumLength { get; private set; }
    public decimal? MinimumValue { get; private set; }
    public decimal? MaximumValue { get; private set; }
    public string? DefaultValueJson { get; private set; }
    public bool IsSensitive { get; private set; }
    public bool IsSearchable { get; private set; }
    public CustomFieldAccessLevel ViewAccess { get; private set; }
    public CustomFieldAccessLevel EditAccess { get; private set; }
    public DateTime CreatedOn { get; private set; }
    public string CreatedBy { get; private set; } = null!;
    public IReadOnlyCollection<CustomFieldOption> Options => _options.AsReadOnly();
    public IReadOnlyCollection<CustomFieldRule> Rules => _rules.AsReadOnly();
    public IReadOnlyCollection<CustomFieldDependency> Dependencies => _dependencies.AsReadOnly();

    internal static CustomFieldVersion Create(
        CustomFieldDefinition definition, int number, CustomFieldVersionDraft draft, string createdBy, DateTime createdOn)
    {
        ValidateDraft(draft);
        return new CustomFieldVersion
        {
            Definition = definition,
            VersionNumber = number,
            Label = CustomFieldDefinition.Require(draft.Label, nameof(draft.Label), 200),
            HelpText = NormalizeOptional(draft.HelpText, 2000),
            DataType = draft.DataType,
            IsRequired = draft.IsRequired,
            MinimumLength = draft.MinimumLength,
            MaximumLength = draft.MaximumLength,
            MinimumValue = draft.MinimumValue,
            MaximumValue = draft.MaximumValue,
            DefaultValueJson = NormalizeOptional(draft.DefaultValueJson, 8000),
            IsSensitive = draft.IsSensitive,
            IsSearchable = draft.IsSearchable,
            ViewAccess = draft.ViewAccess,
            EditAccess = draft.EditAccess,
            CreatedBy = CustomFieldDefinition.Require(createdBy, nameof(createdBy), 200),
            CreatedOn = CustomFieldDefinition.RequireUtc(createdOn, nameof(createdOn))
        };
    }

    public CustomFieldOption AddOption(string stableKey, string label, int displayOrder)
    {
        if (DataType is not (CustomFieldDataType.Option or CustomFieldDataType.MultiOption))
            throw new CustomFieldDomainException("Options can only be added to option fields.");
        var option = CustomFieldOption.Create(this, stableKey, label, displayOrder);
        if (_options.Any(x => x.StableKey.Equals(option.StableKey, StringComparison.OrdinalIgnoreCase)))
            throw new CustomFieldDomainException($"Option '{option.StableKey}' already exists in this version.");
        _options.Add(option);
        return option;
    }

    public CustomFieldRule AddRule(CustomFieldRuleEffect effect, ConditionalRuleNode condition)
    {
        ConditionalRuleValidator.Validate(condition);
        var rule = CustomFieldRule.Create(this, effect, condition);
        _rules.Add(rule);
        return rule;
    }

    public CustomFieldDependency AddDependency(long dependsOnDefinitionId)
    {
        if (dependsOnDefinitionId <= 0)
            throw new CustomFieldDomainException("A persisted dependency definition is required.");
        if (DefinitionId != 0 && dependsOnDefinitionId == DefinitionId)
            throw new CustomFieldDomainException("A field cannot depend on itself.");
        if (_dependencies.Any(x => x.DependsOnDefinitionId == dependsOnDefinitionId))
            throw new CustomFieldDomainException("The dependency already exists.");
        var dependency = CustomFieldDependency.Create(this, dependsOnDefinitionId);
        _dependencies.Add(dependency);
        return dependency;
    }

    private static void ValidateDraft(CustomFieldVersionDraft draft)
    {
        if (draft.IsSensitive && (draft.ViewAccess != CustomFieldAccessLevel.ManagerOrAdmin ||
                                  draft.EditAccess != CustomFieldAccessLevel.ManagerOrAdmin))
            throw new CustomFieldDomainException("Sensitive fields must require manager/admin access for viewing and editing.");
        if (draft.MinimumLength is < 0 || draft.MaximumLength is < 0)
            throw new CustomFieldDomainException("Length limits cannot be negative.");
        if (draft.MinimumLength > draft.MaximumLength)
            throw new CustomFieldDomainException("Minimum length cannot exceed maximum length.");
        if (draft.MinimumValue > draft.MaximumValue)
            throw new CustomFieldDomainException("Minimum value cannot exceed maximum value.");
        if (draft.DataType != CustomFieldDataType.Text &&
            (draft.MinimumLength.HasValue || draft.MaximumLength.HasValue))
            throw new CustomFieldDomainException("Length limits are valid only for text fields.");
        if (draft.DataType is not (CustomFieldDataType.Integer or CustomFieldDataType.Decimal) &&
            (draft.MinimumValue.HasValue || draft.MaximumValue.HasValue))
            throw new CustomFieldDomainException("Numeric ranges are valid only for integer and decimal fields.");
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim();
        if (result.Length > maximumLength)
            throw new CustomFieldDomainException($"Value cannot exceed {maximumLength} characters.");
        return result;
    }
}

public sealed class CustomFieldOption
{
    private CustomFieldOption() { }
    public long Id { get; private set; }
    public long VersionId { get; private set; }
    public CustomFieldVersion Version { get; private set; } = null!;
    public string StableKey { get; private set; } = null!;
    public string Label { get; private set; } = null!;
    public int DisplayOrder { get; private set; }

    internal static CustomFieldOption Create(CustomFieldVersion version, string stableKey, string label, int displayOrder) =>
        new()
        {
            Version = version,
            StableKey = CustomFieldGovernance.NormalizeAndValidateStableKey(stableKey),
            Label = CustomFieldDefinition.Require(label, nameof(label), 200),
            DisplayOrder = displayOrder
        };
}

public sealed class CustomFieldDependency
{
    private CustomFieldDependency() { }
    public long Id { get; private set; }
    public long VersionId { get; private set; }
    public CustomFieldVersion Version { get; private set; } = null!;
    public long DependsOnDefinitionId { get; private set; }

    internal static CustomFieldDependency Create(CustomFieldVersion version, long targetId) =>
        new() { Version = version, DependsOnDefinitionId = targetId };
}
