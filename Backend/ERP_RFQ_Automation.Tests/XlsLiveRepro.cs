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
        var bytes = await File.ReadAllBytesAsync("/tmp/real-rfq.xls");
        var svc = new DocumentFileInspectionService(new EicarMalwareScanner(), new DocumentInspectionOptions());
        await using var ms = new MemoryStream(bytes);
        var result = await svc.InspectAsync(new FileInspectionRequest(ms, "real-rfq.xls", "application/vnd.ms-excel", bytes.Length));
        _o.WriteLine($"Status={result.Status} Reason={result.Reason} ErrorCode={result.ErrorCode} Detected={result.DetectedContentType}");
        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
    }
}
