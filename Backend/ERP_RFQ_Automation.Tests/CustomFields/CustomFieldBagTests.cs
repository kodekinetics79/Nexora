using System.Text.Json;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CustomFields;

/// <summary>
/// AA-01 · tenant-defined custom fields stored in a single jsonb value bag on the owning row.
///
/// The point of these tests is the declared TYPE. A custom field that accepts anything is a
/// data-quality liability, not a feature: if a Decimal field can hold "ask Ahmed" then every
/// downstream total, sort and export built on it is a lie.
/// </summary>
public sealed class CustomFieldBagTests
{
    private const long TenantA = 5201;
    private const long TenantB = 5202;

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    // ---- type enforcement -----------------------------------------------------------------

    [Theory]
    // declared type            offending value          why it must be refused
    [InlineData(CustomFieldDataType.Integer, "\"twelve\"")]      // text into a whole number
    [InlineData(CustomFieldDataType.Integer, "12.5")]            // fraction into a whole number
    [InlineData(CustomFieldDataType.Decimal, "\"1,200.00\"")]    // formatted text into a number
    [InlineData(CustomFieldDataType.Boolean, "\"yes\"")]         // text into a flag
    [InlineData(CustomFieldDataType.Boolean, "1")]               // number into a flag
    [InlineData(CustomFieldDataType.Date, "\"next tuesday\"")]   // prose into a date
    [InlineData(CustomFieldDataType.Date, "20260101")]           // number into a date
    [InlineData(CustomFieldDataType.Text, "42")]                 // number into text
    [InlineData(CustomFieldDataType.Timestamp, "\"soon\"")]
    [InlineData(CustomFieldDataType.Reference, "\"CUST-1\"")]    // non-id into a reference
    public void A_value_that_violates_its_declared_type_is_rejected(CustomFieldDataType type, string rawJson)
    {
        var definition = Definition(TenantA, "Customer", "field_under_test", "Field", type);

        var error = Assert.Throws<CustomFieldDomainException>(() =>
            CustomFieldBagValidator.ValidateAndMerge(
                [definition], null,
                new Dictionary<string, JsonElement> { ["field_under_test"] = Json(rawJson) },
                enforceRequired: false));

        Assert.Contains("Field", error.Message);
    }

    [Fact]
    public void A_value_outside_a_declared_numeric_range_is_rejected()
    {
        var definition = Definition(TenantA, "Customer", "target_margin", "Target margin",
            CustomFieldDataType.Decimal, draft: new("Target margin", CustomFieldDataType.Decimal,
                MinimumValue: 0m, MaximumValue: 100m));

        Assert.Throws<CustomFieldDomainException>(() => CustomFieldBagValidator.ValidateAndMerge(
            [definition], null,
            new Dictionary<string, JsonElement> { ["target_margin"] = Json("250") }, false));
    }

    [Fact]
    public void An_option_value_outside_the_declared_option_list_is_rejected()
    {
        var definition = CustomFieldDefinition.Create(TenantA, "Customer", "segment", "admin", DateTime.UtcNow);
        var version = definition.AddVersion(new("Segment", CustomFieldDataType.Option), "admin", DateTime.UtcNow);
        version.AddOption("government", "Government", 1);
        version.AddOption("private", "Private", 2);
        definition.ActivateVersion(1);

        Assert.Throws<CustomFieldDomainException>(() => CustomFieldBagValidator.ValidateAndMerge(
            [definition], null,
            new Dictionary<string, JsonElement> { ["segment"] = Json("\"military\"") }, false));

        var accepted = CustomFieldBagValidator.ValidateAndMerge(
            [definition], null,
            new Dictionary<string, JsonElement> { ["segment"] = Json("\"government\"") }, false);
        Assert.Contains("government", accepted);
    }

    [Fact]
    public void A_key_that_is_not_an_active_custom_field_is_rejected_rather_than_stored()
    {
        var definition = Definition(TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);

        Assert.Throws<CustomFieldDomainException>(() => CustomFieldBagValidator.ValidateAndMerge(
            [definition], null,
            new Dictionary<string, JsonElement> { ["not_defined_anywhere"] = Json("\"x\"") }, false));
    }

