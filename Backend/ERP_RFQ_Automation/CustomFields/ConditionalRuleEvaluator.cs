using System.Globalization;
using System.Text.Json;

namespace ERP_RFQ_Automation.CustomFields;

public sealed record CustomFieldRuleState(bool IsVisible, bool IsRequired, bool IsReadOnly);

public static class ConditionalRuleEvaluator
{
    public static CustomFieldRuleState Evaluate(
        CustomFieldVersion version, IReadOnlyDictionary<string, CustomFieldValueInput> values)
    {
        var evaluated = version.Rules.Select(rule => (rule.Effect, Matches(rule.Condition, values))).ToArray();
        var visibleRules = evaluated.Where(x => x.Effect == CustomFieldRuleEffect.Visible).ToArray();
        var isVisible = (visibleRules.Length == 0 || visibleRules.Any(x => x.Item2)) &&
                        !evaluated.Any(x => x.Effect == CustomFieldRuleEffect.Hidden && x.Item2);
        return new CustomFieldRuleState(
            isVisible,
            version.IsRequired || evaluated.Any(x => x.Effect == CustomFieldRuleEffect.Required && x.Item2),
            evaluated.Any(x => x.Effect == CustomFieldRuleEffect.ReadOnly && x.Item2));
    }

    private static bool Matches(
        ConditionalRuleNode node, IReadOnlyDictionary<string, CustomFieldValueInput> values) => node switch
    {
        ConditionalGroupNode group when group.Operator == ConditionalGroupOperator.All =>
            group.Children.All(child => Matches(child, values)),
        ConditionalGroupNode group => group.Children.Any(child => Matches(child, values)),
        ConditionalComparisonNode comparison => MatchesComparison(comparison, values),
        _ => false
    };

    private static bool MatchesComparison(
        ConditionalComparisonNode comparison, IReadOnlyDictionary<string, CustomFieldValueInput> values)
    {
        values.TryGetValue(comparison.FieldKey, out var input);
        var actual = Scalar(input);
        if (comparison.Operator == CustomFieldComparisonOperator.IsEmpty) return actual == null;
        if (comparison.Operator == CustomFieldComparisonOperator.IsNotEmpty) return actual != null;
        if (comparison.Operand is null || actual == null) return false;
        var operand = comparison.Operand.Value;

        return comparison.Operator switch
        {
            CustomFieldComparisonOperator.Equal => EqualsValue(actual, operand),
            CustomFieldComparisonOperator.NotEqual => !EqualsValue(actual, operand),
            CustomFieldComparisonOperator.Contains => Contains(actual, operand),
            CustomFieldComparisonOperator.In => operand.ValueKind == JsonValueKind.Array &&
                                                operand.EnumerateArray().Any(item => EqualsValue(actual, item)),
            CustomFieldComparisonOperator.GreaterThan => Compare(actual, operand) > 0,
            CustomFieldComparisonOperator.GreaterThanOrEqual => Compare(actual, operand) >= 0,
            CustomFieldComparisonOperator.LessThan => Compare(actual, operand) < 0,
            CustomFieldComparisonOperator.LessThanOrEqual => Compare(actual, operand) <= 0,
            _ => false
        };
    }

    private static object? Scalar(CustomFieldValueInput? input)
    {
        if (input == null) return null;
        if (input.Text != null) return input.Text;
        if (input.Integer.HasValue) return input.Integer.Value;
        if (input.Decimal.HasValue) return input.Decimal.Value;
        if (input.Boolean.HasValue) return input.Boolean.Value;
        if (input.Date.HasValue) return input.Date.Value;
        if (input.Timestamp.HasValue) return input.Timestamp.Value;
        if (input.Json != null) return input.Json;
        return input.ReferenceId;
    }

    private static bool EqualsValue(object actual, JsonElement operand)
    {
        if (TryDecimal(actual, out var actualNumber) && operand.TryGetDecimal(out var operandNumber))
            return actualNumber == operandNumber;
        if (actual is bool boolean && operand.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return boolean == operand.GetBoolean();
        return string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture), ElementText(operand),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(object actual, JsonElement operand)
    {
        var expected = ElementText(operand);
        if (actual is string text)
        {
            if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    return JsonSerializer.Deserialize<string[]>(text)?.Contains(
                        expected, StringComparer.OrdinalIgnoreCase) == true;
                }
                catch (JsonException) { }
            }
            return text.Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static int Compare(object actual, JsonElement operand)
    {
        if (TryDecimal(actual, out var actualNumber) && operand.TryGetDecimal(out var operandNumber))
            return actualNumber.CompareTo(operandNumber);
        if (DateTime.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var actualDate) &&
            DateTime.TryParse(ElementText(operand), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var operandDate))
            return actualDate.CompareTo(operandDate);
        return string.Compare(Convert.ToString(actual, CultureInfo.InvariantCulture), ElementText(operand),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDecimal(object value, out decimal result) =>
        decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number,
            CultureInfo.InvariantCulture, out result);

    private static string ElementText(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? element.GetString() ?? string.Empty
        : element.GetRawText();
}
