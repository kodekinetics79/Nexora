using ERP_RFQ_Automation.CustomFields;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CustomFields;

public sealed class CustomFieldPersistenceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ModelBuilderExtension_RegistersTheCompleteDomainAndGovernanceIndexes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = CreateContext(connection);
        var model = context.Model;

        Assert.NotNull(model.FindEntityType(typeof(CustomFieldDefinition)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldVersion)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldOption)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldRule)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldDependency)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldRecord)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldValue)));
        Assert.NotNull(model.FindEntityType(typeof(CustomFieldValueHistory)));

        var definition = model.FindEntityType(typeof(CustomFieldDefinition))!;
        Assert.Contains(definition.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(x => x.Name).SequenceEqual(new[]
                { "BusinessUnitId", "EntityType", "StableKey" }));
    }

    [Fact]
    public void Mapping_PersistsDefinitionVersionOptionsAndRules()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = Options(connection, includeInterceptor: true);

        using (var context = new CustomFieldTestContext(options))
        {
            context.Database.EnsureCreated();
            var definition = CustomFieldDefinition.Create(7, "Rfq", "shipping_mode", "admin", Now);
            var version = definition.AddVersion(new("Shipping mode", CustomFieldDataType.Option), "admin", Now);
            version.AddOption("air_freight", "Air freight", 1);
            version.AddRule(CustomFieldRuleEffect.Visible,
                new ConditionalComparisonNode("country_code", CustomFieldComparisonOperator.IsNotEmpty));
            definition.ActivateVersion(1);
            context.Add(definition);
            context.SaveChanges();
        }

        using var verify = new CustomFieldTestContext(options);
        var stored = verify.Set<CustomFieldDefinition>()
            .Include(x => x.Versions).ThenInclude(x => x.Options)
            .Include(x => x.Versions).ThenInclude(x => x.Rules)
            .Single();
        Assert.Equal(CustomFieldDefinitionStatus.Active, stored.Status);
        Assert.Single(stored.Versions.Single().Options);
        Assert.Single(stored.Versions.Single().Rules);
    }

    [Fact]
    public void GovernanceInterceptor_BlocksVersionMutationAndDeletion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = Options(connection, includeInterceptor: true);
        long definitionId;

        using (var context = new CustomFieldTestContext(options))
        {
            context.Database.EnsureCreated();
            var definition = CustomFieldDefinition.Create(7, "Rfq", "project_code", "admin", Now);
            definition.AddVersion(new("Project code", CustomFieldDataType.Text), "admin", Now);
            context.Add(definition);
            context.SaveChanges();
            definitionId = definition.Id;
        }

        using (var context = new CustomFieldTestContext(options))
        {
            var version = context.Set<CustomFieldVersion>().Single();
            context.Entry(version).Property(x => x.Label).CurrentValue = "Changed in place";
            Assert.Throws<CustomFieldDomainException>(() => context.SaveChanges());
        }

        using (var context = new CustomFieldTestContext(options))
        {
            var definition = context.Set<CustomFieldDefinition>().Single(x => x.Id == definitionId);
            context.Remove(definition);
            Assert.Throws<CustomFieldDomainException>(() => context.SaveChanges());
        }
    }

    private static CustomFieldTestContext CreateContext(SqliteConnection connection) =>
        new(Options(connection, includeInterceptor: false));

    private static DbContextOptions<CustomFieldTestContext> Options(
        SqliteConnection connection, bool includeInterceptor)
    {
        var builder = new DbContextOptionsBuilder<CustomFieldTestContext>().UseSqlite(connection);
        if (includeInterceptor) builder.AddInterceptors(new CustomFieldGovernanceInterceptor());
        return builder.Options;
    }

    private sealed class CustomFieldTestContext(DbContextOptions<CustomFieldTestContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureGovernedCustomFields();
    }
}
