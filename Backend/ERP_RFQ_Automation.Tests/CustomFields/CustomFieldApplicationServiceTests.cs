using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.CustomFields;

public sealed class CustomFieldApplicationServiceTests
{
    [Fact]
    public async Task Definition_lifecycle_exposes_an_activated_typed_schema()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);

        var created = await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "lead", "shipping_mode", new("Shipping mode", CustomFieldDataType.Option, IsRequired: true),
            Options: [new("air", "Air", 1), new("sea", "Sea", 2)], Activate: true),
            "admin@example.com", CancellationToken.None);
        var schema = await service.GetEntitySchemaAsync(71, "Lead", 701, false, CancellationToken.None);

        Assert.Equal(CustomFieldDefinitionStatus.Active, created.Status);
        Assert.Equal(1, created.ActiveVersionNumber);
        var field = Assert.Single(schema.Fields);
        Assert.Equal("shipping_mode", field.StableKey);
        Assert.Equal(CustomFieldDataType.Option, field.Version.DataType);
        Assert.Equal(2, field.Version.Options.Count);
        Assert.Null(field.Value);
    }

    [Fact]
    public async Task The_legacy_value_write_path_fails_closed_and_names_its_replacement()
    {
        // AA-01 retired this route: custom-field values live in ONE jsonb bag on the owning
        // row. It fails loudly rather than 404-ing so an old caller is told where to go.
        //
        // Deliberately NOT carried over to the bag path, and therefore no longer covered by
        // any test because the behaviour no longer exists: per-value optimistic concurrency,
        // idempotency-key replay, and custom_field_value_history audit rows.
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "project_code", new("Project code", CustomFieldDataType.Text, MinimumLength: 3),
            Activate: true), "admin", CancellationToken.None);

        var error = await Assert.ThrowsAsync<CustomFieldWritePathRetiredException>(() =>
            service.UpsertValueAsync(71, "Lead", 701, "project_code",
                new(new(Text: "ALPHA"), null, "value-create-701", "corr-create"),
                "user@example.com", false, CancellationToken.None));

        Assert.Contains("/api/custom-fields/records/", error.Message);
        Assert.Equal(0, await context.Set<CustomFieldValue>().CountAsync());
    }

    [Fact]
    public async Task Sensitive_fields_are_hidden_from_tenant_users_in_the_schema()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "target_margin", new("Target margin", CustomFieldDataType.Decimal,
                IsSensitive: true, ViewAccess: CustomFieldAccessLevel.ManagerOrAdmin,
                EditAccess: CustomFieldAccessLevel.ManagerOrAdmin), Activate: true),
            "admin", CancellationToken.None);

        var tenantSchema = await service.GetEntitySchemaAsync(71, "Lead", 701, false, CancellationToken.None);
        var managerSchema = await service.GetEntitySchemaAsync(71, "Lead", 701, true, CancellationToken.None);

        // A tenant user does not see the field at all; a manager does. The matching EDIT gate
        // now lives on the jsonb bag path — see CustomFieldBagTests.
        Assert.Empty(tenantSchema.Fields);
        Assert.Single(managerSchema.Fields);
    }

    [Fact]
    public async Task Entity_and_definition_access_fail_closed_across_tenants()
    {
        using var db = await SeedLeadAsync();
        await using (var tenant71 = db.ContextFor(71))
        {
            var service = new CustomFieldApplicationService(tenant71);
            await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
                "Lead", "project_code", new("Project code", CustomFieldDataType.Text), Activate: true),
                "admin", CancellationToken.None);
        }
        await using var tenant72 = db.ContextFor(72);
        var tenant72Service = new CustomFieldApplicationService(tenant72);

        await Assert.ThrowsAsync<CustomFieldNotFoundException>(() => tenant72Service.GetEntitySchemaAsync(
            72, "Lead", 701, true, CancellationToken.None));
        Assert.Empty(await tenant72Service.ListDefinitionsAsync(72, "Lead", CancellationToken.None));
    }

    [Fact]
    public async Task Activation_rejects_option_fields_without_options_and_unknown_rule_keys()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);

        await Assert.ThrowsAsync<CustomFieldDomainException>(() => service.CreateDefinitionAsync(71,
            new CreateCustomFieldDefinitionCommand(
                "Lead", "shipping_mode", new("Shipping mode", CustomFieldDataType.Option), Activate: true),
            "admin", CancellationToken.None));
        await Assert.ThrowsAsync<CustomFieldDomainException>(() => service.CreateDefinitionAsync(71,
            new CreateCustomFieldDefinitionCommand(
                "Lead", "priority_note", new("Priority note", CustomFieldDataType.Text),
                Rules: [new(CustomFieldRuleEffect.Visible,
                    new ConditionalComparisonNode("unknown_field", CustomFieldComparisonOperator.IsNotEmpty))],
                Activate: true), "admin", CancellationToken.None));
        Assert.Empty(await context.Set<CustomFieldDefinition>().ToListAsync());
    }

    [Fact]
    public async Task Activation_rejects_dependency_cycles_and_rolls_back_the_new_version()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        var first = await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "first_field", new("First", CustomFieldDataType.Text), Activate: true),
            "admin", CancellationToken.None);
        var second = await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "second_field", new("Second", CustomFieldDataType.Text),
            DependencyDefinitionIds: [first.Id], Activate: true), "admin", CancellationToken.None);

        await Assert.ThrowsAsync<CustomFieldDomainException>(() => service.AddVersionAsync(
            71, first.Id, new AddCustomFieldVersionCommand(
                new("First v2", CustomFieldDataType.Text), DependencyDefinitionIds: [second.Id], Activate: true),
            "admin", CancellationToken.None));

        context.ChangeTracker.Clear();
        var stored = Assert.Single(await service.ListDefinitionsAsync(71, "Lead", CancellationToken.None),
            x => x.Id == first.Id);
        Assert.Single(stored.Versions);
        Assert.Equal(1, stored.ActiveVersionNumber);
    }

    [Fact]
    public async Task Retirement_rejects_a_definition_required_by_an_active_schema()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        var parent = await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "parent_field", new("Parent", CustomFieldDataType.Text), Activate: true),
            "admin", CancellationToken.None);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "dependent_field", new("Dependent", CustomFieldDataType.Text),
            DependencyDefinitionIds: [parent.Id], Activate: true), "admin", CancellationToken.None);

        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.RetireDefinitionAsync(
            71, parent.Id, new RetireCustomFieldDefinitionCommand("No longer used"),
            "admin", CancellationToken.None));
    }

    [Fact]
    public async Task Conditional_read_only_rules_are_still_reported_by_the_schema_read()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        var control = await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "control_state", new("Control", CustomFieldDataType.Text), Activate: true),
            "admin", CancellationToken.None);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "governed_note", new("Governed note", CustomFieldDataType.Text),
            Rules: [new(CustomFieldRuleEffect.ReadOnly, new ConditionalComparisonNode(
                "control_state", CustomFieldComparisonOperator.Equal,
                JsonSerializer.SerializeToElement("locked")))],
            DependencyDefinitionIds: [control.Id], Activate: true), "admin", CancellationToken.None);
        await SeedLegacyValueAsync(context, 71, "Lead", 701, "governed_note", new(Text: "original"));
        await SeedLegacyValueAsync(context, 71, "Lead", 701, "control_state", new(Text: "locked"));

        var schema = await service.GetEntitySchemaAsync(71, "Lead", 701, false, CancellationToken.None);

        // The READ side still reports the rule. ENFORCEMENT of conditional rules on write went
        // with the retired EAV write path and is NOT reimplemented on the jsonb bag — stated
        // here so the gap is visible in the suite rather than only in a report.
        Assert.True(schema.Fields.Single(x => x.StableKey == "governed_note").IsReadOnly);
    }

    [Fact]
    public async Task Activation_rejects_a_version_that_invalidates_existing_values()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        var definition = await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "typed_value", new("Typed value", CustomFieldDataType.Text), Activate: true),
            "admin", CancellationToken.None);
        await SeedLegacyValueAsync(context, 71, "Lead", 701, "typed_value", new(Text: "text"));

        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.AddVersionAsync(
            71, definition.Id, new AddCustomFieldVersionCommand(
                new("Typed value", CustomFieldDataType.Decimal), Activate: true),
            "admin", CancellationToken.None));
        context.ChangeTracker.Clear();
        var stored = Assert.Single(await service.ListDefinitionsAsync(71, "Lead", CancellationToken.None));
        Assert.Single(stored.Versions);
        Assert.Equal(1, stored.ActiveVersionNumber);
    }

    /// <summary>
    /// Writes a row into the legacy EAV value table directly. The table and its READ path are
    /// still supported; only the service/API write path was retired, so tests that need
    /// pre-existing legacy values seed them through the entities.
    /// </summary>
    private static async Task SeedLegacyValueAsync(
        ErpRfqAutomationContext context, long businessUnitId, string entityType, long entityId,
        string stableKey, CustomFieldValueInput input)
    {
        var definition = await context.Set<CustomFieldDefinition>().Include(x => x.Versions)
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.StableKey == stableKey);
        var version = definition.Versions.Single(v => v.VersionNumber == definition.ActiveVersionNumber);

        var record = await context.Set<CustomFieldRecord>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.EntityType == entityType && x.EntityId == entityId);
        if (record is null)
        {
            record = CustomFieldRecord.Create(businessUnitId, entityType, entityId, DateTime.UtcNow);
            context.Add(record);
            await context.SaveChangesAsync();
        }

        context.Add(CustomFieldValue.Create(
            businessUnitId, record.Id, definition.Id, version, input, "seed", DateTime.UtcNow));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task<TestDb> SeedLeadAsync()
    {
        var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 701, 71);
        await context.SaveChangesAsync();
        return db;
    }
}
