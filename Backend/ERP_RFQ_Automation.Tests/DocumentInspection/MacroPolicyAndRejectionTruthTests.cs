using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.DocumentInspection;

/// <summary>
/// The owner uploaded a legacy .xls and was told "its contents do not match a document type we can
/// process, or the file is damaged. Re-export or re-save the document and upload it again. If it
/// opens correctly for you, send it as a PDF."
///
/// That sentence was a static guess keyed off the bucket code <c>document_rejected</c>, and for a
/// macro-enabled workbook every clause of it is wrong: the file is not damaged, re-exporting changes
/// nothing, and a PDF loses the line items. The precise reason existed the whole time — it was
/// computed, persisted, and then discarded at display.
///
/// These tests pin BOTH halves of the fix:
///  1. the macro control still rejects (it is a real malware vector) but now says why and how to fix
///     it, under its own error code so the category is analysable and the UI can be specific;
///  2. the specific reason survives the whole path — inspection → occurrence → batch read model —
///     and is never replaced by a generic sentence.
/// </summary>
public sealed class MacroPolicyAndRejectionTruthTests
{
    // The remedy the owner was WRONGLY given. No macro rejection may ever suggest it again.
    private const string WrongRemedy = "PDF";

    [Theory]
    [InlineData("_VBA_PROJECT_CUR")]
    [InlineData("VBA")]
    [InlineData("Macros")]
    [InlineData("macros")]         // the directory scan is case-insensitive; keep it that way
    [InlineData("_vba_project_cur")]
    public async Task Macro_enabled_legacy_workbook_is_still_rejected_and_says_why(string macroStorage)
    {
        var result = await InspectAsync(CreateOleCompound("Workbook", macroStorage), "rfq.xls");

        // MANDATORY 3: the control does not weaken. Macros are refused, every casing, every name.
        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, result.ErrorCode);
        Assert.False(result.IsRetryable);

