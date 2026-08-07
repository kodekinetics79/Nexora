using System.Text.Json;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// Decodes the free-form JSON that privileged endpoints already write into
/// <c>PlatformAuditLog.Metadata</c>, so "what changed?" can be answered without every reader
/// re-learning each endpoint's private shape.
///
/// <para><b>Why decoding rather than a schema.</b> The metadata column predates this reader and is
/// written by a dozen endpoints in at least three shapes: <c>{before, after}</c> (plan.update,
/// tenant.ai-policy.update), <c>{from, to, reason}</c> (every tenant status transition), and
/// <c>{fromPlanId, toPlanId, ...}</c> (tenant.plan.change). Imposing a schema now would mean
/// rewriting those endpoints and leaving every historical row undecodable — the rows an
/// investigation actually reaches for. Reading the shapes that exist keeps history legible.</para>
///
/// <para><b>Nothing is invented.</b> When a row carries no recognisable change pair the result is
/// an EMPTY change list and the raw metadata, not a guess. An audit explorer that fabricates a
/// plausible diff is worse than one that says it does not know.</para>
/// </summary>
public static class PlatformAuditMetadata
{
    /// <summary>
    /// Parses <paramref name="metadata"/> defensively. The column is <c>jsonb</c> in PostgreSQL but
    /// plain text on the portable providers, and rows written before that distinction was enforced
    /// are not guaranteed to be JSON at all — a parse failure must degrade to "no structure here",
    /// never to a 500 on the one endpoint an operator opened to investigate a problem.
    /// </summary>
    public static JsonElement? TryParse(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;
        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>before</c> object of a <c>{before, after}</c> record, when present.</summary>
    public static JsonElement? Before(JsonElement? metadata) => Property(metadata, "before");

    /// <summary>The <c>after</c> object of a <c>{before, after}</c> record, when present.</summary>
    public static JsonElement? After(JsonElement? metadata) => Property(metadata, "after");

    /// <summary>
    /// Field-level changes for one audit row, in whichever of the three recorded shapes the row
    /// happens to use. Ordered by field name so two renderings of the same row are identical.
    /// </summary>
    public static IReadOnlyList<PlatformAuditFieldChangeDto> Changes(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } root)
            return [];

        var changes = new List<PlatformAuditFieldChangeDto>();

        // Shape 1: {before: {...}, after: {...}} — the richest, used by every endpoint that
        // snapshots an entity around a mutation.
        var before = Before(root);
        var after = After(root);
        if (before is { ValueKind: JsonValueKind.Object } beforeObject
            && after is { ValueKind: JsonValueKind.Object } afterObject)
        {
            var fields = beforeObject.EnumerateObject().Select(p => p.Name)
                .Concat(afterObject.EnumerateObject().Select(p => p.Name))
                .Distinct(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var beforeValue = beforeObject.TryGetProperty(field, out var b) ? Render(b) : null;
                var afterValue = afterObject.TryGetProperty(field, out var a) ? Render(a) : null;
                if (!string.Equals(beforeValue, afterValue, StringComparison.Ordinal))
                    changes.Add(new PlatformAuditFieldChangeDto
                    {
                        Field = field, Before = beforeValue, After = afterValue
                    });
            }
        }

        // Shape 2 and 3 together: any fromX / toX pair, including the bare from/to that the tenant
        // lifecycle writes. Matching on the prefix rather than enumerating known verbs means an
        // endpoint added next month gets a readable diff without touching this file.
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.StartsWith("from", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = property.Name[4..];
            var counterpart = root.EnumerateObject()
                .FirstOrDefault(p => p.Name.Length == suffix.Length + 2
                                     && p.Name.StartsWith("to", StringComparison.OrdinalIgnoreCase)
                                     && p.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (counterpart.Value.ValueKind == JsonValueKind.Undefined)
                continue;

            // A bare from/to has no field name of its own. "state" is the honest label: the tenant
            // lifecycle is the only writer of that shape and that is precisely what it moves.
            var field = suffix.Length == 0 ? "state" : char.ToLowerInvariant(suffix[0]) + suffix[1..];
            if (changes.Any(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase)))
                continue;
            changes.Add(new PlatformAuditFieldChangeDto
            {
                Field = field, Before = Render(property.Value), After = Render(counterpart.Value)
            });
        }

        return changes.OrderBy(c => c.Field, StringComparer.Ordinal).ToList();
    }

    private static JsonElement? Property(JsonElement? metadata, string name)
        => metadata is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;

    /// <summary>
    /// A single-line rendering for comparison and display. Nested objects and arrays keep their raw
    /// JSON: truncating them would make two different values compare equal and hide a change.
    /// </summary>
    private static string? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };
}
