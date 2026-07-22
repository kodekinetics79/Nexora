using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.CustomFields;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConditionalGroupNode), "group")]
[JsonDerivedType(typeof(ConditionalComparisonNode), "comparison")]
public abstract record ConditionalRuleNode;

public enum ConditionalGroupOperator
{
    All,
    Any
}

public sealed record ConditionalGroupNode(
    ConditionalGroupOperator Operator,
    IReadOnlyList<ConditionalRuleNode> Children) : ConditionalRuleNode;

public sealed record ConditionalComparisonNode(
    string FieldKey,
    CustomFieldComparisonOperator Operator,
    JsonElement? Operand = null) : ConditionalRuleNode;

public static class ConditionalRuleValidator
{
    public const int MaximumDepth = 8;
    public const int MaximumNodes = 100;

    public static void Validate(ConditionalRuleNode? root)
    {
        if (root is null) throw new CustomFieldDomainException("A conditional rule requires a condition.");
        var count = 0;
        ValidateNode(root, 1, ref count);
    }

    public static IReadOnlySet<string> ReferencedFieldKeys(ConditionalRuleNode root)
    {
        Validate(root);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectKeys(root, keys);
        return keys;
    }

    private static void ValidateNode(ConditionalRuleNode node, int depth, ref int count)
    {
        if (depth > MaximumDepth)
            throw new CustomFieldDomainException($"Conditional rules cannot exceed {MaximumDepth} levels.");
        if (++count > MaximumNodes)
            throw new CustomFieldDomainException($"Conditional rules cannot exceed {MaximumNodes} nodes.");

        switch (node)
        {
            case ConditionalGroupNode group:
                if (group.Children is null || group.Children.Count == 0)
                    throw new CustomFieldDomainException("Conditional groups require at least one child.");
                foreach (var child in group.Children)
                    ValidateNode(child ?? throw new CustomFieldDomainException("Rule children cannot be null."), depth + 1, ref count);
                break;
            case ConditionalComparisonNode comparison:
                CustomFieldGovernance.NormalizeAndValidateStableKey(comparison.FieldKey);
                var requiresOperand = comparison.Operator is not (
                    CustomFieldComparisonOperator.IsEmpty or CustomFieldComparisonOperator.IsNotEmpty);
                if (requiresOperand && comparison.Operand is null)
                    throw new CustomFieldDomainException($"Operator {comparison.Operator} requires an operand.");
                if (!requiresOperand && comparison.Operand is not null)
                    throw new CustomFieldDomainException($"Operator {comparison.Operator} does not accept an operand.");
                break;
            default:
                throw new CustomFieldDomainException("Unsupported conditional-rule node type.");
        }
    }

    private static void CollectKeys(ConditionalRuleNode node, ISet<string> keys)
    {
        if (node is ConditionalComparisonNode comparison)
            keys.Add(CustomFieldGovernance.NormalizeAndValidateStableKey(comparison.FieldKey));
        else if (node is ConditionalGroupNode group)
            foreach (var child in group.Children) CollectKeys(child, keys);
    }
}

public sealed class CustomFieldRule
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private CustomFieldRule() { }
    public long Id { get; private set; }
    public long VersionId { get; private set; }
    public CustomFieldVersion Version { get; private set; } = null!;
    public CustomFieldRuleEffect Effect { get; private set; }
    public string ConditionJson { get; private set; } = null!;

    public ConditionalRuleNode Condition =>
        JsonSerializer.Deserialize<ConditionalRuleNode>(ConditionJson, SerializerOptions)
        ?? throw new CustomFieldDomainException("Stored conditional rule is invalid.");

    internal static CustomFieldRule Create(
        CustomFieldVersion version, CustomFieldRuleEffect effect, ConditionalRuleNode condition) =>
        new()
        {
            Version = version,
            Effect = effect,
            ConditionJson = JsonSerializer.Serialize<ConditionalRuleNode>(condition, SerializerOptions)
        };
}

public static class CustomFieldDependencyGraph
{
    public static void EnsureAcyclic(IEnumerable<(long DefinitionId, long DependsOnDefinitionId)> dependencies)
    {
        var graph = dependencies
            .GroupBy(x => x.DefinitionId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.DependsOnDefinitionId).Distinct().ToArray());
        var visiting = new HashSet<long>();
        var visited = new HashSet<long>();
        var path = new Stack<long>();

        foreach (var node in graph.Keys)
            Visit(node, graph, visiting, visited, path);
    }

    private static void Visit(
        long node,
        IReadOnlyDictionary<long, long[]> graph,
        ISet<long> visiting,
        ISet<long> visited,
        Stack<long> path)
    {
        if (visited.Contains(node)) return;
        if (!visiting.Add(node))
        {
            var cycle = path.Reverse().SkipWhile(x => x != node).Append(node);
            throw new CustomFieldDomainException($"Custom-field dependency cycle detected: {string.Join(" -> ", cycle)}.");
        }

        path.Push(node);
        if (graph.TryGetValue(node, out var targets))
            foreach (var target in targets) Visit(target, graph, visiting, visited, path);
        path.Pop();
        visiting.Remove(node);
        visited.Add(node);
    }
}
