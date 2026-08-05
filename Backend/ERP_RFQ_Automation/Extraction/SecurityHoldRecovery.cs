using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// The single definition of "this intake occurrence is held by a security scan that never produced
/// a verdict, so it can be replayed from the immutable source without a re-upload".
///
/// Both the reconciliation read model (which decides whether the UI offers "Retry blocked files")
/// and <see cref="SecurityScanRecoveryService"/> (which performs the replay) must agree. When they
/// disagreed, the UI counted files as recoverable while the recovery call reported
/// <c>Eligible: 0</c> and silently did nothing.
/// </summary>
public static class SecurityHoldRecovery
{
    /// <summary>Intake error codes that indicate a scanner-side hold rather than a malware verdict.</summary>
    public static readonly IReadOnlyList<string> RecoverableErrorCodes =
    [
        "document_quarantined",
        "security_scanner_unavailable"
    ];

    public static bool IsRecoverableSecurityHold(
        IntakeOccurrenceStatus intakeStatus,
        string? lastErrorCode,
        string? sourceMetadataJson)
    {
        if (intakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan)
        {
            return true;
        }

        if (intakeStatus != IntakeOccurrenceStatus.Rejected)
        {
            return false;
        }

        if (lastErrorCode is null ||
            !RecoverableErrorCodes.Contains(lastErrorCode, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // No recorded inspection verdict at all — the hold came from a scanner that never answered.
        // Note the ingest gateway writes the occurrence's source metadata with `inspection: null`,
        // so this is the NORMAL shape for a scanner-outage hold, not an edge case.
        if (string.IsNullOrWhiteSpace(sourceMetadataJson))
        {
            return true;
        }

        try
        {
            using var metadata = JsonDocument.Parse(sourceMetadataJson);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object
                || !metadata.RootElement.TryGetProperty("inspection", out var inspection)
                || inspection.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            // A recorded signature means a real engine reached a verdict; that is never replayable.
            return !inspection.TryGetProperty("ScannerSignature", out var signature)
                   || signature.ValueKind == JsonValueKind.Null
                   || string.IsNullOrWhiteSpace(signature.GetString());
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}
