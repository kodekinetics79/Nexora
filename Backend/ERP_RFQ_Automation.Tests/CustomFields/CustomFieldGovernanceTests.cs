using ERP_RFQ_Automation.CustomFields;

namespace ERP_RFQ_Automation.Tests.CustomFields;

public sealed class CustomFieldGovernanceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(" Customer_PO ", "customer_po")]
    [InlineData("delivery_zone_2", "delivery_zone_2")]
    public void StableKey_IsNormalized(string supplied, string expected)
    {
        var definition = CustomFieldDefinition.Create(7, "Rfq", supplied, "admin", Now);
        Assert.Equal(expected, definition.StableKey);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("BusinessUnitId")]
    [InlineData("master_reference")]
    [InlineData("bad-key")]
    [InlineData("x")]
    public void StableKey_RejectsReservedOrInvalidNames(string supplied)
    {
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldDefinition.Create(7, "Rfq", supplied, "admin", Now));
    }

    [Fact]
    public void Versions_AreSequentialAndActivationIsExplicit()
    {
        var definition = CreateDefinition();
        var first = definition.AddVersion(new("Site", CustomFieldDataType.Text), "admin", Now);
        var second = definition.AddVersion(new("Delivery Site", CustomFieldDataType.Text), "admin", Now.AddMinutes(1));

        definition.ActivateVersion(2);

        Assert.Equal(1, first.VersionNumber);
        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(2, definition.ActiveVersionNumber);
        Assert.Equal(CustomFieldDefinitionStatus.Active, definition.Status);
        Assert.True(typeof(CustomFieldVersion).GetProperty(nameof(CustomFieldVersion.Label))!.SetMethod!.IsPrivate);
    }

    [Fact]
    public void Retirement_PreservesVersionsAndPreventsFurtherMutation()
    {
        var definition = CreateDefinition();
        definition.AddVersion(new("Site", CustomFieldDataType.Text), "admin", Now);

        definition.Retire("admin", "No longer collected", Now.AddDays(1));

        Assert.Equal(CustomFieldDefinitionStatus.Retired, definition.Status);
        Assert.Single(definition.Versions);
        Assert.Throws<CustomFieldDomainException>(() =>
            definition.AddVersion(new("New", CustomFieldDataType.Text), "admin", Now.AddDays(2)));
        Assert.Throws<CustomFieldDomainException>(() => definition.ActivateVersion(1));
    }

    [Theory]
    [InlineData(CustomFieldDataType.Boolean)]
    [InlineData(CustomFieldDataType.Date)]
    [InlineData(CustomFieldDataType.Option)]
    public void Version_RejectsNumericRulesForNonNumericTypes(CustomFieldDataType type)
    {
        var definition = CreateDefinition();
        Assert.Throws<CustomFieldDomainException>(() => definition.AddVersion(
            new("Invalid", type, MinimumValue: 1), "admin", Now));
    }

    private static CustomFieldDefinition CreateDefinition() =>
        CustomFieldDefinition.Create(7, "Rfq", "delivery_site", "admin", Now);
}
