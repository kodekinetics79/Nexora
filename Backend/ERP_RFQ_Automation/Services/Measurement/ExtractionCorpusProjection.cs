using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Services.Measurement;

/// <summary>One (scope, field) cell of a single reviewed document.</summary>
/// <param name="Observed">Positions where a value existed to be judged.</param>
/// <param name="Corrected">Positions the reviewer changed.</param>
public sealed record CorpusObservation(string Scope, string FieldName, int Observed, int Corrected)
{
    public bool Correct => Corrected == 0;
}

/// <summary>
/// Turns a review's before/after images into countable observations.
///
/// This is the ONLY place the correction signal is interpreted, and it is a pure
/// function of two JSON strings so it serves both callers identically:
///   * the write path, inside <c>SubmitLeadReviewAsync</c>'s transaction, with the exact
///     strings that are about to be persisted on the audit row; and
///   * the read path, over <see cref="LeadReviewAudit"/> rows written before the corpus
///     table existed — the historical signal the product has been discarding.
/// Both therefore produce identical numbers by construction, not by convention.
///
/// EVIDENCE RULE (the part that decides whether the resulting number is honest):
/// a position is OBSERVED only when the machine produced a value, or the reviewer
/// supplied one, or both. null → null is not a correct prediction; it is the absence of
/// evidence, and it is excluded from the denominator. Counting those as wins would let a
/// field the documents never carry report near-perfect accuracy.
///
/// Comparison is ordinal on the JSON text of each value, after trimming strings and
/// treating whitespace-only as absent. Numeric values are compared by their parsed
/// value, so 5 and 5.0 are the same reading, not a correction.
/// </summary>
public static class ExtractionCorpusProjection
{
    /// <summary>Line inventory: did the machine find the right SET of lines.</summary>
    public const string LineInventoryField = "lineItems";

    /// <summary>
    /// Lead-level fields carried on the review snapshot. Deliberately excludes
    /// HeaderRemarks (a pipeline-written marker, not an extracted commercial fact) and
    /// the review-governance columns.
    /// </summary>
    public static readonly IReadOnlyList<string> HeaderFields = new[]
    {
        "rfqno", "buyersName", "bidClosingDate", "opportunityNo"
    };

    /// <summary>Line-item fields carried on the review snapshot, in schema order.</summary>
    public static readonly IReadOnlyList<string> LineFields = new[]
    {
        "lineItemNo", "productShortName", "productShortDescription", "commodityProduct",
        "itemMaterialCode", "currency", "unitOfMeasure", "unitPrice", "quantity",
        "manufacturerName", "manufacturerPartNumber", "alternateProductName",
        "alternatePartNumber", "itemText", "leadTime", "extraFields"
    };

    /// <summary>
    /// Projects one review into its observation cells. Returns an empty list when either
    /// image is unreadable — a malformed audit yields no evidence rather than bad evidence.
    /// </summary>
    public static IReadOnlyList<CorpusObservation> Diff(string? beforeJson, string? afterJson)
    {
        if (string.IsNullOrWhiteSpace(beforeJson) || string.IsNullOrWhiteSpace(afterJson))
            return Array.Empty<CorpusObservation>();

        JsonElement before, after;
        JsonDocument? beforeDoc = null, afterDoc = null;
        try
        {
            beforeDoc = JsonDocument.Parse(beforeJson);
            afterDoc = JsonDocument.Parse(afterJson);
            before = beforeDoc.RootElement;
            after = afterDoc.RootElement;
            if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object)
                return Array.Empty<CorpusObservation>();

            var observations = new List<CorpusObservation>(HeaderFields.Count + LineFields.Count + 1);

            foreach (var field in HeaderFields)
            {
                var machine = Read(before, field);
                var human = Read(after, field);
                if (machine is null && human is null) continue; // no evidence either way
                observations.Add(new CorpusObservation(
                    ExtractionCorpusScopes.Header, field, 1,
                    string.Equals(machine, human, StringComparison.Ordinal) ? 0 : 1));
            }

            var machineLines = ReadItems(before);
            var humanLines = ReadItems(after);

            // Line inventory. Lines the reviewer added are lines the machine MISSED;
            // lines the reviewer deleted are lines the machine INVENTED. Both are
            // extraction errors and neither is visible in a per-field comparison.
            var machineIds = machineLines.Keys.ToHashSet();
            var humanIds = humanLines.Keys.ToHashSet();
            var missed = humanIds.Count(id => !machineIds.Contains(id));
            var invented = machineIds.Count(id => !humanIds.Contains(id));
            if (machineIds.Count > 0 || humanIds.Count > 0)
                observations.Add(new CorpusObservation(
                    ExtractionCorpusScopes.Document, LineInventoryField,
                    Math.Max(machineIds.Count, humanIds.Count), missed + invented));

            // Per-field line accuracy is scored only over lines that survived review.
            // A deleted line's field values are not wrong readings of a real line; they
            // are a line that should not exist, already counted as inventory error above.
            var survivors = machineIds.Where(humanIds.Contains).OrderBy(id => id).ToArray();
            if (survivors.Length > 0)
            {
                foreach (var field in LineFields)
                {
                    var observed = 0;
                    var corrected = 0;
                    foreach (var id in survivors)
                    {
                        var machine = Read(machineLines[id], field);
                        var human = Read(humanLines[id], field);
                        if (machine is null && human is null) continue;
                        observed++;
                        if (!string.Equals(machine, human, StringComparison.Ordinal)) corrected++;
                    }
                    if (observed > 0)
                        observations.Add(new CorpusObservation(
                            ExtractionCorpusScopes.Line, field, observed, corrected));
                }
            }

            return observations;
        }
        catch (JsonException)
        {
            return Array.Empty<CorpusObservation>();
        }
        finally
        {
            beforeDoc?.Dispose();
            afterDoc?.Dispose();
        }
    }

    /// <summary>Items keyed by database id; unkeyed or zero-id entries are ignored.</summary>
    private static Dictionary<long, JsonElement> ReadItems(JsonElement snapshot)
    {
        var items = new Dictionary<long, JsonElement>();
        if (!snapshot.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
            return items;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            if (!id.TryGetInt64(out var value) || value <= 0) continue;
            items[value] = item;
        }
        return items;
    }

    /// <summary>
    /// Canonical text of a field, or null when the field is absent/blank. Numbers are
    /// normalised so 5 and 5.0 compare equal; everything else compares as trimmed text.
    /// </summary>
    private static string? Read(JsonElement owner, string field)
    {
        if (owner.ValueKind != JsonValueKind.Object) return null;
        if (!owner.TryGetProperty(field, out var value)) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            case JsonValueKind.Number:
                return value.TryGetDecimal(out var number)
                    ? number.ToString("0.############################",
                        System.Globalization.CultureInfo.InvariantCulture)
                    : value.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                var raw = value.GetRawText();
                return raw is "{}" or "[]" ? null : raw;
        }
    }
}
