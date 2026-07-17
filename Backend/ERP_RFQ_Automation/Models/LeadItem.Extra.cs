using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ERP_RFQ_Automation.Models;

// LeadItem.cs is database-scaffolded; custom members live in this partial so a
// re-scaffold never wipes them (same pattern as ErpRfqAutomationContext.Tenancy.cs).
public partial class LeadItem
{
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

    /// <summary>…and at most this many chars of serialized JSON per item (~2 KB).</summary>
    public const int MaxSerializedChars = 2048;

    /// <summary>
    /// Sanitizes and serializes captured columns to a JSON object string, or null when
    /// nothing usable remains. Empty/whitespace keys and values are dropped, keys are
    /// capped at <see cref="MaxKeys"/>, and entries are dropped from the end until the
    /// payload fits <see cref="MaxSerializedChars"/>.
    /// </summary>
    public static string? Serialize(IReadOnlyDictionary<string, string>? extraFields)
    {
        if (extraFields is null || extraFields.Count == 0) return null;

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in extraFields)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            kept[key.Trim()] = value.Trim();
            if (kept.Count >= MaxKeys) break;
        }
        if (kept.Count == 0) return null;

        var json = JsonSerializer.Serialize(kept);
        while (json.Length > MaxSerializedChars && kept.Count > 1)
        {
            // Drop the most recently added entry until the payload fits.
            var lastKey = LastKey(kept);
            kept.Remove(lastKey);
            json = JsonSerializer.Serialize(kept);
        }
        return json.Length <= MaxSerializedChars ? json : null;
    }

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
