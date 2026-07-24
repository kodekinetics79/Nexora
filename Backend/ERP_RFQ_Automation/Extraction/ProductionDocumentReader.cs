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
///   * XLSX  — EPPlus; header-mapped into <see cref="RfqSpreadsheetRow"/> and routed down the
///             DETERMINISTIC structured-bypass hook (IsStructured=true) so the LLM is skipped.
///   * CSV   — parsed into <see cref="RfqSpreadsheetRow"/> (structured bypass, same as XLSX).
///   * Images (jpg/jpeg/png/bmp/tiff) — Tesseract OCR.
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
    {
        _log = log;
        _evidenceStorage = evidenceStorage;
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
            var rows = TryParseSpreadsheet(() => _spreadsheetParser.ParseXlsx(bytes, name), name, "XLSX");
            if (rows.Count > 0)
                return Structured(job, name, rows);
        }
        if (bytes.Length > 0 && ext == "csv")
        {
            var rows = TryParseSpreadsheet(() => _spreadsheetParser.ParseCsv(bytes, name), name, "CSV");
            if (rows.Count > 0)
                return Structured(job, name, rows);
        }

        // Unstructured formats -> extract raw text, then chunk over line-item regions.
        var text = ext switch
        {
            "pdf" => ExtractTextFromPdf(bytes),
            // Legacy Word 97-2003 binary (SEC folder door): shared OLE/piece-table
            // parser; falls back to the OpenXML reader for mislabeled .docx files.
            "doc" => ExtractTextFromLegacyDoc(bytes),
            "docx" => ExtractTextFromDocx(bytes),
            "jpg" or "jpeg" or "png" or "bmp" or "tiff" or "tif" or "gif" => ExtractTextFromImage(bytes),
            _ => DecodeText(bytes)
        };

        return Unstructured(job, name, text ?? string.Empty);
    }

    // ---- input builders --------------------------------------------------

    private static DocumentExtractionInput Structured(ExtractionJob job, string name, List<RfqSpreadsheetRow> rows)
        => new()
        {
            BusinessUnitId = job.BusinessUnitId,
            SourceId = $"{job.Id}:claim:{job.Attempts}",
            SourceDocumentName = name,
            IsStructured = true,
            StructuredRows = rows,
            HeaderText = string.Empty,
            LineItemRegions = rows.Select(r => r.ProductName ?? string.Empty).ToList()
        };

    private static DocumentExtractionInput Unstructured(ExtractionJob job, string name, string text)
    {
        var lines = text
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
            SourceId = $"{job.Id}:claim:{job.Attempts}",
            SourceDocumentName = name,
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
        return string.IsNullOrWhiteSpace(text) ? ExtractTextFromDocx(bytes) : text;
    }

    private string ExtractTextFromDocx(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(ms, false);
            var sb = new StringBuilder();
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body != null)
            {
                foreach (var t in body.Descendants<Text>())
                    sb.Append(t.Text).Append(' ');
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DOCX extraction failed.");
            return string.Empty;
        }
    }

    // ---- PDF (text layer + OCR fallback) ---------------------------------

    private string ExtractTextFromPdf(byte[] bytes)
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
            return pdfText;

        _log.LogInformation("PDF has little/no embedded text; attempting OCR fallback.");
        var ocr = TryOcrScannedPdf(bytes);
        if (!IsNearEmpty(ocr))
            return "[OCR-EXTRACTED TEXT FROM SCANNED PDF - lower confidence, may contain recognition errors]\n" + ocr;

        _log.LogWarning("Scanned PDF could not be OCR'd (OCR unavailable or produced no text).");
        return string.Empty;
    }

    /// <summary>Rasterizes a scanned PDF with Docnet and OCRs each page with Tesseract.</summary>
    private string TryOcrScannedPdf(byte[] pdfBytes)
    {
        const int MaxOcrPages = 10;     // bound runtime for large documents
        const double RenderScale = 2.0; // ~144 DPI: OCR accuracy vs. memory/time
        try
        {
            var sb = new StringBuilder();
            lock (OcrLock)
            {
                using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(RenderScale));
                var pageCount = docReader.GetPageCount();
                var pagesToProcess = Math.Min(pageCount, MaxOcrPages);
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
                        _log.LogWarning(exPage, "OCR failed for scanned PDF page {Page}.", i);
                    }
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scanned-PDF OCR fallback unavailable or failed.");
            return string.Empty;
        }
    }

    // ---- images ----------------------------------------------------------

    private string ExtractTextFromImage(byte[] bytes)
    {
        try
        {
            lock (OcrLock)
            {
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromMemory(bytes);
                using var page = engine.Process(img);
                return page.GetText();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Image OCR failed.");
            return string.Empty;
        }
    }

    // ---- spreadsheets -> structured rows ---------------------------------

    private List<RfqSpreadsheetRow> TryParseSpreadsheet(
        Func<IReadOnlyList<RfqSpreadsheetRow>> parse,
        string name,
        string format)
    {
        try
        {
            return parse().ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Format} structured parse failed for {Name}.", format, name);
            return new List<RfqSpreadsheetRow>();
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
