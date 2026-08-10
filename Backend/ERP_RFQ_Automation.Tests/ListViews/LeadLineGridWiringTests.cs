using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.ListViews;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests.ListViews;

/// <summary>
/// AA-01 · the RFQ / lead LINE grid.
///
/// The `lead.items` view existed in the catalog and was reachable through the API while no
/// component consumed it — a view key with no reader, which is wiring failure #2 in
/// docs/WIRING_CONTRACT.md. These tests assert the two halves that make it real:
///
///   1. a tenant-defined custom field on a lead LINE reaches the payload the grid renders,
///      so the column the picker offers has something to read;
///   2. the catalog declares the commercial columns the grid can now show, hidden by
///      default, so a deploy never rearranges anybody's grid.
///
/// Delete the `CustomFields = li.CustomFieldsJson` projection in LeadRepository and the first
/// test fails. Delete a commercial column from the catalog and the picker stops offering it,
/// which the third test catches.
/// </summary>
public sealed class LeadLineGridWiringTests
{
    private const long Bu = 7301;
    private const long UserOne = 93;
    private const string View = "lead.items";

    /// <summary>
    /// The keys shipped in the first `lead.items` release. A stored preference refers to
    /// these by name, so renaming one silently discards every user's layout for that column.
    /// </summary>
    private static readonly string[] ShippedKeys =
    [
        "lineItemNo", "productShortName", "manufacturerName", "manufacturerPartNumber",
        "quantity", "unitOfMeasure", "unitPrice", "leadTime", "itemText"
    ];

    /// <summary>
    /// Read-only context the grid can now show. Each one is READ from data the platform
    /// already persists; none of them is computed in the interface.
    /// </summary>
    private static readonly string[] CommercialKeys =
    [
        "documentExtraColumns", "stockAvailable", "stockIncoming", "projectedShortage",
        "supplyStatus", "expectedAvailableOn", "stockUnitCost"
    ];

    [Fact]
    public async Task A_custom_field_value_on_a_lead_line_reaches_the_payload_the_grid_renders()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 8100, Bu, items: new[]
            {
                Seed.LeadItem(1, "10", 5, "Ball valve"),
                Seed.LeadItem(2, "20", 12, "Gasket"),
            });
            seed.SaveChanges();

            // Written the way the governed save interceptor writes it: one flat jsonb object
            // keyed by the definition's stable key.
            var line = lead.LeadItems.Single(x => x.Id == 1);
            line.CustomFieldsJson = "{\"plant_code\":\"JBL-2\"}";
            seed.SaveChanges();
        }

        await using var context = db.ContextFor(Bu);
        var detail = await new LeadRepository(context).GetLeadByIdAsync(8100, Bu);

        Assert.NotNull(detail);
        var withValue = detail!.LeadItems.Single(x => x.Id == 1);
        var withoutValue = detail.LeadItems.Single(x => x.Id == 2);

        // The bag travels raw, because the client materialises the column from it.
        Assert.Equal("{\"plant_code\":\"JBL-2\"}", withValue.CustomFields);
        // And a line with no values is NULL — "nothing set" — never "{}" or "".
        Assert.Null(withoutValue.CustomFields);
    }

    [Fact]
    public async Task A_tenant_field_defined_on_a_lead_line_is_offered_as_a_column_hidden_by_default()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Bu);
        await context.SaveChangesAsync();

        var definition = CustomFieldDefinition.Create(Bu, "LeadItem", "plant_code", "admin", DateTime.UtcNow);
        definition.AddVersion(new("Plant code", CustomFieldDataType.Text), "admin", DateTime.UtcNow);
        definition.ActivateVersion(1);
        definition.SetDisplayOrder(1);
        context.Add(definition);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var resolved = await new ListViewPreferenceService(context)
            .GetAsync(Bu, UserOne, View, CancellationToken.None);

        var column = Assert.Single(resolved.Columns, x => x.Key == "cf:plant_code");
        Assert.Equal("Plant code", column.Label);
        Assert.Equal("customField", column.Source);
        // Hidden by default: one tenant defining a field must never rearrange a grid for
        // somebody who did not ask for it.
        Assert.False(column.Visible);
        Assert.True(resolved.SupportsCustomFields);
    }

    [Fact]
    public void The_line_grid_offers_the_commercial_columns_and_starts_with_none_of_them_on()
    {
        var view = ListViewCatalog.Find(View);
        Assert.NotNull(view);

        foreach (var key in CommercialKeys)
        {
            var column = Assert.Single(view!.Columns, x => x.Key == key);
            // Present, so a rep can choose it. Off, so nobody's grid changes on deploy.
            Assert.False(column.DefaultVisible);
            Assert.False(column.Locked);
        }
    }

    [Fact]
    public void The_columns_shipped_in_the_first_release_are_never_renamed()
    {
        var view = ListViewCatalog.Find(View);
        Assert.NotNull(view);

        foreach (var key in ShippedKeys)
            Assert.Contains(view!.Columns, x => x.Key == key);
    }

    [Fact]
    public void The_review_verdict_and_the_row_actions_can_be_reordered_but_not_hidden()
    {
        var view = ListViewCatalog.Find(View);
        Assert.NotNull(view);

        foreach (var key in new[] { "checkStatus", "actions" })
        {
            var column = Assert.Single(view!.Columns, x => x.Key == key);
            Assert.True(column.Locked);
            Assert.True(column.DefaultVisible);
        }
    }

    [Fact]
    public async Task A_locked_line_column_cannot_be_hidden_by_a_client_that_asks()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Bu);
        context.Users.Add(new User
        {
            Id = UserOne,
            FirstName = "Reviewer",
            LastName = "Test",
            Email = "reviewer@example.com",
            PasswordHash = "x",
            ImageUrl = "n/a",
            Buid = Bu,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();
        var service = new ListViewPreferenceService(context);

        var saved = await service.SaveAsync(Bu, UserOne, View,
            new([new("checkStatus", false), new("lineItemNo", true), new("actions", false)]),
            "reviewer", CancellationToken.None);

        Assert.True(saved.Columns.Single(x => x.Key == "checkStatus").Visible);
        Assert.True(saved.Columns.Single(x => x.Key == "actions").Visible);
    }
}
