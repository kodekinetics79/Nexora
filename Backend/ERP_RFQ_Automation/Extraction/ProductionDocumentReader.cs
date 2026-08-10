using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Platform.Entitlements;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

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
///   * HTML  — HtmlAgilityPack, TABLE-AWARE (see <see cref="HtmlDocumentTextExtractor"/>): an
///             HTML RFQ's line items live in a &lt;table&gt; and a tag-strip destroys them.
///   * EML / MSG — the message is an envelope, not a text file: the sender's own words plus
///             every attachment, each attachment re-inspected and read by the same reader
///             (see <see cref="EmailContainerReader"/>).
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
    private readonly IFileInspectionService? _inspection;
    private readonly IEntitlementService? _entitlements;
    private readonly NativeSpreadsheetParser _spreadsheetParser = new();

    // A Word RFQ usually states its lines in a table, which is structured data and should not be
    // flattened to prose and sent to a model. Shares the spreadsheet column-alias and
    // header-location rules, so one set of buyer spellings serves both formats.
    private readonly DocxTableParser _docxTableParser;

    // An image (one page by definition) that yields fewer than this many non-whitespace
    // characters is treated as having produced nothing.
    private const int NearEmptyThreshold = 20;

    /// <summary>
    /// Characters per page below which a PDF's embedded text layer is NOT accepted as the
    /// document's content and OCR is attempted.
    ///
    /// <para>
    /// This replaces a flat 20-character test applied to the WHOLE document. That test was
    /// wrong in a way that lost content silently: a 40-page scanned tender that carries a
    /// digital-signature stamp, a Bates number or a portal footer in its text layer clears 20
    /// characters easily, so the reader returned those 25 characters as "native text" with
    /// <c>OcrStatus = NotRequired</c> — an assertion that nothing was needed, on a document
    /// whose entire content is images. The document then looked like a poor-quality RFQ rather
    /// than a missed OCR, and no surface anywhere said otherwise.
    /// </para>
    ///
    /// <para>
    /// 100 characters per page is chosen against the two populations it has to separate. Real
    /// tender prose runs 1,500–3,000 characters per page — a factor of 15 to 30 above this
    /// line. Text-layer ARTEFACTS run 20–80 characters per page: a signature block
    /// ("Digitally signed by ... Date: 2026-08-04"), a page number, a footer URL, a scanner
    /// watermark. 100 sits an order of magnitude below the smallest real page and above the
    /// largest artefact, so neither population lands near the boundary. It is also a DENSITY,
    /// not a total, which is the actual defect: the old test got easier to pass the longer the
    /// scanned document was, which is exactly backwards.
    /// </para>
    ///
    /// <para>
    /// Being below the line is not a verdict. It only means "OCR is worth trying" — and the
    /// native text is still kept if OCR recovers less, so a genuinely sparse one-page covering
    /// note cannot be lost to this threshold either.
    /// </para>
    /// </summary>
    internal const int MinimumNativeCharactersPerPage = 100;

    /// <summary>Absolute floor for a PDF whose page count could not be established.</summary>
    internal const int MinimumNativeCharacters = 20;

    // Header/context slice size for the unstructured path (mirrors DefaultExtractionDocumentReader).
    private const int HeaderLineCount = 20;

    /// <summary>
    /// Ceiling on the surrounding prose retained beside a structured table. Generous enough
    /// for the instruction block every real RFQ carries and small enough that a 4 MB
    /// materials RFP cannot put a novel through the evidence ledger and onto a review screen.
    /// The immutable source document remains the complete record either way.
    /// </summary>
    private const int MaxRetainedProseChars = 16_000;

    // pdfium (Docnet) and the Tesseract native engine are not thread-safe; serialize OCR.
    private static readonly object OcrLock = new();

    public ProductionDocumentReader(
        ILogger<ProductionDocumentReader> log,
        IWebHostEnvironment env,
        IEvidenceObjectStorage evidenceStorage)
        : this(log, env, evidenceStorage, null, null)
    {
    }

    /// <summary>
    /// The constructor DI selects. <paramref name="inspection"/> is what lets an email container
    /// put its own attachments through the SAME security inspection the top-level file went
    /// through. It is nullable only so the existing direct-construction call sites keep working;
    /// when it is absent, email attachments are refused rather than read unscanned.
    /// </summary>
    public ProductionDocumentReader(
        ILogger<ProductionDocumentReader> log,
        IWebHostEnvironment env,
        IEvidenceObjectStorage evidenceStorage,
        IFileInspectionService inspection)
        : this(log, env, evidenceStorage, null, inspection)
    {
    }

    /// <summary>The production DI constructor: OCR authorization is evaluated in the worker scope.</summary>
    public ProductionDocumentReader(
        ILogger<ProductionDocumentReader> log,
        IWebHostEnvironment env,
        IEvidenceObjectStorage evidenceStorage,
        IFileInspectionService inspection,
        IEntitlementService entitlements)
        : this(log, env, evidenceStorage, null, inspection, entitlements)
    {
    }

    internal ProductionDocumentReader(
        ILogger<ProductionDocumentReader> log,
        IWebHostEnvironment env,
        IEvidenceObjectStorage evidenceStorage,
        Func<byte[], IReadOnlyList<string>>? tiffFrameOcr,
        IFileInspectionService? inspection = null,
        IEntitlementService? entitlements = null)
    {
        _log = log;
        _evidenceStorage = evidenceStorage;
        _tiffFrameOcr = tiffFrameOcr;
        _inspection = inspection;
        _entitlements = entitlements;
        _docxTableParser = new DocxTableParser(_spreadsheetParser);
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

        // Evaluate once per claimed job before any OCR-capable branch. Native PDF text remains
        // available when OCR is disabled; the decision is enforced only if a branch would invoke
        // Tesseract/Docnet. Email containers carry the same decision into their attachments.
        EntitlementDecision? ocrDecision = null;
        if (_entitlements is not null && MayRequireOcr(ext))
            ocrDecision = await _entitlements.CheckFeatureAsync(
                job.BusinessUnitId, TypedEntitlementCatalog.Ocr, ct);

        // An EMAIL is an envelope, not a document: it has a body AND attachments, and the RFQ is
        // usually in the attachment. Handled before everything else because the answer is a
        // recursive read, not a text decode. Every attachment is re-inspected on the way out.
        if (bytes.Length > 0 && ext is "eml" or "msg")
        {
            return Unstructured(job, name, await ReadEmailContainerAsync(
                bytes, ext, job.BusinessUnitId, ocrDecision, ct));
        }

        if (bytes.Length > 0 && ext is "html" or "htm")
        {
            return Unstructured(job, name, Native(ExtractTextFromHtml(bytes)));
        }

        // DEFENCE IN DEPTH for the known real-world case: portal exports (SEC, Aramco and every
        // "export to Excel" button that emits an HTML table) arrive named .xls. Intake inspection
        // rejects those at the door with an actionable reason — magic-byte typing is not weakened
        // to let them through — but a job created before that check existed, or bytes that reach
        // this reader by any other route, must still be READ as what they are rather than thrown
        // at a binary workbook parser that can only fail.
        if (bytes.Length > 0 && ext is "xls" or "xlsx" or "xlsm"
            && HtmlDocumentTextExtractor.HasHtmlSignature(bytes))
        {
            _log.LogInformation(
                "Spreadsheet-named document {Name} carries HTML content; reading it as HTML.", name);
            return Unstructured(job, name, Native(ExtractTextFromHtml(bytes)),
                "This file is named as a spreadsheet but its contents are a web page (HTML). "
                + "Nexora read it as HTML so its table rows were preserved.");
        }

        // Structured spreadsheets/CSV bypass the LLM entirely via the deterministic
        // normalizer. A readable workbook whose column layout is NOT recognized falls
        // back to the same unstructured text path PDFs use (see ReadSpreadsheet).
        if (bytes.Length > 0 && (ext == "xlsx" || ext == "xlsm"))
        {
            return ReadSpreadsheet(job, name, "XLSX",
                () => _spreadsheetParser.ParseXlsx(bytes, name),
                () => _spreadsheetParser.RenderXlsxText(bytes));
        }
        if (bytes.Length > 0 && ext == "xls")
        {
            return ReadSpreadsheet(job, name, "XLS",
                () => _spreadsheetParser.ParseXls(bytes, name),
                () => _spreadsheetParser.RenderXlsText(bytes));
        }
        if (bytes.Length > 0 && ext == "csv")
        {
            return ReadSpreadsheet(job, name, "CSV",
                () => _spreadsheetParser.ParseCsv(bytes, name),
                () => _spreadsheetParser.RenderCsvText(bytes));
        }

        // A Word RFQ that states its lines in a table is structured data. Reading the table
        // deterministically costs nothing, cannot hallucinate, and — decisive in the current
        // deployment — does not depend on an AI provider the tenant may not be authorized to
        // use. A .docx with no mappable table falls through to the text path exactly as before.
        if (bytes.Length > 0 && ext == "docx")
        {
            IReadOnlyList<RfqSpreadsheetRow> tableRows;
            try
            {
                tableRows = _docxTableParser.Parse(bytes, name);
            }
            catch (Exception ex)
            {
                // A file that is not a readable Word document must still produce the typed,
                // honest parse failure the text path raises — not whatever OpenXML threw here.
                _log.LogDebug(ex, "DOCX table pre-parse failed for {Name}; using the text path.", name);
                tableRows = Array.Empty<RfqSpreadsheetRow>();
            }

            if (tableRows.Count > 0)
            {
                _log.LogInformation(
                    "DOCX {Name} was read deterministically from its table: {Rows} line(s), no model involved.",
                    name, tableRows.Count);
                return Structured(job, name, tableRows.ToList(), ProseOutsideTables(bytes, name));
            }
            // No mappable table — an ordinary prose document. Falls through to the text path
            // below, byte for byte as before: a prose RFQ is not a "layout not recognized"
            // spreadsheet and must not be given that banner or that reviewer note.
        }

        // Unstructured formats -> extract raw text, then chunk over line-item regions.
        var read = ext switch
        {
            "pdf" => ExtractTextFromPdf(bytes, job.BusinessUnitId, ocrDecision),
            // Legacy Word 97-2003 binary (SEC folder door): shared OLE/piece-table
            // parser; falls back to the OpenXML reader for mislabeled .docx files.
            "doc" => Native(ExtractTextFromLegacyDoc(bytes)),
            "docx" => Native(ExtractTextFromDocx(bytes)),
            "tiff" or "tif" => ExtractTextFromTiff(bytes, job.BusinessUnitId, ocrDecision),
            "jpg" or "jpeg" or "png" or "bmp" or "gif" or "webp" => ExtractTextFromImage(bytes, job.BusinessUnitId, ocrDecision),
            _ => Native(DecodeText(bytes))
        };

        return Unstructured(job, name, read);
    }

    private static bool MayRequireOcr(string ext) => ext is
        "pdf" or "tiff" or "tif" or "jpg" or "jpeg" or "png" or "bmp" or "gif" or "webp" or "eml" or "msg";

    private static void RequireOcrEntitlement(
        long businessUnitId, EntitlementDecision? decision)
    {
        // Null is limited to direct legacy/test construction. Production DI always supplies the
        // service through the five-argument constructor above.
        if (decision is { Allowed: false })
            throw new FeatureEntitlementDeniedException(
                businessUnitId, TypedEntitlementCatalog.Ocr, decision);
    }

    // ---- input builders --------------------------------------------------

    /// <summary>
    /// The document's own words outside its table, retained rather than discarded. Never
    /// throws and never fails a document: prose is context, and a document that reads
    /// perfectly must not be lost because its narrative could not be re-read.
    /// </summary>
    private string? ProseOutsideTables(byte[] bytes, string name)
    {
        try
        {
            var prose = ExtractTextFromDocx(bytes, includeTableRows: false);
            if (string.IsNullOrWhiteSpace(prose)) return null;
            return prose.Length <= MaxRetainedProseChars
                ? prose
                : prose[..MaxRetainedProseChars]
                  + "\n[Truncated: the document states more text than the reviewer panel retains. "
                  + "Open the source attachment for the rest.]";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Surrounding prose could not be retained for {Name}; the table still read.", name);
            return null;
        }
    }

    private static DocumentExtractionInput Structured(
        ExtractionJob job, string name, List<RfqSpreadsheetRow> rows, string? documentNarrative = null)
        => new()
        {
            DocumentNarrative = documentNarrative,
            BusinessUnitId = job.BusinessUnitId,
            SourceId = $"job:{job.Id}",
            // The lease attempt scopes every AI idempotency key this pass issues, so a
            // retried job makes NEW governed requests (see AttemptNumber).
            AttemptNumber = Math.Max(1, job.Attempts),
            ExtractionJobId = job.Id,
            SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
            SourceDocumentName = name,
            ProcessingPath = ExtractionProcessingPath.DeterministicRules,
            IsStructured = true,
            StructuredRows = rows,
            HeaderText = string.Empty,
            LineItemRegions = rows.Select(r => r.ProductName ?? string.Empty).ToList()
        };

    private static DocumentExtractionInput Unstructured(
        ExtractionJob job, string name, DocumentReadResult read, string? structuredFallbackNote = null)
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
            // Same attempt scoping as the structured path — see AttemptNumber.
            AttemptNumber = Math.Max(1, job.Attempts),
            ExtractionJobId = job.Id,
            SourceDocumentOccurrenceId = job.SourceDocumentOccurrenceId,
            SourceDocumentName = name,
            ProcessingPath = read.ProcessingPath,
            OcrStatus = read.OcrStatus,
            OcrPageCount = read.OcrPageCount,
            PageCount = read.PageCount,
            PageCountAuthoritative = read.PageCountAuthoritative,
            OcrTruncated = read.OcrTruncated,
            IsStructured = false,
            HeaderText = header,
            LineItemRegions = regions,
            // The reader's own note (why OCR was or was not taken, what an email container
            // refused) travels on the SAME field the spreadsheet fallback uses, because the
            // worker already prefixes that field onto the failure reason a reviewer reads.
            // Neither is ever silent.
            StructuredFallbackNote = structuredFallbackNote ?? read.Note
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

    /// <param name="includeTableRows">
    /// False to emit ONLY the prose the document states outside its tables. Every RFQ in the
    /// pilot corpus ends with instructions naming the required warranty, validity, country of
    /// origin, Incoterms and submission method, and none of it reached the lead for any of
    /// the 120 documents — the structured input was built with an EMPTY header and the
    /// line-item regions alone. "As per attached specification", a payment term and a
    /// validity period all live here.
    /// </param>
    private string ExtractTextFromDocx(byte[] bytes, bool includeTableRows = true)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(ms, false);
            var mainPart = doc.MainDocumentPart;
            if (mainPart == null)
                throw new DocumentParsingException("The DOCX document has no readable main document body.");

            // STREAMING, NOT DOM — deliberately. The previous implementation read
            // MainDocumentPart.Document.Body, which materialises the entire main part as
            // an object tree. A real Aramco RFP arrived on 2026-08-06 whose document.xml
            // expands to 121 MB (4.4 MB on disk — a materials table with thousands of
            // rows); the DOM for that is several times larger again, and this service
            // runs on a 512 MB instance. Accepting the file into a DOM reader would have
            // traded an intake rejection for a worker OOM. OpenXmlReader walks the part
            // as a token stream: memory is bounded by one line of output, not by the
            // document, so the intake size limit — not this method — decides what fits.
            //
            // Output shape preserved from the DOM version: one line per top-level
            // paragraph; one line per table row with cell texts tab-joined; paragraphs
            // inside a cell joined by spaces.
            var lines = new List<string>();
            var paragraph = new StringBuilder(); // current paragraph text (any depth)
            var cell = new StringBuilder();      // accumulated text of the current cell
            var row = new List<string>();        // completed cells of the current row
            var cellDepth = 0;                   // >0 while inside w:tc (nested tables stay flattened into the cell)

            using var reader = OpenXmlReader.Create(mainPart);
            while (reader.Read())
            {
                if (reader.IsStartElement)
                {
                    if (reader.ElementType == typeof(Text))
                    {
                        paragraph.Append(reader.GetText());
                    }
                    else if (reader.ElementType == typeof(TabChar))
                    {
                        paragraph.Append('\t');
                    }
                    else if (reader.ElementType == typeof(Break) || reader.ElementType == typeof(CarriageReturn))
                    {
                        paragraph.Append(' ');
                    }
                    else if (reader.ElementType == typeof(TableCell))
                    {
                        cellDepth++;
                    }
                }
                else if (reader.IsEndElement)
                {
                    if (reader.ElementType == typeof(Paragraph))
                    {
                        var text = paragraph.ToString();
                        paragraph.Clear();
                        if (cellDepth > 0)
                        {
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                if (cell.Length > 0) cell.Append(' ');
                                cell.Append(text.Trim());
                            }
                        }
                        else
                        {
                            AddLine(lines, text);
                        }
                    }
                    else if (reader.ElementType == typeof(TableCell))
                    {
                        cellDepth--;
                        if (cellDepth == 0)
                        {
                            row.Add(cell.ToString().Trim());
                            cell.Clear();
                        }
                    }
                    else if (reader.ElementType == typeof(TableRow) && cellDepth == 0 && row.Count > 0)
                    {
                        if (includeTableRows) AddLine(lines, string.Join('\t', row));
                        row.Clear();
                    }
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

    // ---- HTML ------------------------------------------------------------

    /// <summary>
    /// Table-aware HTML text. An HTML RFQ's line items are rows in a &lt;table&gt;; the previous
    /// treatment (no reader at all, so <c>.html</c> fell to <see cref="DecodeText"/> and the raw
    /// markup went to the extractor) and the tag-strip regex the mail path used both destroy the
    /// grid. <see cref="HtmlDocumentTextExtractor"/> emits the same tab-joined row shape the DOCX
    /// reader produces, so the chunker sees one familiar layout.
    /// </summary>
    private string ExtractTextFromHtml(byte[] bytes)
    {
        string text;
        try
        {
            text = HtmlDocumentTextExtractor.ExtractText(bytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "HTML extraction failed.");
            throw new DocumentParsingException(
                "This web page (HTML) file could not be read. It may be damaged; ask the sender to "
                + "export it again, or to send the original document.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DocumentParsingException(
                "This web page (HTML) file was opened successfully but contains no readable text. "
                + "If the content is a picture of a table, ask the sender for the underlying "
                + "document or a spreadsheet.");
        }

        return text;
    }

    // ---- email containers (.eml / .msg) ----------------------------------

    /// <summary>
    /// Reads an uploaded email as an envelope: the sender's own words plus every attachment's
    /// content. Attachments are re-inspected by the SAME security inspection the top-level file
    /// passed, so an <c>.eml</c> can never be used to smuggle an unscanned document past intake;
    /// nested messages recurse only to <see cref="EmailContainerReader.MaxContainerDepth"/>.
    ///
    /// <para>
    /// Everything the container refused — an unsupported attachment, a macro-enabled workbook, an
    /// attachment held because scanning was unavailable, a forward nested too deep — becomes a
    /// user-visible note. A message from which nothing at all could be read is a terminal parse
    /// failure carrying those same reasons, never an empty success.
    /// </para>
    /// </summary>
    private async Task<DocumentReadResult> ReadEmailContainerAsync(
        byte[] bytes, string ext, long businessUnitId, EntitlementDecision? ocrDecision, CancellationToken ct)
    {
        ParsedEmailMessage message;
        try
        {
            message = ext == "msg"
                ? EmailContainerReader.ParseMsg(bytes)
                : EmailContainerReader.ParseEml(bytes);
        }
        catch (OleCompoundFileException ex)
        {
            _log.LogWarning(ex, "Outlook .msg could not be parsed.");
            throw new DocumentParsingException(
                "This Outlook message could not be opened — its internal structure is damaged or "
                + "incomplete. Ask the sender to forward it again, or save its attachments and "
                + "upload them directly.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Email container could not be parsed.");
            throw new DocumentParsingException(
                "This email file could not be opened. Ask the sender to forward the message again, "
                + "or save its attachments and upload them directly.");
        }

        var flattened = await EmailContainerReader.FlattenAsync(
            message,
            _inspection,
            (fileName, attachmentExt, attachmentBytes, token) =>
                Task.FromResult(ReadAttachmentText(
                    fileName, attachmentExt, attachmentBytes, businessUnitId, ocrDecision)),
            ct);

        if (_inspection is null)
        {
            _log.LogWarning(
                "No file-inspection service is available to this reader; email attachments were refused "
                + "rather than read unscanned.");
        }

        var note = ComposeContainerNote(flattened);

        if (string.IsNullOrWhiteSpace(flattened.Text) || IsNearEmpty(flattened.Text))
        {
            throw new DocumentParsingException(
                "Nexora opened this email but could not read anything from it. "
                + (note ?? "It carries no message text and no readable attachments."));
        }

        return new DocumentReadResult(flattened.Text, ExtractionProcessingPath.NativeParser,
            ExtractionOcrStatus.NotRequired, 0, false)
        {
            Note = note
        };
    }

    /// <summary>Turns the container's refusal list into ONE user-safe sentence, bounded so a
    /// message with many refused attachments cannot produce an unbounded reason string.</summary>
    private static string? ComposeContainerNote(EmailContainerReadResult flattened)
    {
        if (flattened.Notes.Count == 0) return null;

        var head = $"This email carried {flattened.AttachmentsExtracted + flattened.AttachmentsRefused} "
                   + $"attachment(s); {flattened.AttachmentsExtracted} were read and "
                   + $"{flattened.AttachmentsRefused} were not.";
        var detail = new StringBuilder(head);
        foreach (var note in flattened.Notes)
        {
            if (detail.Length + note.Length + 1 > 900) break;
            detail.Append(' ').Append(note);
        }
        return detail.ToString();
    }

    /// <summary>
    /// Reads ONE attachment with exactly the parser an upload of that same file would use, so a
    /// PDF attached to an email and the same PDF uploaded directly cannot be read differently.
    ///
    /// <para>
    /// Spreadsheets take their RENDERED TEXT rather than the deterministic structured-row
    /// fast-path: the container is ONE extraction input and a single input cannot be both
    /// structured and unstructured. The deterministic path stays available to the same workbook
    /// uploaded on its own, and is fully restored by the per-attachment job fan-out reported as
    /// a seam.
    /// </para>
    /// </summary>
    private string ReadAttachmentText(
        string fileName, string ext, byte[] bytes, long businessUnitId, EntitlementDecision? ocrDecision)
    {
        if (bytes.Length == 0) return string.Empty;

        switch (ext)
        {
            case "xlsx":
            case "xlsm":
                return _spreadsheetParser.RenderXlsxText(bytes);
            case "xls":
                return HtmlDocumentTextExtractor.HasHtmlSignature(bytes)
                    ? ExtractTextFromHtml(bytes)
                    : _spreadsheetParser.RenderXlsText(bytes);
            case "csv":
                return _spreadsheetParser.RenderCsvText(bytes);
            case "pdf":
                return ExtractTextFromPdf(bytes, businessUnitId, ocrDecision).Text;
            case "doc":
                return ExtractTextFromLegacyDoc(bytes);
            case "docx":
                return ExtractTextFromDocx(bytes);
            case "html":
            case "htm":
                return ExtractTextFromHtml(bytes);
            case "tif":
            case "tiff":
                return ExtractTextFromTiff(bytes, businessUnitId, ocrDecision).Text;
            case "jpg":
            case "jpeg":
            case "png":
            case "bmp":
            case "gif":
            case "webp":
                return ExtractTextFromImage(bytes, businessUnitId, ocrDecision).Text;
            default:
                return DecodeText(bytes);
        }
    }

    // ---- PDF (text layer + OCR fallback) ---------------------------------

    private DocumentReadResult ExtractTextFromPdf(
        byte[] bytes, long businessUnitId = 0, EntitlementDecision? ocrDecision = null)
    {
        var pdfText = string.Empty;
        var pageCount = 0;
        try
        {
            using var doc = PdfDocument.Open(bytes);
            pageCount = doc.NumberOfPages;
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
                sb.AppendLine(page.Text);
            pdfText = sb.ToString();
        }
        catch (PdfDocumentEncryptedException ex)
        {
            // A DISTINCT, TERMINAL disposition. Previously this landed in the catch-all below,
            // was logged as a warning, fell through to the OCR branch, and — because pdfium
            // cannot render an encrypted document either — surfaced as OcrStatus='Failed'. The
            // user was told OCR had failed on a document that is not scanned at all, and the one
            // remedy that works (ask the sender for an unlocked copy) was never mentioned.
            _log.LogWarning(ex, "PDF is password-protected; extraction stopped with a truthful disposition.");
            throw new UnsupportedDocumentFormatException(
                "This PDF is password-protected, so Nexora cannot open it. Ask the sender for a "
                + "copy without a password, or remove the password and upload it again.");
        }
        catch (Exception ex)
        {
            // Not terminal on its own: a PDF whose text layer cannot be parsed may still be a
            // perfectly rasterisable scan. It becomes terminal below only if OCR also finds
            // nothing, and then it is reported as unreadable rather than as "scanned".
            _log.LogWarning(ex, "PDF text extraction failed; the document may be scanned or damaged.");
        }

        var nativeCharacters = CountNonWhitespace(pdfText);
        var threshold = NativeTextThreshold(pageCount);
        var density = pageCount > 0 ? nativeCharacters / (double)pageCount : nativeCharacters;

        // Fast path: the PDF carries a text layer DENSE ENOUGH to be the document's content.
        if (nativeCharacters >= threshold)
        {
            _log.LogInformation(
                "PDF text layer accepted as native content: {Characters} characters over {Pages} page(s) "
                + "({Density:0.#} per page, threshold {Threshold}).",
                nativeCharacters, pageCount, density, threshold);
            return Native(pdfText, Math.Max(1, pageCount));
        }

        _log.LogInformation(
            "PDF text layer is too sparse to be the content ({Characters} characters over {Pages} page(s), "
            + "{Density:0.#} per page, below {PerPage} per page); attempting OCR.",
            nativeCharacters, pageCount, density, MinimumNativeCharactersPerPage);

        RequireOcrEntitlement(businessUnitId, ocrDecision);

        var ocr = TryOcrScannedPdf(bytes);
        var ocrCharacters = CountNonWhitespace(ocr.Text);

        // OCR wins only when it actually recovered MORE than the text layer held. This is what
        // makes the per-page threshold safe to set high: a genuinely sparse one-page covering
        // note keeps its own text instead of being replaced by an empty OCR pass.
        if (ocrCharacters > nativeCharacters && ocrCharacters >= NearEmptyThreshold)
        {
            return new DocumentReadResult(
                "[OCR-EXTRACTED TEXT FROM SCANNED PDF - lower confidence, may contain recognition errors]\n" + ocr.Text,
                ExtractionProcessingPath.LocalOcr,
                ocr.FailedPageCount > 0 ? ExtractionOcrStatus.Partial : ExtractionOcrStatus.Completed,
                ocr.PageCount, ocr.Truncated)
            {
                PageCount = Math.Max(pageCount, ocr.PageCount),
                PageCountAuthoritative = pageCount > 0 || ocr.PageCount > 0,
                Note = ocr.Truncated
                    ? $"This PDF is a scan. Text was recovered by OCR from the first {ocr.PageCount} page(s) only; "
                      + "later pages were not read. OCR text is lower confidence than a native text layer."
                    : null
            };
        }

        if (nativeCharacters > 0)
        {
            // Keep what the text layer held — losing it would be the very silent loss this change
            // exists to remove — but say plainly that the document looks like a scan and that OCR
            // added nothing, so a reviewer reads a sparse result as a scan, not as a poor RFQ.
            return new DocumentReadResult(pdfText, ExtractionProcessingPath.NativeParser,
                ExtractionOcrStatus.Failed, ocr.PageCount, ocr.Truncated)
            {
                PageCount = Math.Max(pageCount, ocr.PageCount),
                PageCountAuthoritative = pageCount > 0 || ocr.PageCount > 0,
                Note = $"This PDF carries only {nativeCharacters} characters of text across "
                       + $"{Math.Max(pageCount, 1)} page(s), which is the pattern of a SCANNED document "
                       + "with a stamp or footer rather than a document with real text. Optical "
                       + "character recognition was attempted and recovered nothing further, so only "
                       + "that text was read."
            };
        }

        _log.LogWarning("PDF yielded no text: the text layer is empty and OCR recovered nothing.");
        return new DocumentReadResult(string.Empty, ExtractionProcessingPath.LocalOcr,
            ExtractionOcrStatus.Failed, ocr.PageCount, ocr.Truncated)
        {
            PageCount = Math.Max(pageCount, ocr.PageCount),
            PageCountAuthoritative = pageCount > 0 || ocr.PageCount > 0,
            Note = "Nexora could not read any text from this PDF. It is either a scan that optical "
                   + "character recognition could not process, or the file is damaged. Ask the sender "
                   + "for a text-based PDF, or for the original document."
        };
    }

    /// <summary>
    /// Non-whitespace characters a PDF's text layer must carry before it counts as the document's
    /// content. Per-PAGE by design — see <see cref="MinimumNativeCharactersPerPage"/>.
    /// </summary>
    internal static int NativeTextThreshold(int pageCount) => pageCount <= 0
        ? MinimumNativeCharacters
        : Math.Max(MinimumNativeCharacters, pageCount * MinimumNativeCharactersPerPage);

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

    private DocumentReadResult ExtractTextFromImage(
        byte[] bytes, long businessUnitId = 0, EntitlementDecision? ocrDecision = null)
    {
        RequireOcrEntitlement(businessUnitId, ocrDecision);
        try
        {
            lock (OcrLock)
            {
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromMemory(bytes);
                using var page = engine.Process(img);
                var text = page.GetText();
                return new DocumentReadResult(text ?? string.Empty, ExtractionProcessingPath.LocalOcr,
                    IsNearEmpty(text) ? ExtractionOcrStatus.Failed : ExtractionOcrStatus.Completed, 1, false)
                    { PageCount = 1, PageCountAuthoritative = true };
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Image OCR failed.");
            return new DocumentReadResult(string.Empty, ExtractionProcessingPath.LocalOcr,
                ExtractionOcrStatus.Failed, 0, false) { PageCount = 1, PageCountAuthoritative = true };
        }
    }

    private DocumentReadResult ExtractTextFromTiff(
        byte[] bytes, long businessUnitId = 0, EntitlementDecision? ocrDecision = null)
    {
        RequireOcrEntitlement(businessUnitId, ocrDecision);
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
                ExtractionOcrStatus.Failed, 0, false) { PageCount = 1, PageCountAuthoritative = false };
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
            status, pageCount, false)
            { PageCount = Math.Max(1, pageCount), PageCountAuthoritative = pageCount > 0 };
    }

    // ---- spreadsheets -> structured rows (with unstructured fallback) ----

    /// <summary>
    /// Reads a spreadsheet/CSV. Recognized column layouts take the deterministic
    /// structured fast-path (LLM bypassed) exactly as before. A workbook that was READ
    /// successfully but whose column layout the deterministic mapper does not recognize
    /// is the NORMAL case for first-contact customer files — it is NOT a terminal
    /// failure. Such documents fall back to the same unstructured path PDFs/Word
    /// documents use: sheet content rendered to text (sheet name + tab-joined rows) and
    /// routed to AI-assisted extraction, still subject to the per-tenant
    /// external-provider allow-list, which fail-closes into a retryable review hold —
    /// never a dead-letter — when no provider is authorized. Only a workbook that
    /// cannot be parsed at all, or that contains no cell content whatsoever, remains a
    /// permanent <see cref="DocumentParsingException"/>.
    /// </summary>
    private DocumentExtractionInput ReadSpreadsheet(
        ExtractionJob job,
        string name,
        string format,
        Func<IReadOnlyList<RfqSpreadsheetRow>> parse,
        Func<string> renderText)
    {
        List<RfqSpreadsheetRow> rows;
        try
        {
            rows = parse().ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Format} structured parse failed for {Name}.", format, name);
            throw new DocumentParsingException($"The {format} workbook could not be parsed safely.", ex);
        }

        if (rows.Count > 0)
            return Structured(job, name, rows); // deterministic fast-path, unchanged

        string rendered;
        try
        {
            rendered = renderText();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "{Format} text rendering failed for {Name} after the structured parse recognized no rows.",
                format, name);
            throw new DocumentParsingException($"The {format} workbook could not be parsed safely.", ex);
        }

        if (string.IsNullOrWhiteSpace(rendered))
            throw new DocumentParsingException(
                $"The {format} workbook was read successfully but contains no cell content to extract.");

        _log.LogInformation(
            "{Format} workbook {Name} was read but no RFQ column layout was recognized; " +
            "falling back to unstructured text extraction.",
            format, name);

        var fallbackNote =
            $"The {format} spreadsheet was read successfully, but its column layout was not recognized " +
            "by the deterministic RFQ mapper. Its sheet content was rendered to text and routed to " +
            "AI-assisted extraction; if no authorized AI provider is available for this tenant, the " +
            "document is held for review instead.";

        return Unstructured(job, name,
            Native("[SPREADSHEET LAYOUT NOT RECOGNIZED - the workbook was read successfully but the "
                   + "deterministic column mapping found no known RFQ headers; tab-separated sheet text follows]\n"
                   + rendered),
            fallbackNote);
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

    private static DocumentReadResult Native(string? text, int pageCount = 0) => new(
        text ?? string.Empty, ExtractionProcessingPath.NativeParser,
        ExtractionOcrStatus.NotRequired, 0, false)
        { PageCount = Math.Max(1, pageCount), PageCountAuthoritative = pageCount > 0 };

    private sealed record DocumentReadResult(
        string Text,
        ExtractionProcessingPath ProcessingPath,
        ExtractionOcrStatus OcrStatus,
        int OcrPageCount,
        bool OcrTruncated)
    {
        public int PageCount { get; init; } = 1;
        public bool PageCountAuthoritative { get; init; }
        /// <summary>User-safe sentence explaining anything the reader did not take, or why it
        /// took the path it did. Surfaced through <c>DocumentExtractionInput.StructuredFallbackNote</c>.</summary>
        public string? Note { get; init; }
    }

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
