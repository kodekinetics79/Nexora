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

    // A PDF/image that yields fewer than this many non-whitespace characters is treated as scanned.
    private const int NearEmptyThreshold = 20;
    // Header/context slice size for the unstructured path (mirrors DefaultExtractionDocumentReader).
    private const int HeaderLineCount = 20;

    // pdfium (Docnet) and the Tesseract native engine are not thread-safe; serialize OCR.
    private static readonly object OcrLock = new();

    public ProductionDocumentReader(ILogger<ProductionDocumentReader> log, IWebHostEnvironment env)
    {
        _log = log;
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
            bytes = File.Exists(job.StoragePath)
                ? await File.ReadAllBytesAsync(job.StoragePath, ct)
                : Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read stored file {Path}.", job.StoragePath);
            bytes = Array.Empty<byte>();
        }

        // Structured spreadsheets/CSV bypass the LLM entirely via the deterministic normalizer.
        if (bytes.Length > 0 && (ext == "xlsx" || ext == "xlsm"))
        {
            var rows = ReadXlsxRows(bytes, name);
            if (rows.Count > 0)
                return Structured(job, name, rows);
        }
        if (bytes.Length > 0 && ext == "csv")
        {
            var rows = ReadCsvRows(bytes, name);
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

    private List<RfqSpreadsheetRow> ReadXlsxRows(byte[] bytes, string name)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var package = new ExcelPackage(ms);
            var ws = package.Workbook.Worksheets.FirstOrDefault(w => w.Dimension != null)
                     ?? package.Workbook.Worksheets.FirstOrDefault();
            if (ws?.Dimension == null)
                return new List<RfqSpreadsheetRow>();

            var firstRow = ws.Dimension.Start.Row;
            var lastRow = ws.Dimension.End.Row;
            var firstCol = ws.Dimension.Start.Column;
            var lastCol = ws.Dimension.End.Column;
            if (lastRow <= firstRow)
                return new List<RfqSpreadsheetRow>();

            // Header row = first row of the used range.
            var headers = new List<string>();
            for (var c = firstCol; c <= lastCol; c++)
                headers.Add((ws.Cells[firstRow, c].Text ?? string.Empty).Trim().ToLowerInvariant());

            var map = BuildColumnMap(headers.ToArray());

            string? Cell(int excelRow, int localIndex)
            {
                if (localIndex < 0) return null;
                var v = ws.Cells[excelRow, firstCol + localIndex].Text;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }

            var rows = new List<RfqSpreadsheetRow>();
            for (var r = firstRow + 1; r <= lastRow; r++)
            {
                var row = new RfqSpreadsheetRow
                {
                    RowNumber = r,
                    SourceDocumentName = name,
                    RfqNo = Cell(r, map.Rfq),
                    BuyerName = Cell(r, map.Buyer),
                    ReceivedDate = Cell(r, map.Recv),
                    BidClosingDate = Cell(r, map.Bid),
                    ProductName = Cell(r, map.Product),
                    Quantity = Cell(r, map.Qty),
                    UnitPrice = Cell(r, map.Price),
                    Currency = Cell(r, map.Curr),
                    ManufacturerName = Cell(r, map.Mfr),
                    ManufacturerPartNumber = Cell(r, map.Mpn),
                    LeadTimeDays = Cell(r, map.Lead)
                };

                // Skip fully-empty rows so trailing blanks don't inflate the item count.
                if (row.ProductName is null && row.ManufacturerPartNumber is null &&
                    row.Quantity is null && row.UnitPrice is null && row.RfqNo is null)
                    continue;

                rows.Add(row);
            }
            return rows;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "XLSX structured parse failed for {Name}.", name);
            return new List<RfqSpreadsheetRow>();
        }
    }

    private List<RfqSpreadsheetRow> ReadCsvRows(byte[] bytes, string name)
    {
        var text = DecodeText(bytes);
        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        if (lines.Count <= 1)
            return new List<RfqSpreadsheetRow>();

        var headers = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var map = BuildColumnMap(headers);

        string? Cell(string[] cells, int i) => i >= 0 && i < cells.Length && cells[i].Trim().Length > 0 ? cells[i].Trim() : null;

        var rows = new List<RfqSpreadsheetRow>();
        for (var r = 1; r < lines.Count; r++)
        {
            var cells = SplitCsv(lines[r]);
            rows.Add(new RfqSpreadsheetRow
            {
                RowNumber = r + 1,
                SourceDocumentName = name,
                RfqNo = Cell(cells, map.Rfq),
                BuyerName = Cell(cells, map.Buyer),
                ReceivedDate = Cell(cells, map.Recv),
                BidClosingDate = Cell(cells, map.Bid),
                ProductName = Cell(cells, map.Product),
                Quantity = Cell(cells, map.Qty),
                UnitPrice = Cell(cells, map.Price),
                Currency = Cell(cells, map.Curr),
                ManufacturerName = Cell(cells, map.Mfr),
                ManufacturerPartNumber = Cell(cells, map.Mpn),
                LeadTimeDays = Cell(cells, map.Lead)
            });
        }
        return rows;
    }

    private readonly record struct ColumnMap(
        int Rfq, int Buyer, int Recv, int Bid, int Product,
        int Qty, int Price, int Curr, int Mfr, int Mpn, int Lead);

    private static ColumnMap BuildColumnMap(string[] headers)
    {
        int Idx(params string[] keys) => Array.FindIndex(headers, h => keys.Contains(h));
        return new ColumnMap(
            Rfq: Idx("rfqno", "rfq no", "rfq"),
            Buyer: Idx("buyername", "buyer name", "buyer"),
            Recv: Idx("receiveddate", "received date"),
            Bid: Idx("bidclosingdate", "bid closing date"),
            Product: Idx("productname", "product name", "product", "description"),
            Qty: Idx("quantity", "qty"),
            Price: Idx("unitprice", "unit price", "price"),
            Curr: Idx("currency"),
            Mfr: Idx("manufacturername", "manufacturer"),
            Mpn: Idx("manufacturerpartnumber", "mpn", "part number"),
            Lead: Idx("leadtimedays", "lead time", "leadtime"));
    }

    // Minimal RFC-4180-ish splitter (handles quoted fields + escaped quotes).
    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result.ToArray();
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
