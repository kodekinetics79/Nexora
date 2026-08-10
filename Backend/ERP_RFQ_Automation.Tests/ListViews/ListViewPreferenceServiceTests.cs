using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.ListViews;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.ListViews;

/// <summary>
/// AA-01 · per-user list-view column preferences.
///
/// These tests exist because the product owner's requirement is specifically that the layout
/// is the USER's — "as per user preference not a fixed one". The isolation tests below are
/// the ones that prove that claim, and the stale-key tests are the ones that stop a shipped
/// column rename from breaking somebody's grid.
/// </summary>
public sealed class ListViewPreferenceServiceTests
{
    private const long TenantA = 4101;
    private const long TenantB = 4102;
    private const long UserOne = 91;
    private const long UserTwo = 92;
    private const string View = "customers.list";

    [Fact]
    public async Task A_user_with_no_stored_preference_gets_the_catalog_default_and_nothing_is_written()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        var resolved = await service.GetAsync(TenantA, UserOne, View, CancellationToken.None);

        Assert.False(resolved.IsCustomised);
        Assert.Equal(
            ListViewCatalog.Find(View)!.Columns.Select(x => x.Key),
            resolved.Columns.Select(x => x.Key));
        // Defaults live in code. Reading them must not create a row.
        Assert.Equal(0, await context.Set<ColumnPreference>().CountAsync());
    }

    [Fact]
    public async Task A_saved_layout_is_visible_to_its_owner_and_invisible_to_every_other_user()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        await service.SaveAsync(TenantA, UserOne, View, new([
            new("name", true),
            new("docId", false),
            new("contactEmail", true)
        ]), "one@example.com", CancellationToken.None);

        var owner = await service.GetAsync(TenantA, UserOne, View, CancellationToken.None);
        var colleague = await service.GetAsync(TenantA, UserTwo, View, CancellationToken.None);

        Assert.True(owner.IsCustomised);
        Assert.Equal("name", owner.Columns[0].Key);
        Assert.False(owner.Columns.Single(x => x.Key == "docId").Visible);

        // The colleague is untouched: same tenant, different person, declared defaults.
        Assert.False(colleague.IsCustomised);
        Assert.Equal("docId", colleague.Columns[0].Key);
        Assert.True(colleague.Columns.Single(x => x.Key == "docId").Visible);
    }

    [Fact]
    public async Task A_saved_layout_never_crosses_a_business_unit_boundary()
    {
        using var db = await SeedAsync();

        await using (var contextA = db.ContextFor(TenantA))
        {
            await new ListViewPreferenceService(contextA).SaveAsync(
                TenantA, UserOne, View, new([new("contactEmail", true), new("name", true)]),
                "one@example.com", CancellationToken.None);
        }

        // Same user id, different business unit: a distinct working context, so a distinct layout.
        await using (var contextB = db.ContextFor(TenantB))
        {
            var other = await new ListViewPreferenceService(contextB)
                .GetAsync(TenantB, UserOne, View, CancellationToken.None);
            Assert.False(other.IsCustomised);
            Assert.Equal("docId", other.Columns[0].Key);
        }

        // And the tenant-scoped context must not even be able to see the other tenant's row.
        await using var scopedToB = db.ContextFor(TenantB);
        Assert.Equal(0, await scopedToB.Set<ColumnPreference>().CountAsync());
        await using var scopedToA = db.ContextFor(TenantA);
        Assert.Equal(1, await scopedToA.Set<ColumnPreference>().CountAsync());
    }

    [Fact]
    public async Task An_unknown_column_key_in_a_stored_layout_is_ignored_rather_than_thrown()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        // Simulates a preference saved before a column was renamed or a custom field retired.
        var row = ColumnPreference.Create(TenantA, UserOne, View, [
            new("a_column_that_no_longer_exists", true),
            new("name", true),
            new("cf:retired_field", true)
        ], "one@example.com", DateTime.UtcNow);
        context.Set<ColumnPreference>().Add(row);
        await context.SaveChangesAsync();

        var resolved = await service.GetAsync(TenantA, UserOne, View, CancellationToken.None);

        Assert.DoesNotContain(resolved.Columns, x => x.Key == "a_column_that_no_longer_exists");
        Assert.DoesNotContain(resolved.Columns, x => x.Key == "cf:retired_field");
        Assert.Equal("name", resolved.Columns[0].Key);
        // Everything the catalog still declares is present — nothing is lost to the stale keys.
        Assert.Equal(
            ListViewCatalog.Find(View)!.Columns.Select(x => x.Key).OrderBy(x => x),
            resolved.Columns.Select(x => x.Key).OrderBy(x => x));
    }

    [Fact]
    public async Task A_corrupt_stored_payload_degrades_to_the_declared_default()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);

        var row = ColumnPreference.Create(TenantA, UserOne, View, [new("name", true)],
            "one@example.com", DateTime.UtcNow);
        context.Set<ColumnPreference>().Add(row);
        await context.SaveChangesAsync();

        // Write garbage straight past the entity, as a bad migration or manual edit would.
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"UserColumnPreferences\" SET \"Columns\" = 'not json at all'");
        context.ChangeTracker.Clear();

        var resolved = await new ListViewPreferenceService(context)
            .GetAsync(TenantA, UserOne, View, CancellationToken.None);

        Assert.Equal(
            ListViewCatalog.Find(View)!.Columns.Select(x => x.Key),
            resolved.Columns.Select(x => x.Key));
    }

    [Fact]
    public async Task A_locked_column_cannot_be_hidden_even_if_the_client_asks()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        var saved = await service.SaveAsync(TenantA, UserOne, View,
            new([new("actions", false), new("name", true)]), "one@example.com", CancellationToken.None);

        Assert.True(saved.Columns.Single(x => x.Key == "actions").Visible);
        Assert.True(saved.Columns.Single(x => x.Key == "actions").Locked);
    }

    [Fact]
    public async Task A_column_added_to_the_catalog_after_a_layout_was_saved_still_appears()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        await service.SaveAsync(TenantA, UserOne, View,
            new([new("name", true), new("docId", true)]), "one@example.com", CancellationToken.None);

        var resolved = await service.GetAsync(TenantA, UserOne, View, CancellationToken.None);

        // The two saved columns lead, in saved order; everything else follows at its default.
        Assert.Equal("name", resolved.Columns[0].Key);
        Assert.Equal("docId", resolved.Columns[1].Key);
        Assert.Contains(resolved.Columns, x => x.Key == "isActive");
        Assert.True(resolved.Columns.Single(x => x.Key == "isActive").Visible);
        Assert.False(resolved.Columns.Single(x => x.Key == "createdOn").Visible);
    }

    [Fact]
    public async Task Reset_removes_the_stored_row_and_returns_the_declared_default()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        await service.SaveAsync(TenantA, UserOne, View,
            new([new("contactEmail", true), new("name", false)]), "one@example.com", CancellationToken.None);
        var afterReset = await service.ResetAsync(TenantA, UserOne, View, CancellationToken.None);

        Assert.False(afterReset.IsCustomised);
        Assert.Equal(
            ListViewCatalog.Find(View)!.Columns.Select(x => x.Key),
            afterReset.Columns.Select(x => x.Key));
        Assert.Equal(0, await context.Set<ColumnPreference>().CountAsync());
    }

    [Fact]
    public async Task Saving_twice_updates_the_one_row_rather_than_accumulating_rows()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        await service.SaveAsync(TenantA, UserOne, View, new([new("name", true)]), "one", CancellationToken.None);
        await service.SaveAsync(TenantA, UserOne, View, new([new("docId", true)]), "one", CancellationToken.None);

        Assert.Equal(1, await context.Set<ColumnPreference>().CountAsync());
    }

    [Fact]
    public async Task A_view_with_no_custom_field_attach_point_says_so()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        // Lead (the header) carries no jsonb value bag, so the picker must not promise that a
        // tenant-defined field will ever appear on this grid. An empty "Custom" section with
        // no explanation reads as a bug.
        var leads = await service.GetAsync(TenantA, UserOne, "leads.list", CancellationToken.None);
        Assert.False(leads.SupportsCustomFields);
        Assert.DoesNotContain(leads.Columns, x => x.Source == "customField");

        // Customers, Suppliers and lead LINES do have an attach point.
        foreach (var viewKey in new[] { "customers.list", "suppliers.list", "lead.items" })
            Assert.True((await service.GetAsync(TenantA, UserOne, viewKey, CancellationToken.None))
                .SupportsCustomFields);
    }

    [Fact]
    public async Task A_custom_field_defined_on_Lead_never_reaches_the_leads_grid()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        // Legal to define (Lead is a governed entity type) but it has no bag, so it must not
        // be offered as a column on a grid that could never render it.
        await DefineCustomFieldAsync(context, TenantA, "Lead", "campaign_code", "Campaign code", 1);

        var leads = await new ListViewPreferenceService(context)
            .GetAsync(TenantA, UserOne, "leads.list", CancellationToken.None);

        Assert.DoesNotContain(leads.Columns, x => x.Key == "cf:campaign_code");
    }

    [Fact]
    public async Task An_unregistered_view_key_is_refused_rather_than_silently_defaulted()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        var service = new ListViewPreferenceService(context);

        await Assert.ThrowsAsync<ListViewNotFoundException>(() =>
            service.GetAsync(TenantA, UserOne, "not.a.real.view", CancellationToken.None));
    }

    // ---- composition with tenant-defined custom fields ------------------------------------

    [Fact]
    public async Task An_active_tenant_custom_field_becomes_a_selectable_column_hidden_by_default()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineCustomFieldAsync(context, TenantA, "Customer", "vendor_code", "Our vendor code", 1);

        var resolved = await new ListViewPreferenceService(context)
            .GetAsync(TenantA, UserOne, View, CancellationToken.None);

        var column = resolved.Columns.Single(x => x.Key == "cf:vendor_code");
        Assert.Equal("Our vendor code", column.Label);
        Assert.Equal("customField", column.Source);
        Assert.Equal(CustomFieldDataType.Text, column.DataType);
        // Off by default: one tenant adding a field must not rearrange everybody's grid.
        Assert.False(column.Visible);
        Assert.False(column.Locked);
    }

    [Fact]
    public async Task A_tenant_custom_field_never_leaks_into_another_tenants_column_picker()
    {
        using var db = await SeedAsync();
        await using (var contextA = db.ContextFor(TenantA))
        {
            await DefineCustomFieldAsync(contextA, TenantA, "Customer", "vendor_code", "Our vendor code", 1);
        }

        await using var contextB = db.ContextFor(TenantB);
        var resolved = await new ListViewPreferenceService(contextB)
            .GetAsync(TenantB, UserOne, View, CancellationToken.None);

        Assert.DoesNotContain(resolved.Columns, x => x.Source == "customField");
    }

    [Fact]
    public async Task A_draft_or_retired_custom_field_is_not_offered_as_a_column()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);

        // Draft: created but never activated.
        var draft = CustomFieldDefinition.Create(TenantA, "Customer", "draft_field", "admin", DateTime.UtcNow);
        draft.AddVersion(new("Draft field", CustomFieldDataType.Text), "admin", DateTime.UtcNow);
        context.Add(draft);

        // Retired: activated, then withdrawn.
        var retired = CustomFieldDefinition.Create(TenantA, "Customer", "retired_field", "admin", DateTime.UtcNow);
        retired.AddVersion(new("Retired field", CustomFieldDataType.Text), "admin", DateTime.UtcNow);
        retired.ActivateVersion(1);
        retired.Retire("admin", "No longer collected", DateTime.UtcNow);
        context.Add(retired);
        await context.SaveChangesAsync();

        var resolved = await new ListViewPreferenceService(context)
            .GetAsync(TenantA, UserOne, View, CancellationToken.None);

        Assert.DoesNotContain(resolved.Columns, x => x.Key == "cf:draft_field");
        Assert.DoesNotContain(resolved.Columns, x => x.Key == "cf:retired_field");
    }

    [Fact]
    public async Task Custom_field_columns_follow_the_display_order_declared_on_the_definition()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineCustomFieldAsync(context, TenantA, "Customer", "zulu_field", "Zulu", displayOrder: 1);
        await DefineCustomFieldAsync(context, TenantA, "Customer", "alpha_field", "Alpha", displayOrder: 2);

        var resolved = await new ListViewPreferenceService(context)
            .GetAsync(TenantA, UserOne, View, CancellationToken.None);

        var customKeys = resolved.Columns.Where(x => x.Source == "customField").Select(x => x.Key).ToArray();
        Assert.Equal(new[] { "cf:zulu_field", "cf:alpha_field" }, customKeys);
    }

    [Fact]
    public async Task A_user_can_turn_a_custom_field_column_on_and_it_persists_for_that_user_only()
    {
        using var db = await SeedAsync();
        await using var context = db.ContextFor(TenantA);
        await DefineCustomFieldAsync(context, TenantA, "Customer", "vendor_code", "Our vendor code", 1);
        var service = new ListViewPreferenceService(context);

        await service.SaveAsync(TenantA, UserOne, View,
            new([new("cf:vendor_code", true), new("name", true)]), "one@example.com", CancellationToken.None);

        var owner = await service.GetAsync(TenantA, UserOne, View, CancellationToken.None);
        var colleague = await service.GetAsync(TenantA, UserTwo, View, CancellationToken.None);

        Assert.Equal("cf:vendor_code", owner.Columns[0].Key);
        Assert.True(owner.Columns[0].Visible);
        Assert.False(colleague.Columns.Single(x => x.Key == "cf:vendor_code").Visible);
    }

    // ---------------------------------------------------------------------------------------

    private static async Task<TestDb> SeedAsync()
    {
        var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, TenantA);
        Seed.EnsureBusinessUnit(context, TenantB);
        foreach (var (id, buid) in new[] { (UserOne, TenantA), (UserTwo, TenantA), (UserOne + 1000, TenantB) })
        {
            context.Users.Add(new User
            {
                Id = id,
                FirstName = $"User{id}",
                LastName = "Test",
                Email = $"user{id}@example.com",
                PasswordHash = "x",
                ImageUrl = "n/a",
                Buid = buid,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }
        // UserOne also exists inside TenantB in real deployments only as a distinct account;
        // the preference row itself carries BusinessUnitId, which is what the test asserts on.
        await context.SaveChangesAsync();
        return db;
    }

    private static async Task DefineCustomFieldAsync(
        ErpRfqAutomationContext context, long businessUnitId, string entityType,
        string stableKey, string label, int displayOrder)
    {
        var definition = CustomFieldDefinition.Create(businessUnitId, entityType, stableKey, "admin", DateTime.UtcNow);
        definition.AddVersion(new(label, CustomFieldDataType.Text), "admin", DateTime.UtcNow);
        definition.ActivateVersion(1);
        definition.SetDisplayOrder(displayOrder);
        context.Add(definition);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
