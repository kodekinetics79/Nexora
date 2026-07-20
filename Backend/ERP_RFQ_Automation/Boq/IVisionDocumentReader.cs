namespace ERP_RFQ_Automation.Boq;

// ─── Vision-ready seam (WP-BOQ) ──────────────────────────────────────────────
//
// SLDs and other engineering drawings can't be read by the text-only LLM. The
// BOQ draft flow detects drawing files (extension / mime) and asks this reader
// for a textual scope description first; when no vision model is configured it
// answers honestly and the flow falls back to a TBD-heavy skeleton BOQ prepared
// for manual takeoff. How an AnthropicVisionReader plugs in later is documented
// in Boq/BOQ-WIRING.md — nothing else in the flow changes.

/// <summary>What the draft flow knows about the file it wants read.</summary>
public sealed record VisionReadRequest(
    string FileName,
    string? MimeType,
    /// <summary>Absolute path on disk when the file is available locally; null otherwise.</summary>
    string? FilePath);

/// <summary>Outcome of a vision read. Never throws for "not configured" — that is a normal, reportable state.</summary>
public sealed record VisionReadResult(
    bool Success,
    /// <summary>A textual scope description extracted from the drawing (feeds the normal LLM draft path).</summary>
    string? ExtractedText,
    /// <summary>Plain-language reason when Success is false.</summary>
    string? FailureReason)
{
    public static VisionReadResult Ok(string text) => new(true, text, null);
    public static VisionReadResult Fail(string reason) => new(false, null, reason);
}

/// <summary>
/// Reads engineering drawings (SLDs, layouts, P&amp;IDs) into a textual scope
/// description a text LLM can turn into a BOQ draft.
/// </summary>
public interface IVisionDocumentReader
{
    /// <summary>False for the placeholder implementation — lets callers branch to the honest fallback without a call.</summary>
    bool IsConfigured { get; }

    Task<VisionReadResult> ReadAsync(VisionReadRequest request, CancellationToken ct);
}

/// <summary>
/// Default placeholder: always reports "vision model not configured". Registered
/// by AddBoqEngine with TryAddScoped, so registering a real reader BEFORE the
/// extension call swaps it in with zero other changes (see BOQ-WIRING.md).
/// </summary>
public sealed class NotConfiguredVisionReader : IVisionDocumentReader
{
    public const string Reason =
        "Drawing detected — connect a vision-capable AI model to auto-read diagrams; " +
        "sections prepared for manual takeoff.";

    public bool IsConfigured => false;

    public Task<VisionReadResult> ReadAsync(VisionReadRequest request, CancellationToken ct) =>
        Task.FromResult(VisionReadResult.Fail(Reason));
}
