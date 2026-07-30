using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;
using Tesseract;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Production <see cref="IExtractionDocumentReader"/>. Reads the immutably-stored source
/// file for a claimed <see cref="ExtractionJob"/> and turns it into a
/// <see cref="DocumentExtractionInput"/> for the extractor, handling the real RFQ formats:
///
///   * PDF   — PdfPig text layer, with a Docnet(rasterize)+Tesseract(OCR) fallback for
///             scanned / image-only PDFs (same approach as EmailService/ManualUploadService).
///   * DOCX  — OpenXML text extraction.
///   * XLS/XLSX — ExcelDataReader/EPPlus; header-mapped into <see cref="RfqSpreadsheetRow"/> and routed down the
///             DETERMINISTIC structured-bypass hook (IsStructured=true) so the LLM is skipped.
///   * CSV   — parsed into <see cref="RfqSpreadsheetRow"/> (structured bypass, same as XLSX).
///   * Images (jpg/jpeg/png/bmp/tiff) — Tesseract OCR, including every TIFF frame.
///   * Plain text / everything else — read as UTF-8.
///
/// Self-contained: it calls PdfPig / Docnet / Tesseract / OpenXML / EPPlus directly and does
/// NOT depend on EmailService or ManualUploadService. Register via Program.cs in place of
/// <see cref="DefaultExtractionDocumentReader"/> (which remains as a fallback).
/// </summary>
public sealed class ProductionDocumentReader : IExtractionDocumentReader
{
    private readonly ILogger<ProductionDocumentReader> _log;
    private readonly string _tessDataPath;
    private readonly IEvidenceObjectStorage _evidenceStorage;
    private readonly Func<byte[], IReadOnlyList<string>>? _tiffFrameOcr;
    private readonly NativeSpreadsheetParser _spreadsheetParser = new();

    // A PDF/image that yields fewer than this many non-whitespace characters is treated as scanned.
    private const int NearEmptyThreshold = 20;
    // Header/context slice size for the unstructured path (mirrors DefaultExtractionDocumentReader).
    private const int HeaderLineCount = 20;

    // pdfium (Docnet) and the Tesseract native engine are not thread-safe; serialize OCR.
    private static readonly object OcrLock = new();

    public ProductionDocumentReader(
        ILogger<ProductionDocumentReader> log,
        IWebHostEnvironment env,
        IEvidenceObjectStorage evidenceStorage)
        : this(log, env, evidenceStorage, null)
    {
    }

    internal ProductionDocumentReader(
        ILogger<ProductionDocumentReader> log,
        IWebHostEnvironment env,
        IEvidenceObjectStorage evidenceStorage,
        Func<byte[], IReadOnlyList<string>>? tiffFrameOcr)
    {
        _log = log;
        _evidenceStorage = evidenceStorage;
        _tiffFrameOcr = tiffFrameOcr;
        _tessDataPath = Path.Combine(env.ContentRootPath, "tessdata");
        // EPPlus 7 requires a license context; the app sets this at startup, set it here too
        // so the reader is safe to use independently of startup ordering.
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public async Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default)
    {
        var name = job.FileName ?? Path.GetFileName(job.StoragePath);
        var ext = (job.FileType ?? Path.GetExtension(job.StoragePath) ?? string.Empty)
            .TrimStart('.').ToLowerInvariant();

        byte[] bytes;
        try
        {
            await using var stream = await _evidenceStorage.OpenVerifiedReadAsync(
                job.StoragePath, job.ContentHash, ct);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            bytes = memory.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Verified evidence read failed for extraction job {JobId}.", job.Id);
            throw new EvidenceIntegrityException(job.Id, "verified_read_failed", ex);
        }

        // Structured spreadsheets/CSV bypass the LLM entirely via the deterministic normalizer.
        if (bytes.Length > 0 && (ext == "xlsx" || ext == "xlsm"))
        {
            return Structured(job, name,
                ParseSpreadsheet(() => _spreadsheetParser.ParseXlsx(bytes, name), name, "XLSX"));
        }
        if (bytes.Length > 0 && ext == "xls")
        {
            return Structured(job, name,
                ParseSpreadsheet(() => _spreadsheetParser.ParseXls(bytes, name), name, "XLS"));
        }
        if (bytes.Length > 0 && ext == "csv")
        {
            return Structured(job, name,
                ParseSpreadsheet(() => _spreadsheetParser.ParseCsv(bytes, name), name, "CSV"));
        }

        // Unstructured formats -> extract raw text, then chunk over line-item regions.
        var read = ext switch
        {
            "pdf" => ExtractTextFromPdf(bytes),
            // Legacy Word 97-2003 binary (SEC folder door): shared OLE/piece-table
            // parser; falls back to the OpenXML reader for mislabeled .docx files.
            "doc" => Native(ExtractTextFromLegacyDoc(bytes)),
            "docx" => Native(ExtractTextFromDocx(bytes)),
            "tiff" or "tif" => ExtractTextFromTiff(bytes),
            "jpg" or "jpeg" or "png" or "bmp" or "gif" or "webp" => ExtractTextFromImage(bytes),
            _ => Native(DecodeText(bytes))
        };

        return Unstructured(job, name, read);
    }

