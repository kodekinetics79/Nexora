using System.Text.Json;
using ERP_RFQ_Automation.CustomFields;

namespace ERP_RFQ_Automation.Tests.CustomFields;

public sealed class ConditionalRuleTests
{
    [Fact]
    public void Ast_IsValidatedSerializedAndRoundTripsWithoutExecutableCode()
    {
        var condition = new ConditionalGroupNode(ConditionalGroupOperator.All, new ConditionalRuleNode[]
        {
            new ConditionalComparisonNode("delivery_country", CustomFieldComparisonOperator.Equal,
                JsonSerializer.SerializeToElement("AE")),
            new ConditionalComparisonNode("project_value", CustomFieldComparisonOperator.GreaterThan,
                JsonSerializer.SerializeToElement(100_000))
        });
        var definition = CustomFieldDefinition.Create(7, "Rfq", "approval_note", "admin", UtcNow());
        var version = definition.AddVersion(new("Approval note", CustomFieldDataType.Text), "admin", UtcNow());

        var rule = version.AddRule(CustomFieldRuleEffect.Required, condition);
        var restored = Assert.IsType<ConditionalGroupNode>(rule.Condition);

        Assert.Equal(2, restored.Children.Count);
        Assert.Equal(new[] { "delivery_country", "project_value" },
            ConditionalRuleValidator.ReferencedFieldKeys(restored).OrderBy(x => x));
        Assert.DoesNotContain("script", rule.ConditionJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ast_RejectsEmptyGroupsAndMissingOperands()
    {
        Assert.Throws<CustomFieldDomainException>(() => ConditionalRuleValidator.Validate(
            new ConditionalGroupNode(ConditionalGroupOperator.Any, Array.Empty<ConditionalRuleNode>())));
        Assert.Throws<CustomFieldDomainException>(() => ConditionalRuleValidator.Validate(
            new ConditionalComparisonNode("country_code", CustomFieldComparisonOperator.Equal)));
    }

    [Fact]
    public void Ast_RejectsExcessiveDepth()
    {
        ConditionalRuleNode node = new ConditionalComparisonNode(
            "country_code", CustomFieldComparisonOperator.IsNotEmpty);
        for (var i = 0; i < ConditionalRuleValidator.MaximumDepth; i++)
            node = new ConditionalGroupNode(ConditionalGroupOperator.All, new[] { node });

        Assert.Throws<CustomFieldDomainException>(() => ConditionalRuleValidator.Validate(node));
    }

    [Fact]
    public void DependencyGraph_RejectsDirectAndIndirectCycles()
    {
        Assert.Throws<CustomFieldDomainException>(() => CustomFieldDependencyGraph.EnsureAcyclic(new[]
        {
            (DefinitionId: 1L, DependsOnDefinitionId: 2L),
            (DefinitionId: 2L, DependsOnDefinitionId: 3L),
            (DefinitionId: 3L, DependsOnDefinitionId: 1L)
        }));

        CustomFieldDependencyGraph.EnsureAcyclic(new[]
        {
            (DefinitionId: 1L, DependsOnDefinitionId: 2L),
            (DefinitionId: 1L, DependsOnDefinitionId: 3L),
            (DefinitionId: 3L, DependsOnDefinitionId: 4L)
        });
    }

    private static DateTime UtcNow() => new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
}