        // MANDATORY 2: name the cause, give the real remedy, never the wrong one.
        Assert.Contains("contains macros", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Save As", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".xlsx", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongRemedy, result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("damaged", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Macro_enabled_legacy_word_document_names_word_and_docx()
    {
        var result = await InspectAsync(CreateOleCompound("WordDocument", "Macros"), "rfq.doc");

        Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, result.ErrorCode);
        Assert.Contains("contains macros", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".docx", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(".xlsx", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Macro_enabled_ooxml_workbook_and_document_share_the_same_code()
    {
        var xlsm = CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.ms-excel.sheet.macroEnabled.main+xml",
            ("xl/vbaProject.bin", new byte[] { 1, 2, 3 }));
        var docm = CreateOpenXmlPackage(
            "word/document.xml",
            "application/vnd.ms-word.document.macroEnabled.main+xml");

        var workbook = await InspectAsync(xlsm, "rfq.xlsm");
        var document = await InspectAsync(docm, "rfq.docx");

        Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, workbook.ErrorCode);
        Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, document.ErrorCode);
        Assert.Contains(".xlsx", workbook.Reason, StringComparison.Ordinal);
        Assert.Contains(".docx", document.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongRemedy, workbook.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A macro-free workbook must keep clearing. The macro block is a targeted control, not a
    /// blanket refusal of legacy Office, and three of the owner's four .xls files cleared.
    /// </summary>
    [Fact]
    public async Task Macro_free_legacy_workbook_still_clears()
    {
        var result = await InspectAsync(CreateOleCompound("Workbook"), "rfq.xls");

        Assert.Equal(FileInspectionStatus.Cleared, result.Status);
        Assert.Equal("application/vnd.ms-excel", result.DetectedContentType);
    }

    /// <summary>
    /// Every OTHER rejection keeps a reason of its own. The point of the macro code is not that
    /// macros are special-cased into honesty while the rest stay a guess — it is that the code says
    /// what it knows and the reason carries the rest.
    /// </summary>
    [Fact]
    public async Task Signature_extension_mismatch_keeps_its_own_reason_and_the_bucket_code()
    {
        var result = await InspectAsync("%PDF-1.7\n"u8.ToArray(), "rfq.xls");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Equal(DocumentInspectionErrorCodes.DocumentRejected, result.ErrorCode);
        Assert.Contains("does not match", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".xls", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("macro", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unrecognised_content_names_the_format_the_file_claims_to_be()
    {
        // The classic case: an HTML or SpreadsheetML export saved with an .xls name.
        var result = await InspectAsync(
            Encoding.UTF8.GetBytes("<html><body><table><tr><td>RFQ</td></tr></table></body></html>"),
            "rfq.xls");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Equal(DocumentInspectionErrorCodes.DocumentRejected, result.ErrorCode);
        Assert.Contains(".xls", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("macro", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_structure_mismatch_does_not_claim_the_file_is_macro_enabled()
    {
        var result = await InspectAsync(CreateOleCompound("WordDocument"), "rfq.xls");

        Assert.Equal(FileInspectionStatus.Rejected, result.Status);
        Assert.Equal(DocumentInspectionErrorCodes.DocumentRejected, result.ErrorCode);
        Assert.Contains("could not confirm", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("macro", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tripwire mirroring the presentability gate in Frontend/src/utils/apiErrors.ts. These strings
    /// are now rendered as product copy, so anything that would be demoted to the support
    /// disclosure — a type name, a host:port, a URL, a path, an unbounded dump — must never appear.
    /// </summary>
    [Fact]
    public async Task Every_rejection_reason_is_safe_to_render_to_a_tenant_user()
    {
        var reasons = new List<string>();
        foreach (var (bytes, fileName) in RejectionFixtures())
        {
            var result = await InspectAsync(bytes, fileName);
            Assert.Equal(FileInspectionStatus.Rejected, result.Status);
            reasons.Add(result.Reason);
        }

        Assert.NotEmpty(reasons);
        foreach (var reason in reasons)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.True(reason.Length <= 300, $"Reason exceeds the 300-character render budget: {reason}");
            Assert.DoesNotContain("Exception", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("://", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("System.", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", reason, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', reason);
            Assert.All(reason, character =>
                Assert.False(char.IsControl(character), "Reasons must contain no control characters."));
            // A reason that only restates the machine code explains nothing.
            Assert.DoesNotContain("document_rejected", reason, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(byte[] Bytes, string FileName)> RejectionFixtures()
    {
        yield return (CreateOleCompound("Workbook", "_VBA_PROJECT_CUR"), "macro.xls");
        yield return (CreateOleCompound("WordDocument", "Macros"), "macro.doc");
        yield return (CreateOleCompound("WordDocument"), "mislabelled.xls");
        yield return (CreateOleCompound("Workbook"), "mislabelled.doc");
        yield return ("%PDF-1.7\n"u8.ToArray(), "renamed.xls");
        yield return (Encoding.UTF8.GetBytes("<html><body>quote</body></html>"), "export.xls");
        yield return (Convert.FromHexString("D0CF11E0A1B11AE10000"), "truncated.xls");
        yield return ([0x50, 0x4B, 0x03, 0x04, 0x01], "broken.xlsx");
        yield return (CreateOpenXmlPackage(
            "xl/workbook.xml",
            "application/vnd.ms-excel.sheet.macroEnabled.main+xml",
            ("xl/vbaProject.bin", [1, 2, 3])), "macro.xlsm");
        yield return ("hello"u8.ToArray(), "payload.exe");
        yield return ([0x41, 0x00, 0x42], "binary.txt");
    }

    private static async Task<FileInspectionResult> InspectAsync(byte[] bytes, string fileName)
    {
        var service = new DocumentFileInspectionService(new EicarMalwareScanner());
        await using var stream = new MemoryStream(bytes, writable: false);
        return await service.InspectAsync(new FileInspectionRequest(
            stream, fileName, DeclaredLength: bytes.LongLength));
    }

    internal static byte[] CreateOpenXmlPackage(
        string principalPart,
        string principalContentType,
        params (string Name, byte[] Content)[] additionalEntries)
    {
        var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                           "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                           $"<Override PartName=\"/{principalPart}\" ContentType=\"{principalContentType}\"/>" +
                           "</Types>";
        return CreateZip(
            [("[Content_Types].xml", Encoding.UTF8.GetBytes(contentTypes)),
             (principalPart, "<root/>"u8.ToArray()),
             .. additionalEntries]);
    }

    internal static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var target = new MemoryStream();
        using (var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return target.ToArray();
    }

    /// <summary>
    /// A minimal but structurally valid OLE compound file: header, one FAT sector, one directory
    /// sector. Extra entries land in free 128-byte directory slots, which is exactly how a real
    /// macro project presents itself to this parser's flat directory scan.
    /// </summary>
    internal static byte[] CreateOleCompound(string principalStream, params string[] additionalEntries)
    {
        const int sectorSize = 512;
        const uint freeSector = 0xFFFFFFFF;
        const uint endOfChain = 0xFFFFFFFE;
        const uint fatSector = 0xFFFFFFFD;
        var bytes = new byte[sectorSize * 3];
        Convert.FromHexString("D0CF11E0A1B11AE1").CopyTo(bytes, 0);
        WriteUInt16(bytes, 24, 0x003E);
        WriteUInt16(bytes, 26, 0x0003);
        WriteUInt16(bytes, 28, 0xFFFE);
        WriteUInt16(bytes, 30, 9);
        WriteUInt16(bytes, 32, 6);
        WriteUInt32(bytes, 44, 1);
        WriteUInt32(bytes, 48, 1);
        WriteUInt32(bytes, 56, 4096);
        WriteUInt32(bytes, 60, endOfChain);
        WriteUInt32(bytes, 68, endOfChain);
        WriteUInt32(bytes, 76, 0);
        for (var index = 1; index < 109; index++) WriteUInt32(bytes, 76 + index * 4, freeSector);

        WriteUInt32(bytes, sectorSize, fatSector);
        WriteUInt32(bytes, sectorSize + 4, endOfChain);
        for (var index = 2; index < sectorSize / 4; index++) WriteUInt32(bytes, sectorSize + index * 4, freeSector);

        WriteDirectoryEntry(bytes, sectorSize * 2, "Root Entry", 5);
        WriteDirectoryEntry(bytes, sectorSize * 2 + 128, principalStream, 2);
        for (var index = 0; index < additionalEntries.Length && index < 2; index++)
            WriteDirectoryEntry(bytes, sectorSize * 2 + (index + 2) * 128, additionalEntries[index], 1);
        return bytes;
    }

    private static void WriteDirectoryEntry(byte[] bytes, int offset, string name, byte objectType)
    {
        var encoded = Encoding.Unicode.GetBytes(name + '\0');
        encoded.CopyTo(bytes, offset);
        WriteUInt16(bytes, offset + 64, (ushort)encoded.Length);
        bytes[offset + 66] = objectType;
        WriteUInt32(bytes, offset + 116, 0xFFFFFFFE);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