    // ---- input builders --------------------------------------------------

    private static DocumentExtractionInput Structured(ExtractionJob job, string name, List<RfqSpreadsheetRow> rows)
        => new()
        {
            BusinessUnitId = job.BusinessUnitId,
            SourceId = $"job:{job.Id}",
            ExtractionJobId = job.Id,
            SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
            SourceDocumentName = name,
            ProcessingPath = ExtractionProcessingPath.DeterministicRules,
            IsStructured = true,
            StructuredRows = rows,
            HeaderText = string.Empty,
            LineItemRegions = rows.Select(r => r.ProductName ?? string.Empty).ToList()
        };

    private static DocumentExtractionInput Unstructured(ExtractionJob job, string name, DocumentReadResult read)
    {
        var lines = read.Text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();

        var headerCount = Math.Min(HeaderLineCount, lines.Count);
        var header = string.Join('\n', lines.Take(headerCount));
        var regions = lines.Skip(headerCount).ToList();
        if (regions.Count == 0 && lines.Count > 0)
            regions = lines; // whole-doc pass when the body is short

        return new DocumentExtractionInput
        {
            BusinessUnitId = job.BusinessUnitId,
            SourceId = $"job:{job.Id}",
            ExtractionJobId = job.Id,
            SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
            SourceDocumentName = name,
            ProcessingPath = read.ProcessingPath,
            OcrStatus = read.OcrStatus,
            OcrPageCount = read.OcrPageCount,
            OcrTruncated = read.OcrTruncated,
            IsStructured = false,
            HeaderText = header,
            LineItemRegions = regions
        };
    }

    // ---- text formats ----------------------------------------------------

