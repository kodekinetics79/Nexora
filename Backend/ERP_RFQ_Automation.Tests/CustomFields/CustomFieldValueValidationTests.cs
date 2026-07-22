using ERP_RFQ_Automation.CustomFields;

namespace ERP_RFQ_Automation.Tests.CustomFields;

public sealed class CustomFieldValueValidationTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RequiredText_EnforcesPresenceAndLength()
    {
        var version = Version(new("Project code", CustomFieldDataType.Text, true, 3, 8));

        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput()));
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(Text: "AB")));
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(Text: "123456789")));

        CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(Text: "ABC-123"));
    }

    [Fact]
    public void Decimal_EnforcesTypeAndRange()
    {
        var version = Version(new("Authorized margin", CustomFieldDataType.Decimal,
            MinimumValue: 5, MaximumValue: 40));

        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(Integer: 20)));
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(Decimal: 41)));

        CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(Decimal: 25.5m));
    }

    [Fact]
    public void Value_RejectsMultipleTypedColumns()
    {
        var version = Version(new("Notes", CustomFieldDataType.Text));
        Assert.Throws<CustomFieldDomainException>(() => CustomFieldValueValidator.Validate(
            version, new CustomFieldValueInput(Text: "ok", Boolean: true)));
    }

    [Fact]
    public void OptionAndMultiOption_RequireDefinedStableKeys()
    {
        var definition = CustomFieldDefinition.Create(7, "Rfq", "delivery_mode", "admin", Now);
        var single = definition.AddVersion(new("Delivery", CustomFieldDataType.Option), "admin", Now);
        single.AddOption("air_freight", "Air freight", 1);
        single.AddOption("sea_freight", "Sea freight", 2);

        CustomFieldValueValidator.Validate(single, new CustomFieldValueInput(Text: "air_freight"));
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(single, new CustomFieldValueInput(Text: "rail")));

        var multi = definition.AddVersion(new("Delivery", CustomFieldDataType.MultiOption), "admin", Now.AddMinutes(1));
        multi.AddOption("air_freight", "Air freight", 1);
        multi.AddOption("sea_freight", "Sea freight", 2);
        CustomFieldValueValidator.Validate(multi,
            new CustomFieldValueInput(Json: "[\"air_freight\",\"sea_freight\"]"));
        Assert.Throws<CustomFieldDomainException>(() => CustomFieldValueValidator.Validate(multi,
            new CustomFieldValueInput(Json: "[\"air_freight\",\"air_freight\"]")));
    }

    [Fact]
    public void Reference_RequiresTypeAndIdTogether()
    {
        var version = Version(new("Related customer", CustomFieldDataType.Reference));
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(ReferenceType: "Customer")));
        Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldValueValidator.Validate(version, new CustomFieldValueInput(ReferenceId: 42)));

        CustomFieldValueValidator.Validate(version,
            new CustomFieldValueInput(ReferenceType: "Customer", ReferenceId: 42));
    }

    private static CustomFieldVersion Version(CustomFieldVersionDraft draft)
    {
        var definition = CustomFieldDefinition.Create(7, "Rfq", "test_field", "admin", Now);
        return definition.AddVersion(draft, "admin", Now);
    }
}
