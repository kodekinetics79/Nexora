using System.Text.Json;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CustomFields;

/// <summary>
/// AA-01 · the governance a tenant manager meets through the admin screen.
///
/// Three promises the interface makes, proved here so they are not merely copy:
///   1. the stable key is permanent,
///   2. retiring keeps the data,
///   3. a data-type change on a field that already holds values is refused, not coerced.
/// </summary>
public sealed class CustomFieldAdministrationTests
{
    private const long TenantA = 6301;
    private const long TenantB = 6302;

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    // ---- 1. the key is permanent ----------------------------------------------------------

    [Fact]
    public async Task The_stable_key_cannot_be_changed_once_created()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);

        var definition = await context.Set<CustomFieldDefinition>().SingleAsync();
        // Reach past the service and mutate the entity as a rogue code path would. The save
        // interceptor is the backstop, so this is refused no matter who asks.
        context.Entry(definition).Property(x => x.StableKey).CurrentValue = "renamed_key";

        var error = await Assert.ThrowsAsync<CustomFieldDomainException>(() => context.SaveChangesAsync());
        Assert.Contains("cannot be changed once created", error.Message);
    }

    [Fact]
    public async Task The_entity_a_field_attaches_to_cannot_be_changed_once_created()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);

        var definition = await context.Set<CustomFieldDefinition>().SingleAsync();
        context.Entry(definition).Property(x => x.EntityType).CurrentValue = "Supplier";

        await Assert.ThrowsAsync<CustomFieldDomainException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Editing_a_field_publishes_a_new_version_and_never_touches_the_key()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new CustomFieldApplicationService(context);
        var created = await service.CreateDefinitionAsync(TenantA, new CreateCustomFieldDefinitionCommand(
            "Customer", "vendor_code", new("Vendor code", CustomFieldDataType.Text), Activate: true),
            "admin", CancellationToken.None);

        var updated = await service.AddVersionAsync(TenantA, created.Id, new AddCustomFieldVersionCommand(
            new("Our vendor code at this buyer", CustomFieldDataType.Text, IsRequired: true), Activate: true),
            "admin", CancellationToken.None);

        Assert.Equal("vendor_code", updated.StableKey);
        Assert.Equal(2, updated.ActiveVersionNumber);
        Assert.Equal(2, updated.Versions.Count);
        // The label a value was captured under is still recoverable from version 1.
        Assert.Equal("Vendor code", updated.Versions.Single(v => v.VersionNumber == 1).Label);
    }

    // ---- 2. retiring is not deleting -------------------------------------------------------

    [Fact]
    public async Task Retiring_a_field_keeps_every_value_already_captured()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var bag = new CustomFieldBagService(context);
        await bag.UpdateAsync(TenantA, "Customer", 9001, new(
            new Dictionary<string, JsonElement> { ["vendor_code"] = Json("\"TC-9910\"") }),
            true, CancellationToken.None);

        var definitionId = await context.Set<CustomFieldDefinition>().Select(x => x.Id).SingleAsync();
        await new CustomFieldApplicationService(context).RetireDefinitionAsync(
            TenantA, definitionId, new RetireCustomFieldDefinitionCommand("Superseded"),
            "admin", CancellationToken.None);
        context.ChangeTracker.Clear();

        // The value is untouched on the row…
        var stored = await context.Customers.AsNoTracking()
            .Where(x => x.Id == 9001).Select(x => x.CustomFieldsJson).SingleAsync();
        Assert.Contains("TC-9910", stored);

        // …and the field simply stops being offered.
        var offered = await bag.GetAsync(TenantA, "Customer", 9001, true, CancellationToken.None);
        Assert.Empty(offered.Fields);
    }

    [Fact]
    public async Task A_retired_field_can_be_reactivated_and_keeps_the_record_of_its_retirement()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var service = new CustomFieldApplicationService(context);
        var definitionId = await context.Set<CustomFieldDefinition>().Select(x => x.Id).SingleAsync();

        await service.RetireDefinitionAsync(TenantA, definitionId,
            new RetireCustomFieldDefinitionCommand("Paused for review"), "admin", CancellationToken.None);
        var reactivated = await service.ReactivateDefinitionAsync(TenantA, definitionId, CancellationToken.None);

        Assert.Equal(CustomFieldDefinitionStatus.Active, reactivated.Status);
        Assert.Equal(1, reactivated.ActiveVersionNumber);
        // The retirement stamp survives: it is the record of what happened, not a flag to clear.
        Assert.Equal("Paused for review", reactivated.RetirementReason);
        Assert.NotNull(reactivated.RetiredOn);

        var offered = await new CustomFieldBagService(context)
            .GetAsync(TenantA, "Customer", 9001, true, CancellationToken.None);
        Assert.Single(offered.Fields);
    }

    [Fact]
    public async Task An_active_field_cannot_be_reactivated()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var definitionId = await context.Set<CustomFieldDefinition>().Select(x => x.Id).SingleAsync();

        await Assert.ThrowsAsync<CustomFieldDomainException>(() =>
            new CustomFieldApplicationService(context)
                .ReactivateDefinitionAsync(TenantA, definitionId, CancellationToken.None));
    }

    // ---- 3. a dangerous type change is refused, not coerced --------------------------------

    [Fact]
    public async Task Changing_the_data_type_is_refused_once_a_record_holds_a_value()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        await new CustomFieldBagService(context).UpdateAsync(TenantA, "Customer", 9001, new(
            new Dictionary<string, JsonElement> { ["vendor_code"] = Json("\"TC-9910\"") }),
            true, CancellationToken.None);
        context.ChangeTracker.Clear();

        var service = new CustomFieldApplicationService(context);
        var definitionId = await context.Set<CustomFieldDefinition>().Select(x => x.Id).SingleAsync();

        var error = await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.AddVersionAsync(
            TenantA, definitionId,
            new AddCustomFieldVersionCommand(new("Vendor code", CustomFieldDataType.Decimal), Activate: true),
            "admin", CancellationToken.None));

        // The message has to tell an administrator what to do instead, not merely refuse.
        Assert.Contains("Retire this field", error.Message);
        context.ChangeTracker.Clear();

        // Nothing changed: still one version, still Text, and the value is intact.
        var stored = Assert.Single(await service.ListDefinitionsAsync(TenantA, "Customer", CancellationToken.None));
        Assert.Single(stored.Versions);
        Assert.Equal(CustomFieldDataType.Text, stored.Versions[0].DataType);
        Assert.Contains("TC-9910", await context.Customers.AsNoTracking()
            .Where(x => x.Id == 9001).Select(x => x.CustomFieldsJson).SingleAsync());
    }

    [Fact]
    public async Task Changing_the_data_type_is_allowed_while_no_record_holds_a_value()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "credit_days", "Credit days", CustomFieldDataType.Text);
        var service = new CustomFieldApplicationService(context);
        var definitionId = await context.Set<CustomFieldDefinition>().Select(x => x.Id).SingleAsync();

        var updated = await service.AddVersionAsync(TenantA, definitionId,
            new AddCustomFieldVersionCommand(new("Credit days", CustomFieldDataType.Integer), Activate: true),
            "admin", CancellationToken.None);

        Assert.Equal(CustomFieldDataType.Integer,
            updated.Versions.Single(v => v.VersionNumber == updated.ActiveVersionNumber).DataType);
    }

    [Fact]
    public async Task A_value_held_by_ANOTHER_tenant_does_not_block_this_tenants_type_change()
    {
        using var db = await SeedAsync();
        await using (var contextB = db.ContextFor(TenantB))
        {
            await DefineAsync(contextB, TenantB, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
            await new CustomFieldBagService(contextB).UpdateAsync(TenantB, "Customer", 9002, new(
                new Dictionary<string, JsonElement> { ["vendor_code"] = Json("\"OTHER\"") }),
                true, CancellationToken.None);
        }

        await using var contextA = db.ContextFor(TenantA);
        await DefineAsync(contextA, TenantA, "Customer", "vendor_code", "Vendor code", CustomFieldDataType.Text);
        var definitionId = await contextA.Set<CustomFieldDefinition>()
            .Where(x => x.BusinessUnitId == TenantA).Select(x => x.Id).SingleAsync();

        var updated = await new CustomFieldApplicationService(contextA).AddVersionAsync(
            TenantA, definitionId,
            new AddCustomFieldVersionCommand(new("Vendor code", CustomFieldDataType.Integer), Activate: true),
            "admin", CancellationToken.None);

        Assert.Equal(2, updated.ActiveVersionNumber);
    }

    // ---- reordering -----------------------------------------------------------------------

    [Fact]
    public async Task Reordering_is_a_single_batch_and_does_not_create_versions()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineAsync(context, TenantA, "Customer", "alpha_field", "Alpha", CustomFieldDataType.Text);
        await DefineAsync(context, TenantA, "Customer", "bravo_field", "Bravo", CustomFieldDataType.Text);
        var service = new CustomFieldApplicationService(context);

        var before = await service.ListDefinitionsAsync(TenantA, "Customer", CancellationToken.None);
        var alpha = before.Single(x => x.StableKey == "alpha_field");
        var bravo = before.Single(x => x.StableKey == "bravo_field");

        var reordered = await service.ReorderDefinitionsAsync(TenantA, new ReorderCustomFieldsCommand(
            "Customer", [new(bravo.Id, 0), new(alpha.Id, 1)]), CancellationToken.None);

        Assert.Equal("bravo_field", reordered[0].StableKey);
        Assert.Equal("alpha_field", reordered[1].StableKey);
        // The 20-version budget exists to protect LABEL and TYPE history. Reordering must not
        // spend it, or ten drags would exhaust a field.
        Assert.All(reordered, definition => Assert.Single(definition.Versions));
    }

    [Fact]
    public async Task A_reorder_cannot_touch_another_tenants_definitions()
    {
        using var db = await SeedAsync();
        long foreignId;
        await using (var contextB = db.ContextFor(TenantB))
        {
            await DefineAsync(contextB, TenantB, "Customer", "foreign_field", "Foreign", CustomFieldDataType.Text);
            foreignId = await contextB.Set<CustomFieldDefinition>()
                .Where(x => x.BusinessUnitId == TenantB).Select(x => x.Id).SingleAsync();
        }

        await using var contextA = db.ContextFor(TenantA);
        await DefineAsync(contextA, TenantA, "Customer", "mine_field", "Mine", CustomFieldDataType.Text);

        // Naming a foreign id is simply ignored — it is not in this tenant's map at all.
        await new CustomFieldApplicationService(contextA).ReorderDefinitionsAsync(
            TenantA, new ReorderCustomFieldsCommand("Customer", [new(foreignId, 99)]), CancellationToken.None);
        contextA.ChangeTracker.Clear();

        await using var verify = db.ContextFor(TenantB);
        var foreign = await verify.Set<CustomFieldDefinition>().SingleAsync(x => x.Id == foreignId);
        Assert.Equal(0, foreign.DisplayOrder);
    }

    // ---- sensitive fields on the bag path --------------------------------------------------

    [Fact]
    public async Task A_manager_only_field_is_neither_shown_to_nor_writable_by_a_tenant_user()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new CustomFieldApplicationService(context);
        await service.CreateDefinitionAsync(TenantA, new CreateCustomFieldDefinitionCommand(
            "Customer", "target_margin", new("Target margin", CustomFieldDataType.Decimal,
                IsSensitive: true, ViewAccess: CustomFieldAccessLevel.ManagerOrAdmin,
                EditAccess: CustomFieldAccessLevel.ManagerOrAdmin), Activate: true),
            "admin", CancellationToken.None);
        var bag = new CustomFieldBagService(context);

        // The value never leaves the server for a tenant user — a disabled input would still
        // have shipped it to the browser.
        Assert.Empty((await bag.GetAsync(TenantA, "Customer", 9001, false, CancellationToken.None)).Fields);
        Assert.Single((await bag.GetAsync(TenantA, "Customer", 9001, true, CancellationToken.None)).Fields);

        await Assert.ThrowsAsync<CustomFieldConflictException>(() => bag.UpdateAsync(
            TenantA, "Customer", 9001,
            new(new Dictionary<string, JsonElement> { ["target_margin"] = Json("12.5") }),
            false, CancellationToken.None));

        var saved = await bag.UpdateAsync(TenantA, "Customer", 9001,
            new(new Dictionary<string, JsonElement> { ["target_margin"] = Json("12.5") }),
            true, CancellationToken.None);
        Assert.Equal("12.5", saved.Fields.Single().DisplayValue);
    }

    [Fact]
    public async Task A_required_field_is_enforced_by_the_server_on_a_full_record_save()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new CustomFieldApplicationService(context);
        await service.CreateDefinitionAsync(TenantA, new CreateCustomFieldDefinitionCommand(
            "Customer", "vendor_code", new("Vendor code", CustomFieldDataType.Text, IsRequired: true),
            Activate: true), "admin", CancellationToken.None);
        var bag = new CustomFieldBagService(context);

        // The editor's save (EnforceRequired: true) refuses a blank required field. The browser
        // is not the gate; this is.
        await Assert.ThrowsAsync<CustomFieldDomainException>(() => bag.UpdateAsync(
            TenantA, "Customer", 9001,
            new(new Dictionary<string, JsonElement> { ["vendor_code"] = Json("null") }, EnforceRequired: true),
            true, CancellationToken.None));

        // A partial patch of a record that predates the requirement is not blocked by it.
        await bag.UpdateAsync(TenantA, "Customer", 9001,
            new(new Dictionary<string, JsonElement>(), EnforceRequired: false),
            true, CancellationToken.None);
    }

    // ---------------------------------------------------------------------------------------

    private static async Task DefineAsync(
        ErpRfqAutomationContext context, long businessUnitId, string entityType,
        string stableKey, string label, CustomFieldDataType type)
    {
        var definition = CustomFieldDefinition.Create(businessUnitId, entityType, stableKey, "admin", DateTime.UtcNow);
        definition.AddVersion(new(label, type), "admin", DateTime.UtcNow);
        definition.ActivateVersion(1);
        context.Add(definition);
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
            Id = 9001, Name = "Tech Connect", ImageUrl = "n/a", Buid = TenantA,
            IsActive = true, CreatedBy = "seed", CreatedOn = now
        });
        context.Customers.Add(new Customer
        {
            Id = 9002, Name = "Other Tenant Buyer", ImageUrl = "n/a", Buid = TenantB,
            IsActive = true, CreatedBy = "seed", CreatedOn = now
        });
        await context.SaveChangesAsync();
        return db;
    }
}