    private static string DecodeText(byte[] bytes)
        => bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);

    private string ExtractTextFromLegacyDoc(byte[] bytes)
    {
        var text = WordBinaryTextExtractor.Extract(bytes, _log);
        // A file named .doc that is actually OOXML has no OLE signature — try OpenXML.
        if (!string.IsNullOrWhiteSpace(text))
            return text;
        try
        {
            var openXmlText = ExtractTextFromDocx(bytes);
            if (!string.IsNullOrWhiteSpace(openXmlText))
                return openXmlText;
        }
        catch (DocumentParsingException)
        {
            // The legacy parser already established this is not readable OLE. A failed
            // OOXML fallback means the .doc content is unsupported, not retryable.
        }
        throw new UnsupportedDocumentFormatException(
            "The legacy .doc file passed security inspection but the local binary reader could not parse it; an isolated converter is not configured.");
    }

    private string ExtractTextFromDocx(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                throw new DocumentParsingException("The DOCX document has no readable main document body.");

            var lines = new List<string>();
            foreach (var element in body.Elements())
            {
                switch (element)
                {
                    case Paragraph paragraph:
                        AddLine(lines, ExtractParagraphText(paragraph));
                        break;
                    case Table table:
                        foreach (var row in table.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>()
                                .Select(cell => string.Join(" ", cell.Elements<Paragraph>()
                                    .Select(ExtractParagraphText)
                                    .Where(value => !string.IsNullOrWhiteSpace(value))))
                                .Select(value => value.Trim())
                                .ToArray();
                            AddLine(lines, string.Join('\t', cells));
                        }
                        break;
                }
            }

            return string.Join('\n', lines);
        }
        catch (DocumentParsingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DOCX extraction failed.");
            throw new DocumentParsingException("The DOCX document could not be parsed safely.", ex);
        }
    }

    private static string ExtractParagraphText(Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text runText:
                    text.Append(runText.Text);
                    break;
                case TabChar:
                    text.Append('\t');
                    break;
                case Break or CarriageReturn:
                    text.Append(' ');
                    break;
            }
        }
        return text.ToString().Trim();
    }

    private static void AddLine(List<string> lines, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add(value.Trim());
    }

    // ---- PDF (text layer + OCR fallback) ---------------------------------

    private DocumentReadResult ExtractTextFromPdf(byte[] bytes)
    {
        string pdfText = string.Empty;
        try
        {
            using var doc = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
                sb.AppendLine(page.Text);
            pdfText = sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PDF text extraction failed.");
        }

        // Fast path: the PDF already carries an embedded text layer.
        if (!IsNearEmpty(pdfText))
            return Native(pdfText);

        _log.LogInformation("PDF has little/no embedded text; attempting OCR fallback.");
        var ocr = TryOcrScannedPdf(bytes);
        if (!IsNearEmpty(ocr.Text))
            return new DocumentReadResult(
                "[OCR-EXTRACTED TEXT FROM SCANNED PDF - lower confidence, may contain recognition errors]\n" + ocr.Text,
                ExtractionProcessingPath.LocalOcr,
                ocr.FailedPageCount > 0 ? ExtractionOcrStatus.Partial : ExtractionOcrStatus.Completed,
                ocr.PageCount, ocr.Truncated);

        _log.LogWarning("Scanned PDF could not be OCR'd (OCR unavailable or produced no text).");
        return new DocumentReadResult(string.Empty, ExtractionProcessingPath.LocalOcr,
            ExtractionOcrStatus.Failed, ocr.PageCount, ocr.Truncated);
    }

    /// <summary>Rasterizes a scanned PDF with Docnet and OCRs each page with Tesseract.</summary>
    private OcrReadResult TryOcrScannedPdf(byte[] pdfBytes)
    {
        const int MaxOcrPages = 10;     // bound runtime for large documents
        const double RenderScale = 2.0; // ~144 DPI: OCR accuracy vs. memory/time
        try
        {
            var sb = new StringBuilder();
            var pagesToProcess = 0;
            var truncated = false;
            var failedPageCount = 0;
            lock (OcrLock)
            {
                using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(RenderScale));
                var pageCount = docReader.GetPageCount();
                pagesToProcess = Math.Min(pageCount, MaxOcrPages);
                truncated = pageCount > MaxOcrPages;
                if (pageCount > MaxOcrPages)
                    _log.LogWarning("Scanned PDF has {Total} pages; OCR limited to first {Limit}.", pageCount, MaxOcrPages);

                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                for (var i = 0; i < pagesToProcess; i++)
                {
                    try
                    {
                        using var pageReader = docReader.GetPageReader(i);
                        var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover()); // BGRA over white
                        var width = pageReader.GetPageWidth();
                        var height = pageReader.GetPageHeight();
                        if (rawBytes == null || width <= 0 || height <= 0 || rawBytes.Length < width * height * 4)
                            continue;

                        var bmp = BgraToBmp24(rawBytes, width, height);
                        using var pix = Pix.LoadFromMemory(bmp);
                        using var page = engine.Process(pix);
                        var pageText = page.GetText();
                        if (!string.IsNullOrWhiteSpace(pageText))
                            sb.AppendLine(pageText);
                    }
                    catch (Exception exPage)
                    {
                        failedPageCount++;
                        _log.LogWarning(exPage, "OCR failed for scanned PDF page {Page}.", i);
                    }
                }
            }
            return new OcrReadResult(sb.ToString(), pagesToProcess, truncated, failedPageCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scanned-PDF OCR fallback unavailable or failed.");
            return new OcrReadResult(string.Empty, 0, false, 0);
        }
    }

    // ---- images ----------------------------------------------------------

    private DocumentReadResult ExtractTextFromImage(byte[] bytes)
    {
        try
        {
            lock (OcrLock)
            {
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromMemory(bytes);
                using var page = engine.Process(img);
                var text = page.GetText();
                return new DocumentReadResult(text ?? string.Empty, ExtractionProcessingPath.LocalOcr,
                    IsNearEmpty(text) ? ExtractionOcrStatus.Failed : ExtractionOcrStatus.Completed, 1, false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Image OCR failed.");
            return new DocumentReadResult(string.Empty, ExtractionProcessingPath.LocalOcr,
                ExtractionOcrStatus.Failed, 0, false);
        }
    }

    private DocumentReadResult ExtractTextFromTiff(byte[] bytes)
    {
        if (_tiffFrameOcr != null)
        {
            var frameTexts = _tiffFrameOcr(bytes);
            return TiffResult(frameTexts, frameTexts.Count, 0);
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"nexora-ocr-{Guid.NewGuid():N}.tiff");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var temporary = new FileStream(temporaryPath, options))
                temporary.Write(bytes);
            lock (OcrLock)
            {
                using var images = PixArray.LoadMultiPageTiffFromFile(temporaryPath);
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                var text = new StringBuilder();
                var failedPageCount = 0;
                var pageNumber = 0;

                foreach (var image in images)
                {
                    pageNumber++;
                    try
                    {
                        using var page = engine.Process(image);
                        var pageText = page.GetText();
                        if (!string.IsNullOrWhiteSpace(pageText))
                            text.AppendLine(pageText);
                    }
                    catch (Exception ex)
                    {
                        failedPageCount++;
                        _log.LogWarning(ex, "OCR failed for TIFF page {Page}.", pageNumber);
                    }
                }

                return TiffResult([text.ToString()], images.Count, failedPageCount);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Multi-page TIFF OCR failed.");
            return new DocumentReadResult(string.Empty, ExtractionProcessingPath.LocalOcr,
                ExtractionOcrStatus.Failed, 0, false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Temporary TIFF cleanup failed.");
            }
        }
    }

    private static DocumentReadResult TiffResult(
        IReadOnlyList<string> frameTexts,
        int pageCount,
        int failedPageCount)
    {
        var value = string.Join('\n', frameTexts.Where(text => !string.IsNullOrWhiteSpace(text)));
        var status = IsNearEmpty(value)
            ? ExtractionOcrStatus.Failed
            : failedPageCount > 0
                ? ExtractionOcrStatus.Partial
                : ExtractionOcrStatus.Completed;
        return new DocumentReadResult(value, ExtractionProcessingPath.LocalOcr,
            status, pageCount, false);
    }

    // ---- spreadsheets -> structured rows ---------------------------------

    private List<RfqSpreadsheetRow> ParseSpreadsheet(
        Func<IReadOnlyList<RfqSpreadsheetRow>> parse,
        string name,
        string format)
    {
        try
        {
            var rows = parse().ToList();
            if (rows.Count == 0)
                throw new DocumentParsingException($"The {format} workbook contains no recognizable RFQ rows.");
            return rows;
        }
        catch (DocumentParsingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Format} structured parse failed for {Name}.", format, name);
            throw new DocumentParsingException($"The {format} workbook could not be parsed safely.", ex);
        }
    }

    // ---- image encoding helpers (Docnet BGRA -> 24-bit BMP for Tesseract) -

    private static byte[] BgraToBmp24(byte[] bgra, int width, int height)
    {
        var rowSize = ((24 * width + 31) / 32) * 4; // rows padded to a 4-byte boundary
        var pixelDataSize = rowSize * height;
        const int headerSize = 54;
        var bmp = new byte[headerSize + pixelDataSize];

        bmp[0] = 0x42; // 'B'
        bmp[1] = 0x4D; // 'M'
        WriteInt32LE(bmp, 2, bmp.Length);
        WriteInt32LE(bmp, 10, headerSize);
        WriteInt32LE(bmp, 14, 40);
        WriteInt32LE(bmp, 18, width);
        WriteInt32LE(bmp, 22, height); // positive -> bottom-up
        WriteInt16LE(bmp, 26, 1);
        WriteInt16LE(bmp, 28, 24);
        WriteInt32LE(bmp, 30, 0); // BI_RGB
        WriteInt32LE(bmp, 34, pixelDataSize);
        WriteInt32LE(bmp, 38, 2835);
        WriteInt32LE(bmp, 42, 2835);

        var srcStride = width * 4;
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * srcStride;
            var dst = headerSize + (height - 1 - y) * rowSize;
            for (var x = 0; x < width; x++)
            {
                var s = srcRow + x * 4;
                bmp[dst++] = bgra[s];     // B
                bmp[dst++] = bgra[s + 1]; // G
                bmp[dst++] = bgra[s + 2]; // R
            }
        }
        return bmp;
    }

    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static int CountNonWhitespace(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var n = 0;
        foreach (var c in s) if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    private static bool IsNearEmpty(string? s) => CountNonWhitespace(s) < NearEmptyThreshold;

    private static DocumentReadResult Native(string? text) => new(
        text ?? string.Empty, ExtractionProcessingPath.NativeParser,
        ExtractionOcrStatus.NotRequired, 0, false);

    private sealed record DocumentReadResult(
        string Text,
        ExtractionProcessingPath ProcessingPath,
        ExtractionOcrStatus OcrStatus,
        int OcrPageCount,
        bool OcrTruncated);

    private sealed record OcrReadResult(string Text, int PageCount, bool Truncated, int FailedPageCount);
}

public sealed class EvidenceIntegrityException : IOException
{
    public EvidenceIntegrityException(long extractionJobId, string code, Exception innerException)
        : base("The authoritative source document failed evidence integrity verification.", innerException)
    {
        ExtractionJobId = extractionJobId;
        Code = code;
    }

    public long ExtractionJobId { get; }
    public string Code { get; }
}

public class DocumentParsingException : IOException
{
    public DocumentParsingException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class UnsupportedDocumentFormatException : DocumentParsingException
{
    public UnsupportedDocumentFormatException(string message) : base(message) { }
}
