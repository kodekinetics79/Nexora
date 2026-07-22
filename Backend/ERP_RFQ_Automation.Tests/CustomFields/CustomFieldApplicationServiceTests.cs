using ERP_RFQ_Automation.CustomFields;
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
    public async Task Value_writes_are_typed_audited_idempotent_and_version_guarded()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "project_code", new("Project code", CustomFieldDataType.Text, MinimumLength: 3),
            Activate: true), "admin", CancellationToken.None);
        var create = new UpsertCustomFieldValueCommand(
            new(Text: "ALPHA"), null, "value-create-701", "corr-create");

        var first = await service.UpsertValueAsync(
            71, "Lead", 701, "project_code", create, "user@example.com", false, CancellationToken.None);
        var replay = await service.UpsertValueAsync(
            71, "Lead", 701, "project_code", create, "user@example.com", false, CancellationToken.None);
        var updated = await service.UpsertValueAsync(71, "Lead", 701, "project_code",
            new(new(Text: "BRAVO"), 1, "value-update-701", "corr-update", "Customer correction"),
            "user@example.com", false, CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, updated.Version);
        Assert.Equal("BRAVO", updated.Value.Text);
        Assert.Equal(2, await context.Set<CustomFieldValueHistory>().CountAsync());
        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.UpsertValueAsync(
            71, "Lead", 701, "project_code",
            new(new(Text: "CHARLIE"), 1, "value-stale-701", "corr-stale"),
            "user@example.com", false, CancellationToken.None));
    }

    [Fact]
    public async Task Sensitive_fields_are_hidden_and_cannot_be_edited_by_tenant_users()
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

        Assert.Empty(tenantSchema.Fields);
        Assert.Single(managerSchema.Fields);
        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.UpsertValueAsync(
            71, "Lead", 701, "target_margin",
            new(new(Decimal: 12.5m), null, "margin-user", "corr-user"),
            "user", false, CancellationToken.None));
        var value = await service.UpsertValueAsync(
            71, "Lead", 701, "target_margin",
            new(new(Decimal: 12.5m), null, "margin-admin", "corr-admin"),
            "admin", true, CancellationToken.None);
        Assert.Equal(12.5m, value.Value.Decimal);
    }

    [Fact]
    public async Task Idempotency_replay_is_bound_to_the_authorized_entity_and_definition()
    {
        using var db = await SeedLeadAsync();
        await using var context = db.ContextFor(71);
        var service = new CustomFieldApplicationService(context);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "public_note", new("Public note", CustomFieldDataType.Text), Activate: true),
            "admin", CancellationToken.None);
        await service.CreateDefinitionAsync(71, new CreateCustomFieldDefinitionCommand(
            "Lead", "secret_note", new("Secret note", CustomFieldDataType.Text,
                IsSensitive: true, ViewAccess: CustomFieldAccessLevel.ManagerOrAdmin,
                EditAccess: CustomFieldAccessLevel.ManagerOrAdmin), Activate: true),
            "admin", CancellationToken.None);
        var key = "sensitive-operation-701";
        await service.UpsertValueAsync(71, "Lead", 701, "secret_note",
            new(new(Text: "restricted"), null, key, "corr-sensitive"),
            "admin", true, CancellationToken.None);

        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.UpsertValueAsync(
            71, "Lead", 701, "public_note",
            new(new(Text: "unrelated"), null, key, "corr-public"),
            "user", false, CancellationToken.None));
        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.UpsertValueAsync(
            71, "Lead", 701, "secret_note",
            new(new(Text: "restricted"), null, key, "corr-sensitive"),
            "user", false, CancellationToken.None));
        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.UpsertValueAsync(
            71, "Lead", 701, "secret_note",
            new(new(Text: "different payload"), null, key, "corr-sensitive"),
            "admin", true, CancellationToken.None));
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
    public async Task Conditional_read_only_rules_are_enforced_by_schema_and_write_api()
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
        var note = await service.UpsertValueAsync(71, "Lead", 701, "governed_note",
            new(new(Text: "original"), null, "note-create", "corr-note"),
            "user", false, CancellationToken.None);
        await service.UpsertValueAsync(71, "Lead", 701, "control_state",
            new(new(Text: "locked"), null, "control-create", "corr-control"),
            "user", false, CancellationToken.None);

        var schema = await service.GetEntitySchemaAsync(71, "Lead", 701, false, CancellationToken.None);
        Assert.True(schema.Fields.Single(x => x.StableKey == "governed_note").IsReadOnly);
        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.UpsertValueAsync(
            71, "Lead", 701, "governed_note",
            new(new(Text: "changed"), note.Version, "note-update", "corr-note-update"),
            "user", false, CancellationToken.None));
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
        await service.UpsertValueAsync(71, "Lead", 701, "typed_value",
            new(new(Text: "text"), null, "typed-create", "corr-typed"),
            "user", false, CancellationToken.None);

        await Assert.ThrowsAsync<CustomFieldConflictException>(() => service.AddVersionAsync(
            71, definition.Id, new AddCustomFieldVersionCommand(
                new("Typed value", CustomFieldDataType.Decimal), Activate: true),
            "admin", CancellationToken.None));
        context.ChangeTracker.Clear();
        var stored = Assert.Single(await service.ListDefinitionsAsync(71, "Lead", CancellationToken.None));
        Assert.Single(stored.Versions);
        Assert.Equal(1, stored.ActiveVersionNumber);
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
