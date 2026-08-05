namespace ERP_RFQ_Automation.Security.DocumentInspection;

public enum FileInspectionStatus
{
    Cleared,
    Quarantined,
    Rejected
}
public enum MalwareScanStatus
{
    Clean,
    Infected,
    Unavailable,
    Error
}

public sealed class MalwareVerdictPolicyOptions
{
    public const string SectionName = "DocumentInspection:MalwareVerdictPolicy";
    public TimeSpan MaximumCleanVerdictAge { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// The machine codes intake attaches to a stopped document. They are the ONLY stable contract the
/// UI may branch on (Frontend/src/utils/intakeErrors.ts mirrors this list), so a code must name a
/// distinct CAUSE rather than a status.
///
/// <para>
/// <see cref="DocumentRejected"/> is deliberately a bucket of last resort: it means "inspection
/// said no" and nothing more, so any surface rendering it must show the accompanying
/// <see cref="FileInspectionResult.Reason"/> instead of guessing at the cause. Whenever a cause is
/// both common and separately actionable it gets its own code here — that is what
/// <see cref="MacroEnabledDocument"/> is: a macro-enabled workbook is not "damaged" and re-exporting
/// it as a PDF is not the remedy, so telling the user that was actively wrong.
/// </para>
/// </summary>
public static class DocumentInspectionErrorCodes
{
    /// <summary>Inspection reached a terminal verdict; the reason string carries the specifics.</summary>
    public const string DocumentRejected = "document_rejected";

    /// <summary>Held without a verdict. Replayable — see <c>SecurityHoldRecovery</c>.</summary>
    public const string DocumentQuarantined = "document_quarantined";

    /// <summary>
    /// The document carries macros (VBA). A real malware vector, always refused; the remedy is a
    /// macro-free re-save, never a re-export or a PDF.
    /// </summary>
    public const string MacroEnabledDocument = "macro_enabled_document";
}

public sealed record FileInspectionRequest(
    Stream Content,
    string FileName,
    string? DeclaredContentType = null,
    long? DeclaredLength = null,
    ReusableMalwareVerdict? ReusableMalwareVerdict = null);

public sealed record ReusableMalwareVerdict(
    string Engine,
    string? SignatureVersion,
    DateTimeOffset ScannedOn);

public sealed record FileInspectionResult(
    FileInspectionStatus Status,
    string? DetectedContentType,
    long InspectedLength,
    string Reason,
    string ScannerEngine,
    string? ScannerSignature)
{
    public bool IsCleared => Status == FileInspectionStatus.Cleared;
    public MalwareScanStatus? MalwareStatus { get; init; }
    public bool IsRetryable { get; init; }
    public string ErrorCode { get; init; } = Status == FileInspectionStatus.Rejected
        ? DocumentInspectionErrorCodes.DocumentRejected
        : DocumentInspectionErrorCodes.DocumentQuarantined;
    public bool MalwareVerdictReused { get; init; }

    /// <summary>
    /// Operator-only detail (scanner endpoint, exception text, remediation keys). NEVER returned to
    /// tenant users or persisted to tenant-visible metadata — <see cref="Reason"/> carries the
    /// user-safe wording; this belongs in logs and admin/ops surfaces only.
    /// </summary>
    public string? OperatorDiagnostics { get; init; }
}

public sealed record MalwareScanResult(
    MalwareScanStatus Status,
    string Engine,
    string? Signature,
    string Reason)
{
    /// <summary>
    /// Operator-only detail behind <see cref="Reason"/>: host:port, exception type/message and the
    /// configuration keys to change. Logged, never surfaced to tenant users.
    /// </summary>
    public string? Diagnostics { get; init; }

    public static MalwareScanResult Clean(string engine, string? signature = null) =>
        new(MalwareScanStatus.Clean, engine, signature, "No malware was detected.");

    public static MalwareScanResult Infected(string engine, string signature) =>
        new(MalwareScanStatus.Infected, engine, signature, "Malware was detected.");

    public static MalwareScanResult Unavailable(string engine, string reason, string? diagnostics = null) =>
        new(MalwareScanStatus.Unavailable, engine, null, reason) { Diagnostics = diagnostics };

    public static MalwareScanResult Error(string engine, string reason, string? diagnostics = null) =>
        new(MalwareScanStatus.Error, engine, null, reason) { Diagnostics = diagnostics };
}

public interface IFileInspectionService
{
    Task<FileInspectionResult> InspectAsync(
        FileInspectionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMalwareScanner
{
    Task<MalwareScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken = default);
}
