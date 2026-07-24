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

public sealed record FileInspectionRequest(
    Stream Content,
    string FileName,
    string? DeclaredContentType = null,
    long? DeclaredLength = null);

public sealed record FileInspectionResult(
    FileInspectionStatus Status,
    string? DetectedContentType,
    long InspectedLength,
    string Reason,
    string ScannerEngine,
    string? ScannerSignature)
{
    public bool IsCleared => Status == FileInspectionStatus.Cleared;
}

public sealed record MalwareScanResult(
    MalwareScanStatus Status,
    string Engine,
    string? Signature,
    string Reason)
{
    public static MalwareScanResult Clean(string engine, string? signature = null) =>
        new(MalwareScanStatus.Clean, engine, signature, "No malware was detected.");

    public static MalwareScanResult Infected(string engine, string signature) =>
        new(MalwareScanStatus.Infected, engine, signature, "Malware was detected.");

    public static MalwareScanResult Unavailable(string engine, string reason) =>
        new(MalwareScanStatus.Unavailable, engine, null, reason);

    public static MalwareScanResult Error(string engine, string reason) =>
        new(MalwareScanStatus.Error, engine, null, reason);
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
