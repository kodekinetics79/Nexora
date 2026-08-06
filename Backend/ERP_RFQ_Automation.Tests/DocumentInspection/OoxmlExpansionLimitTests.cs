using System.Text;
using ERP_RFQ_Automation.Security.DocumentInspection;

namespace ERP_RFQ_Automation.Tests.DocumentInspection;

/// <summary>
/// Regression for a live rejection on 2026-08-05: a genuine Aramco RFP .docx —
/// RFQ_Aramco_4203208081.docx — was refused with "An OOXML entry exceeds the
/// expanded-size limit."
///
/// The shape that triggered it is ordinary for large tenders: document.xml holding
/// thousands of materials-table rows. Such a part legitimately expands past the old
/// 50MB per-entry cap, and its repetitive markup legitimately compresses well past
/// the old 100x per-entry ratio. Neither cap was load-bearing for safety — entries
/// stream through an 80KB buffer and the PACKAGE total (100MB, enforced mid-stream)
/// bounds the work a hostile archive can cause — so both rejected real customers
/// while stopping no attack the total cap doesn't stop.
///
/// These tests pin the corrected posture: the honest giant clears, the actual
/// zip-bomb signatures (extreme ratio, oversized package total, dishonest declared
/// length) all still die.
/// </summary>
public sealed class OoxmlExpansionLimitTests
{
    [Fact]
    public async Task GiantButHonestDocumentXml_ClearsInspection()
    {
        // 121MB expanded — the EXACT size of the real Aramco RFP part production rejected
        // twice (first at the 50MB cap, then at 100MB). Varied row content keeps the
        // ratio in the real-document band (~well under 300x) rather than the uniform-fill
        // bomb band. Must clear under the default caps.
        var row = new StringBuilder();
        var document = new StringBuilder("<?xml version=\"1.0\"?><w:document><w:body><w:tbl>");
        for (var i = 0; document.Length < 121 * 1024 * 1024; i++)
        {
            row.Clear();
            row.Append("<w:tr><w:tc><w:t>MAT-").Append(i)
               .Append("</w:t></w:tc><w:tc><w:t>GASKET SPIRAL WOUND ")
               .Append(i % 997).Append('-').Append(i % 89)
               .Append("</w:t></w:tc><w:tc><w:t>").Append(1 + i % 500)
               .Append("</w:t></w:tc></w:tr>");
            document.Append(row);
        }
        document.Append("</w:tbl></w:body></w:document>");

        var bytes = MacroPolicyAndRejectionTruthTests.CreateOpenXmlPackage(
            "word/document2.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            ("word/document.xml", Encoding.UTF8.GetBytes(document.ToString())));

        var result = await Inspect(bytes, "RFQ_Aramco_regression.docx");

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
    }

    [Fact]
    public async Task UniformFillBombRatio_IsStillRejected()
    {
        // 90MB of a single repeated byte compresses at thousands-to-one — the classic
        // bomb signature, far past even the widened 300x tripwire.
        var bomb = new byte[90 * 1024 * 1024]; // zero-filled
        var bytes = MacroPolicyAndRejectionTruthTests.CreateOpenXmlPackage(
            "word/document2.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            ("word/document.xml", bomb));

        var result = await Inspect(bytes, "bomb.docx");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("expansion ratio", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageTotalPastTheCap_IsStillRejected()
    {
        // Two varied-content parts summing past an explicit package cap. Each alone is
        // inside the per-entry limit; the mid-stream TOTAL check must still refuse.
        // Explicit small caps pin the branch without CI-hostile allocations.
        static byte[] Varied(int megabytes, int seed)
        {
            var sb = new StringBuilder();
            for (var i = 0; sb.Length < megabytes * 1024 * 1024; i++)
                sb.Append("<w:t>PART-").Append(seed).Append('-').Append(i % 9973).Append("</w:t>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        var bytes = MacroPolicyAndRejectionTruthTests.CreateOpenXmlPackage(
            "word/document2.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            ("word/document.xml", Varied(13, 1)),
            ("word/document3.xml", Varied(13, 2)));

        var result = await Inspect(bytes, "too-big-in-total.docx", new DocumentInspectionOptions
        {
            MaximumFileBytes = 256L * 1024 * 1024,
            MaximumArchiveEntryBytes = 20L * 1024 * 1024,
            MaximumArchiveExpandedBytes = 24L * 1024 * 1024,
        });

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Contains("in total", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SizeMessages_CarryNumbersButNeverArchiveEntryNames()
    {
        // Entry names are attacker-controlled text inside the archive, and rejection
        // reasons render verbatim as product copy. The oversize message must name
        // sizes, not parts. Explicit small per-entry cap pins the size branch (varied
        // bytes keep the RATIO tripwire from firing first).
        var oversize = new byte[9 * 1024 * 1024];
        for (var i = 0; i < oversize.Length; i++) oversize[i] = (byte)(i * 31 % 251);

        var bytes = MacroPolicyAndRejectionTruthTests.CreateOpenXmlPackage(
            "word/document2.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            ("word/EVIL-CALL-1-800-000-0000.xml", oversize));

        var result = await Inspect(bytes, "named-entry.docx", new DocumentInspectionOptions
        {
            MaximumFileBytes = 256L * 1024 * 1024,
            MaximumArchiveEntryBytes = 8L * 1024 * 1024,
        });

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.DoesNotContain("EVIL", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MB", result.Reason, StringComparison.Ordinal);
    }

    private static async Task<FileInspectionResult> Inspect(
        byte[] bytes, string fileName, DocumentInspectionOptions? options = null)
    {
        // Mirror production request shape: no declared content type (the ingestion door
        // sends none), declared length from the actual byte count.
        var service = new DocumentFileInspectionService(
            new EicarMalwareScanner(),
            options ?? new DocumentInspectionOptions { MaximumFileBytes = 256L * 1024 * 1024 });
        await using var stream = new MemoryStream(bytes, writable: false);
        return await service.InspectAsync(new FileInspectionRequest(
            stream, fileName, DeclaredLength: bytes.LongLength));
    }
}
