using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ERP_RFQ_Automation.Models;

// LeadItem.cs is database-scaffolded; custom members live in this partial so a
// re-scaffold never wipes them (same pattern as ErpRfqAutomationContext.Tenancy.cs).
public partial class LeadItem
{
    /// <summary>
    /// True only for the Lead's current mutable projection. Superseded rows remain retained so
    /// immutable LeadItemRevision records never lose their exact canonical-line foreign key.
    /// </summary>
    public bool IsCurrentRevisionProjection { get; set; } = true;

    /// <summary>
    /// The immutable canonical LeadItem whose extraction evidence supports this projection.
    /// Null means this row is itself the evidence-bearing item. Human corrections clone a row
    /// rather than mutating it and carry this pointer forward, so the new revision keeps exact
    /// source lineage without rebinding append-only FieldEvidence.
    /// </summary>
    public long? EvidenceSourceLeadItemId { get; set; }

    /// <summary>
    /// Verbatim capture of unrecognized customer-document columns for this line item,
    /// stored as a jsonb object of {"original column header": "cell value"}. Null when
    /// the source document had no unmapped columns. Column type is configured in
    /// ErpRfqAutomationContext.Tenancy.OnModelCreatingPartial; use
    /// <see cref="ExtraFieldsJson"/> to read/write it safely.
    /// </summary>
    public string? ExtraFields { get; set; }
}

/// <summary>
/// Single place for LeadItem.ExtraFields hygiene: bounded, empty-safe JSON
/// serialization (write path) and tolerant deserialization (read path).
/// </summary>
public static class ExtraFieldsJson
{
    /// <summary>Defensive caps: at most this many captured columns per item…</summary>
    public const int MaxKeys = 20;

    /// <summary>
    /// …at most this many chars of serialized JSON per item (~16 KB)…
    ///
    /// <para>It was 2 KB, and the rule dropped entries from the end until the rest fitted — or
    /// returned null when the FIRST entry alone was too long. An Aramco motor line naming 54 to 63
    /// approved makers put 15–18 KB of buyer text in its first entry, so the line arrived with no
    /// approved makers, no part numbers and no Hazardous / SASO flags at all: just a description.
    /// The column is jsonb; the cap is a guard against a runaway document, not a budget.</para>
    /// </summary>
    public const int MaxSerializedChars = 16_384;

    /// <summary>…and at most this many chars in any one value, cut visibly rather than dropped.</summary>
    public const int MaxValueChars = 8_000;

    /// <summary>What a shortened value ends with, so a reader can tell it was cut.</summary>
    public const string CutMarker = " …";

    /// <summary>A value is never shortened below this many chars to make room; entries are dropped instead.</summary>
    private const int MinShortenedValueChars = 500;

    /// <summary>
    /// Sanitizes and serializes captured columns to a JSON object string, or null when
    /// nothing usable remains. Empty/whitespace keys and values are dropped, keys are
    /// capped at <see cref="MaxKeys"/> and each value at <see cref="MaxValueChars"/>. When the
    /// payload is still over <see cref="MaxSerializedChars"/>, the LONGEST value is shortened
    /// first (visibly, with <see cref="CutMarker"/>), so a short flag such as "Hazardous
    /// Indicator: Yes" is never lost to make room for a long list; only then are entries dropped
    /// from the end. A usable first entry always survives.
    /// </summary>
    public static string? Serialize(IReadOnlyDictionary<string, string>? extraFields)
    {
        if (extraFields is null || extraFields.Count == 0) return null;

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in extraFields)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            kept[key.Trim()] = Shorten(value.Trim(), MaxValueChars);
            if (kept.Count >= MaxKeys) break;
        }
        if (kept.Count == 0) return null;

        var json = JsonSerializer.Serialize(kept);
        while (json.Length > MaxSerializedChars)
        {
            var longest = kept.OrderByDescending(pair => pair.Value.Length).First();
            var floor = kept.Count == 1 ? CutMarker.Length + 1 : MinShortenedValueChars;
            if (longest.Value.Length > floor)
            {
                // Escaping can make the JSON longer than the text, so aim a little lower than
                // the plain excess; the loop re-measures and goes again if needed.
                var excess = json.Length - MaxSerializedChars;
                kept[longest.Key] = Shorten(longest.Value,
                    Math.Max(floor, longest.Value.Length - excess - CutMarker.Length - 16));
            }
            else if (kept.Count > 1)
            {
                kept.Remove(LastKey(kept));
            }
            else
            {
                break;
            }
            json = JsonSerializer.Serialize(kept);
        }
        return json.Length <= MaxSerializedChars ? json : null;
    }

    private static string Shorten(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..Math.Max(0, maxChars - CutMarker.Length)].TrimEnd() + CutMarker;

    /// <summary>
    /// Parses a stored jsonb payload back into a dictionary. Never throws: malformed or
    /// non-object payloads (which we never write, but the column is open) yield null.
    /// </summary>
    public static Dictionary<string, string>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is { Count: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string LastKey(Dictionary<string, string> dict)
    {
        string last = "";
        foreach (var key in dict.Keys) last = key;
        return last;
    }
}
