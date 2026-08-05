using ERP_RFQ_Automation.Security.DocumentInspection;
using Xunit;
using Xunit.Abstractions;
namespace ERP_RFQ_Automation.Tests;
public class XlsLiveRepro
{
    private readonly ITestOutputHelper _o;
    public XlsLiveRepro(ITestOutputHelper o) => _o = o;
    [Fact]
    public async Task Real_xls_through_full_inspection()
    {
        // Machine-local live repro: exercises full inspection against a REAL customer .xls
        // that only exists on the workstation where the incident was debugged. On any other
        // machine (CI, a fresh clone) the file is absent and the test degrades to a no-op
        // rather than a hard failure — it is a repro harness, not a regression gate; the
        // synthetic fixtures in MacroPolicyAndRejectionTruthTests cover the gate.
        const string path = "/tmp/real-rfq.xls";
        if (!File.Exists(path)) return;

        var bytes = await File.ReadAllBytesAsync(path);
        var svc = new DocumentFileInspectionService(new EicarMalwareScanner(), new DocumentInspectionOptions());
        await using var ms = new MemoryStream(bytes);
        var result = await svc.InspectAsync(new FileInspectionRequest(ms, "real-rfq.xls", "application/vnd.ms-excel", bytes.Length));
        _o.WriteLine($"Status={result.Status} Reason={result.Reason} ErrorCode={result.ErrorCode} Detected={result.DetectedContentType}");
        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
    }
}