    [Fact]
    public void A_retired_definition_stops_accepting_new_values()
    {
        var definition = Definition(TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        definition.Retire("admin", "No longer collected", DateTime.UtcNow);

        Assert.Throws<CustomFieldDomainException>(() => CustomFieldBagValidator.ValidateAndMerge(
            [definition], null,
            new Dictionary<string, JsonElement> { ["vendor_code"] = Json("\"TC-1\"") }, false));
    }

    [Fact]
    public void A_conforming_value_is_stored_in_a_canonical_form()
    {
        var text = Definition(TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var date = Definition(TenantA, "Customer", "framework_expiry", "Framework expiry", CustomFieldDataType.Date);
        var flag = Definition(TenantA, "Customer", "is_strategic", "Strategic account", CustomFieldDataType.Boolean);

        var json = CustomFieldBagValidator.ValidateAndMerge(
            [text, date, flag], null,
            new Dictionary<string, JsonElement>
            {
                ["vendor_code"] = Json("\"TC-9910\""),
                // Deliberately a non-canonical date string: it must come back normalised.
                ["framework_expiry"] = Json("\"2027-3-31\""),
                ["is_strategic"] = Json("true")
            }, false);

        var bag = CustomFieldBag.Read(json);
        Assert.Equal("TC-9910", bag["vendor_code"].GetString());
        Assert.Equal("2027-03-31", bag["framework_expiry"].GetString());
        Assert.True(bag["is_strategic"].GetBoolean());
    }

    [Fact]
    public void Clearing_a_value_removes_the_key_rather_than_storing_a_null()
    {
        var definition = Definition(TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var withValue = CustomFieldBagValidator.ValidateAndMerge(
            [definition], null,
            new Dictionary<string, JsonElement> { ["vendor_code"] = Json("\"TC-1\"") }, false);

        var cleared = CustomFieldBagValidator.ValidateAndMerge(
            [definition], withValue,
            new Dictionary<string, JsonElement> { ["vendor_code"] = Json("null") }, false);

        // An empty bag is null, not "{}": "unset" has exactly one representation.
        Assert.Null(cleared);
    }

    [Fact]
    public void A_required_field_with_no_value_is_refused_when_requiredness_is_enforced()
    {
        var definition = Definition(TenantA, "Customer", "vendor_code", "Vendor code",
            CustomFieldDataType.Text, draft: new("Vendor code", CustomFieldDataType.Text, IsRequired: true));

        Assert.Throws<CustomFieldDomainException>(() => CustomFieldBagValidator.ValidateAndMerge(
            [definition], null, new Dictionary<string, JsonElement>(), enforceRequired: true));

        // …but a partial patch of a pre-existing record is not blocked by it.
        var patched = CustomFieldBagValidator.ValidateAndMerge(
            [definition], null, new Dictionary<string, JsonElement>(), enforceRequired: false);
        Assert.Null(patched);
    }

    // ---- tolerant reads -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]      // valid JSON, wrong shape
    [InlineData("\"a string\"")] // valid JSON, wrong shape
    [InlineData("{\"unclosed\": ")]
    public void A_malformed_bag_reads_as_empty_and_never_throws(string? stored)
    {
        var bag = CustomFieldBag.Read(stored);
        Assert.Empty(bag);
    }

    // ---- persistence + tenant isolation ---------------------------------------------------

    [Fact]
    public async Task Values_persist_on_the_owning_row_and_survive_a_reload()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var service = new CustomFieldBagService(context);

        await service.UpdateAsync(TenantA, "Customer", 8001, new(
            new Dictionary<string, JsonElement> { ["vendor_code"] = Json("\"TC-9910\"") }), true, CancellationToken.None);
        context.ChangeTracker.Clear();

        var stored = await context.Customers.AsNoTracking()
            .Where(x => x.Id == 8001).Select(x => x.CustomFieldsJson).SingleAsync();
        Assert.Contains("TC-9910", stored);

        var reread = await service.GetAsync(TenantA, "Customer", 8001, true, CancellationToken.None);
        var field = Assert.Single(reread.Fields);
        Assert.Equal("vendor_code", field.StableKey);
        Assert.Equal("TC-9910", field.DisplayValue);
    }

    [Fact]
    public async Task A_type_violation_is_refused_and_nothing_is_written()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "credit_days", "Credit days", CustomFieldDataType.Integer);
        var service = new CustomFieldBagService(context);

        await Assert.ThrowsAsync<CustomFieldDomainException>(() => service.UpdateAsync(
            TenantA, "Customer", 8001,
            new(new Dictionary<string, JsonElement> { ["credit_days"] = Json("\"thirty\"") }),
            true, CancellationToken.None));

        context.ChangeTracker.Clear();
        var stored = await context.Customers.AsNoTracking()
            .Where(x => x.Id == 8001).Select(x => x.CustomFieldsJson).SingleAsync();
        Assert.Null(stored);
    }

