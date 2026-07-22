using ERP_RFQ_Automation.CommercialRouting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

public sealed class CommercialRoutingModelTests
{
    [Fact]
    public void Model_RegistersAllTenantOwnedRoutingEntities()
    {
        var model = CreateModel();
        var expected = new[]
        {
            typeof(CustomerIdentifier), typeof(CustomerOwnership), typeof(LeadRoutingDecision),
            typeof(LeadAssignment), typeof(UnassignedWorkItem)
        };

        foreach (var type in expected)
        {
            var entity = model.FindEntityType(type);
            Assert.NotNull(entity);
            Assert.False(entity!.FindProperty(nameof(CustomerIdentifier.BusinessUnitId))!.IsNullable);
        }
    }

    [Theory]
    [InlineData(typeof(CustomerIdentifier))]
    [InlineData(typeof(LeadRoutingDecision))]
    [InlineData(typeof(LeadAssignment))]
    [InlineData(typeof(UnassignedWorkItem))]
    public void Model_HasTenantScopedUniqueConstraint(Type entityType)
    {
        var entity = CreateModel().FindEntityType(entityType)!;

        Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
            index.Properties.Any(property => property.Name == nameof(CustomerIdentifier.BusinessUnitId)));
    }

    [Fact]
    public void Model_MarksMutableOwnershipAndQueueAsConcurrencyControlled()
    {
        var model = CreateModel();

        Assert.True(model.FindEntityType(typeof(CustomerOwnership))!
            .FindProperty(nameof(CustomerOwnership.Version))!.IsConcurrencyToken);
        Assert.True(model.FindEntityType(typeof(UnassignedWorkItem))!
            .FindProperty(nameof(UnassignedWorkItem.Version))!.IsConcurrencyToken);
    }

    [Fact]
    public void Model_UsesRestrictDeleteForLeadHistory()
    {
        var assignment = CreateModel().FindEntityType(typeof(LeadAssignment))!;

        Assert.All(assignment.GetForeignKeys(), foreignKey =>
            Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    private static IMutableModel CreateModel()
    {
        var builder = new ModelBuilder(new ConventionSet());
        builder.ApplyCommercialRoutingModel();
        return builder.Model;
    }
}
