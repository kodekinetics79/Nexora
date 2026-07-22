namespace ERP_RFQ_Automation.CustomFields;

public enum CustomFieldDataType
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Date,
    Timestamp,
    Option,
    MultiOption,
    Json,
    Reference
}

public enum CustomFieldDefinitionStatus
{
    Draft,
    Active,
    Retired
}

public enum CustomFieldAccessLevel
{
    TenantUser,
    ManagerOrAdmin
}

public enum CustomFieldRuleEffect
{
    Visible,
    Hidden,
    Required,
    ReadOnly
}

public enum CustomFieldComparisonOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    In,
    IsEmpty,
    IsNotEmpty
}

public sealed class CustomFieldDomainException : InvalidOperationException
{
    public CustomFieldDomainException(string message) : base(message) { }
}