    [Fact]
    public async Task A_record_belonging_to_another_tenant_is_not_readable_or_writable()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantB);
        var service = new CustomFieldBagService(context);

        // Customer 8001 belongs to TenantA. A TenantB caller must get a not-found, never an
        // empty bag — an empty read would confirm the row exists.
        await Assert.ThrowsAsync<CustomFieldNotFoundException>(() =>
            service.GetAsync(TenantB, "Customer", 8001, true, CancellationToken.None));
        await Assert.ThrowsAsync<CustomFieldNotFoundException>(() => service.UpdateAsync(
            TenantB, "Customer", 8001,
            new(new Dictionary<string, JsonElement>()), true, CancellationToken.None));
    }

    [Fact]
    public async Task The_lead_line_and_supplier_attach_points_carry_a_bag_too()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "LeadItem", "customer_line_ref", "Customer line ref",
            CustomFieldDataType.Text);
        await DefineAsync(context, TenantA, "Supplier", "approval_status", "Approval status",
            CustomFieldDataType.Text);
        var service = new CustomFieldBagService(context);

        await service.UpdateAsync(TenantA, "LeadItem", 8301, new(
            new Dictionary<string, JsonElement> { ["customer_line_ref"] = Json("\"L-0007\"") }),
            true, CancellationToken.None);
        await service.UpdateAsync(TenantA, "Supplier", 8201, new(
            new Dictionary<string, JsonElement> { ["approval_status"] = Json("\"Approved\"") }),
            true, CancellationToken.None);
        context.ChangeTracker.Clear();

        Assert.Contains("L-0007", await context.LeadItems.AsNoTracking()
            .Where(x => x.Id == 8301).Select(x => x.CustomFieldsJson).SingleAsync());
        Assert.Contains("Approved", await context.Suppliers.AsNoTracking()
            .Where(x => x.Id == 8201).Select(x => x.CustomFieldsJson).SingleAsync());
    }

    [Fact]
    public async Task An_entity_with_no_attach_point_is_refused()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new CustomFieldBagService(context);

        await Assert.ThrowsAsync<CustomFieldDomainException>(() =>
            service.GetAsync(TenantA, "Shipment", 1, true, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------------------

    private static CustomFieldDefinition Definition(
        long businessUnitId, string entityType, string stableKey, string label,
        CustomFieldDataType type, CustomFieldVersionDraft? draft = null)
    {
        var definition = CustomFieldDefinition.Create(businessUnitId, entityType, stableKey, "admin", DateTime.UtcNow);
        definition.AddVersion(draft ?? new(label, type), "admin", DateTime.UtcNow);
        definition.ActivateVersion(1);
        return definition;
    }

    private static async Task DefineAsync(
        ErpRfqAutomationContext context, long businessUnitId, string entityType,
        string stableKey, string label, CustomFieldDataType type)
    {
        context.Add(Definition(businessUnitId, entityType, stableKey, label, type));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task<TestDb> SeedAsync()
    {
        var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, TenantA);
        Seed.EnsureBusinessUnit(context, TenantB);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        context.Customers.Add(new Customer
        {
            Id = 8001, Name = "Tech Connect", ImageUrl = "n/a", Buid = TenantA,
            IsActive = true, CreatedBy = "seed", CreatedOn = now
        });
        context.Suppliers.Add(new Supplier
        {
            Id = 8201, Name = "Gulf Cables", ImageUrl = "n/a", Buid = TenantA,
            IsActive = true, CreatedBy = "seed", CreatedOn = now
        });
        Seed.Lead(context, 8300, TenantA, items: [new LeadItem
        {
            Id = 8301, ProductShortName = "Cable 3C x 95mm", Quantity = 10
        }]);
        await context.SaveChangesAsync();
        return db;
    }
}
