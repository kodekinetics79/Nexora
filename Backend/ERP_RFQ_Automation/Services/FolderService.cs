using DocumentFormat.OpenXml.Wordprocessing; 
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using ERP_RFQ_Automation.Infrastructure.Storage;

namespace ERP_RFQ_Automation.Services
{
    public sealed class FolderProcessingReport
    {
        public Guid BatchId { get; init; } = Guid.NewGuid();
        public int Enqueued { get; set; }
        public int Duplicates { get; set; }
        public int Rejected { get; set; }
        public int Failed { get; set; }
    }

    public class FolderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<FolderService> _logger;
        private readonly ILLMService _llmService;
        private readonly string _sharedFolderPath;
        private readonly string _secFolderPath;
        private readonly string _aramcoFolderPath;
        private readonly string _processedFolderPath;
        private readonly string _attachmentPath;
        private readonly IFileStorage _storage;
        private readonly ILegacyDocumentStore _legacyDocuments;
        private const long MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024; // 25 MB

        private readonly ERP_RFQ_Automation.Extraction.IDocumentIngestion? _ingestion;

        public FolderService(
            ErpRfqAutomationContext context,
            IWebHostEnvironment env,
            ILogger<FolderService> logger,
            ILLMService llmService,
            IFileStorage storage,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion? ingestion = null,
            ILegacyDocumentStore? legacyDocuments = null)
        {
            _context = context;
            _env = env;
            _logger = logger;
            _llmService = llmService;
            _ingestion = ingestion;
            _storage = storage;
            _legacyDocuments = legacyDocuments
                ?? new LegacyDocumentStore(storage, new LocalEvidenceObjectStorage(storage), routeToObjectStore: false);
            _sharedFolderPath = storage.GetPath("Shared_Leads_Folder");
            _secFolderPath = storage.GetPath("SEC_Leads_Folder");
            _aramcoFolderPath = storage.GetPath("Aramco_Leads_Folder");
            _processedFolderPath = storage.GetPath("Processed_Leads_Folder");
            _attachmentPath = storage.GetPath("Leads_Folder_Attachments");
            
            Directory.CreateDirectory(_attachmentPath);
        }

        // ============================================================
        //  OLE Compound Document + Word Binary Format Text Extraction
        // ============================================================
        // The OLE/FIB/piece-table parser moved VERBATIM to the shared
        // DocumentIntelligence.WordBinaryTextExtractor so the unified extraction
        // pipeline's ProductionDocumentReader can read the same SEC .doc files.
        private string ExtractTextFromDoc(MemoryStream ms)
            => DocumentIntelligence.WordBinaryTextExtractor.Extract(ms.ToArray(), _logger);

        // ============================================================
        //  Main Processing Methods
        // ============================================================

        /// <summary>
        /// Writes the uploaded files and returns HOW MANY REACHED DISK.
        ///
        /// <para>This used to return <c>Task</c>, and the controller answered
        /// "{files.Count} files uploaded successfully" — the count REQUESTED, which the method
        /// structurally could not contradict. Three paths below skip a file and carry on: an
        /// unusable filename, a filename that resolves outside the target folder, and a zero-byte
        /// upload (the ordinary outcome of a failed drag from a network share or a cloud-synced
        /// placeholder, and the one that did not even log). A user who dropped five files and had
        /// two skipped was told five arrived, and only the server log disagreed.</para>
        /// </summary>
        public async Task<int> SaveFilesToSharedFolderAsync(
            List<Microsoft.AspNetCore.Http.IFormFile> files,
            string folderType,
            long businessUnitId,
            CancellationToken cancellationToken = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            var targetFolder = GetTenantFolderPath(businessUnitId, folderType);
            Directory.CreateDirectory(targetFolder);
            _storage.ResolvePath(targetFolder);

            var saved = 0;
            foreach (var file in files)
            {
                if (file.Length <= 0)
                {
                    // Previously skipped in total silence, by an `if (file.Length > 0)` with no
                    // else. An empty file is a real user event, not noise.
                    _logger.LogWarning(
                        "Skipped zero-byte upload '{FileName}' for {FolderType}.", file.FileName, folderType);
                    continue;
                }
                {
                    // SEC-10: never trust the client-supplied filename. Strip any directory
                    // component, sanitize, and verify the resolved path stays inside the
                    // target folder — prevents path traversal / arbitrary file write.
                    var safeName = SanitizeFileName(Path.GetFileName(file.FileName));
                    if (string.IsNullOrWhiteSpace(safeName))
                    {
                        _logger.LogWarning("Rejected upload with unusable filename '{FileName}'.", file.FileName);
                        continue;
                    }
                    var extension = Path.GetExtension(safeName).ToLowerInvariant();
                    if (!IsAllowedUploadExtension(folderType, extension))
                        throw new ArgumentException($"File type '{extension}' is not permitted for {folderType} ingestion.");
                    if (file.Length > MAX_ATTACHMENT_SIZE)
                        throw new InvalidOperationException($"File '{safeName}' exceeds the 25 MB limit.");
                    var finalName = $"{Guid.NewGuid():N}_{safeName}";
                    var filePath = Path.Combine(targetFolder, finalName);
                    var fullTarget = Path.GetFullPath(targetFolder);
                    var fullPath = Path.GetFullPath(filePath);
                    var targetPrefix = fullTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Rejected path-traversal filename '{FileName}'.", file.FileName);
                        continue;
                    }
                    var stagingFolder = Path.Combine(targetFolder, ".staging");
                    Directory.CreateDirectory(stagingFolder);
                    _storage.ResolvePath(stagingFolder);
                    var temporaryPath = Path.Combine(stagingFolder, finalName + ".tmp");
                    _storage.ResolvePath(temporaryPath);
                    _storage.ResolvePath(filePath);
                    try
                    {
                        await using (var stream = new FileStream(
                            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            bufferSize: 64 * 1024, useAsync: true))
                        {
                            await file.CopyToAsync(stream, cancellationToken);
                            await stream.FlushAsync(cancellationToken);
                        }
                        File.Move(temporaryPath, filePath, false);
                    }
                    catch
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                        throw;
                    }
                    _logger.LogInformation("Saved file {FileName} to folder {FolderType}.", safeName, folderType);
                    saved++;
                }
            }

            return saved;
        }

        public async Task<FolderProcessingReport> ProcessAllFoldersAsync(
            long businessUnitId,
            CancellationToken cancellationToken = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            if (_ingestion is null)
                throw new InvalidOperationException("The durable document-ingestion gateway is unavailable.");

            var sec = GetTenantFolderPath(businessUnitId, "SEC");
            var aramco = GetTenantFolderPath(businessUnitId, "Aramco");
            var shared = GetTenantFolderPath(businessUnitId, "Shared");
            Directory.CreateDirectory(sec);
            Directory.CreateDirectory(aramco);
            Directory.CreateDirectory(shared);
            var report = new FolderProcessingReport();
            // ONE filter for all three folders. These used to differ — .doc for SEC, .docx for
            // Aramco, five types for Shared — and the mismatch was live: the Aramco corpus is
            // entirely .doc, so its own folder skipped every file it was given. See
            // WatchedFolderExtensions for why the extension gate never protected anything that
            // byte-level inspection does not already protect better.
            bool Readable(string ext) => WatchedFolderExtensions.Contains(ext);
            await EnqueueFolderFilesAsync(
                sec, "SEC Leads", Readable, businessUnitId, report, cancellationToken);
            await EnqueueFolderFilesAsync(
                aramco, "Aramco Leads", Readable, businessUnitId, report, cancellationToken);
            await EnqueueFolderFilesAsync(
                shared, "Shared Leads", Readable, businessUnitId, report, cancellationToken);
            return report;
        }

        /// <summary>
        /// ING-05: routes one watched folder through the unified extraction queue —
        /// each matching file becomes its own content-addressed job (shared batch); the
        /// original is moved to the Processed folder once enqueued (the queue holds an
        /// immutable copy). Per-file failures are isolated and the file is LEFT IN PLACE
        /// so the next run retries it; re-enqueueing already-seen content is a no-op via
        /// the (BusinessUnitId, ContentHash) idempotency.
        /// </summary>
        private async Task EnqueueFolderFilesAsync(
            string folder,
            string leadSourceLabel,
            Func<string, bool> extFilter,
            long businessUnitId,
            FolderProcessingReport report,
            CancellationToken cancellationToken)
        {
            var processingFolder = _storage.GetPath(
                "Tenants", businessUnitId.ToString(CultureInfo.InvariantCulture),
                "Processing", leadSourceLabel.Replace(' ', '_'));
            Directory.CreateDirectory(processingFolder);
            RecoverStaleClaims(processingFolder, folder);
            var filePaths = Directory.GetFiles(folder);
            if (!filePaths.Any())
            {
                _logger.LogInformation("No files found in {Label} folder.", leadSourceLabel);
                return;
            }

            foreach (var filePath in filePaths)
            {
                var fileName = Path.GetFileName(filePath);
                if (fileName.EndsWith(".nexora-retry.json", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(fileName).ToLowerInvariant();

                string? claimedPath = null;
                try
                {
                    var claimCandidate = Path.Combine(processingFolder, $"{Guid.NewGuid():N}_{fileName}");
                    File.Move(filePath, claimCandidate, false);
                    claimedPath = claimCandidate;
                    File.SetLastWriteTimeUtc(claimedPath, DateTime.UtcNow);

                    if (new FileInfo(claimedPath).LinkTarget is not null)
                    {
                        _logger.LogWarning("Rejected symbolic-link file in {Label} folder: {FileName}", leadSourceLabel, fileName);
                        await QuarantineAsync(claimedPath, businessUnitId, leadSourceLabel,
                            "Symbolic-link files are prohibited.", 1, cancellationToken);
                        report.Rejected++;
                        continue;
                    }
                    if (!extFilter(ext))
                    {
                        await QuarantineAsync(claimedPath, businessUnitId, leadSourceLabel,
                            "Unsupported file type.", 1, cancellationToken);
                        report.Rejected++;
                        continue;
                    }

                    var resolvedPath = _storage.ResolvePath(claimedPath);
                    if (!Path.GetFullPath(resolvedPath).Equals(Path.GetFullPath(claimedPath), PathComparison()))
                        throw new UnauthorizedAccessException("The watched file path did not resolve to itself.");
                    var bytes = await File.ReadAllBytesAsync(resolvedPath, cancellationToken);
                    if (bytes.Length == 0)
                    {
                        await QuarantineAsync(claimedPath, businessUnitId, leadSourceLabel,
                            "Empty file.", 1, cancellationToken);
                        report.Rejected++;
                        continue;
                    }
                    if (bytes.Length > MAX_ATTACHMENT_SIZE)
                    {
                        await QuarantineAsync(claimedPath, businessUnitId, leadSourceLabel,
                            "File exceeds the 25 MB limit.", 1, cancellationToken);
                        report.Rejected++;
                        continue;
                    }

                    var result = await _ingestion!.IngestAsync(
                        bytes, fileName, businessUnitId,
                        ERP_RFQ_Automation.Extraction.ExtractionSourceType.Folder,
                        report.BatchId, priority: 0,
                        new ERP_RFQ_Automation.Extraction.ExtractionJobMetadata
                        {
                            ClientEmail = "",
                            LeadSource = leadSourceLabel,
                            EmailSource = leadSourceLabel == "Aramco Leads" ? "Aramco RFP Document" : GetFileTypeLabel(ext)
                        }, ct: cancellationToken);
                    _logger.LogInformation("Enqueued {Label} file {FileName} as job {JobId} ({Outcome}).",
                        leadSourceLabel, fileName, result.JobId, result.Outcome);

                    if (result.Outcome == ERP_RFQ_Automation.Extraction.EnqueueOutcome.Duplicate &&
                        result.ExistingStatus == ERP_RFQ_Automation.Extraction.ExtractionStatus.DeadLetter)
                    {
                        await QuarantineAsync(claimedPath, businessUnitId, leadSourceLabel,
                            $"Matching extraction job {result.JobId} is dead-lettered.", 1, cancellationToken);
                        report.Rejected++;
                        continue;
                    }

                    _storage.ResolvePath(claimedPath);
                    var processedFolder = _storage.GetPath(
                        "Tenants", businessUnitId.ToString(CultureInfo.InvariantCulture), "Processed", leadSourceLabel.Replace(' ', '_'));
                    Directory.CreateDirectory(processedFolder);
                    var processedPath = Path.Combine(processedFolder, $"{result.ContentHash}_{result.JobId}{ext}");
                    if (File.Exists(processedPath))
                        processedPath = Path.Combine(processedFolder, $"{Guid.NewGuid():N}_{fileName}");
                    File.Move(claimedPath, processedPath, false);
                    await ClearRetryStateAsync(businessUnitId, leadSourceLabel, fileName, cancellationToken);
                    DeleteLegacyRetrySidecar(filePath);
                    if (result.Outcome == ERP_RFQ_Automation.Extraction.EnqueueOutcome.Enqueued)
                        report.Enqueued++;
                    else
                        report.Duplicates++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (claimedPath is not null && File.Exists(claimedPath) && !File.Exists(filePath))
                        File.Move(claimedPath, filePath, false);
                    throw;
                }
                catch (FileNotFoundException) when (claimedPath is null && !File.Exists(filePath))
                {
                    // Another concurrent sweep atomically claimed this watched file.
                }
                catch (EvidenceStorageUnavailableException ex)
                {
                    // A storage outage is NOT three strikes against this file. The generic
                    // handler below counts every failure toward RecordRetryAsync, so a
                    // ten-minute outage across three sweeps quarantined every watched document
                    // as "Staging failed after three attempts" — a permanent rejection naming
                    // nothing fixable, for files that were never even read as faulty.
                    //
                    // The file goes back to the watched folder untouched, no attempt is
                    // recorded, and the sweep stops: every remaining file would fail the same
                    // way. The next sweep picks all of them up once storage is restored.
                    _logger.LogError(ex,
                        "Durable evidence storage is unavailable while staging {Label} file {FileName} "
                        + "(configuration fault: {IsConfigurationFault}). The file was left in place and the sweep stopped; "
                        + "no retry was counted against it.",
                        leadSourceLabel, fileName, ex.IsConfigurationFault);
                    if (claimedPath is not null && File.Exists(claimedPath) && !File.Exists(filePath))
                        File.Move(claimedPath, filePath, false);
                    report.Failed++;
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to enqueue {Label} file {FileName}.", leadSourceLabel, fileName);
                    var attempts = await RecordRetryAsync(
                        businessUnitId, leadSourceLabel, fileName, ex, cancellationToken);
                    if (attempts >= 3)
                    {
                        await QuarantineAsync(claimedPath ?? filePath, businessUnitId, leadSourceLabel,
                            "Staging failed after three attempts.", attempts, cancellationToken);
                        await ClearRetryStateAsync(businessUnitId, leadSourceLabel, fileName, cancellationToken);
                        DeleteLegacyRetrySidecar(filePath);
                        report.Rejected++;
                    }
                    else
                    {
                        if (claimedPath is not null && File.Exists(claimedPath) && !File.Exists(filePath))
                            File.Move(claimedPath, filePath, false);
                        report.Failed++;
                    }
                }
            }
        }

        private LeadExtractionResult ParseAramcoRFP(MemoryStream ms, string fileName)
        {
            try
            {
                using (var wordDoc = WordprocessingDocument.Open(ms, false))
                {
                    var body = wordDoc.MainDocumentPart.Document.Body;
                    var tables = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().ToList();

                    _logger.LogInformation("Found {TableCount} tables in Aramco document {FileName}", tables.Count, fileName);

                    if (tables.Count < 5)
                    {
                        _logger.LogWarning("Expected at least 5 tables in Aramco RFP, found {Count}", tables.Count);
                        return null;
                    }

                    string owner = null, eventType = null, currency = null;
                    var overviewTable = tables[1];
                    foreach (var row in overviewTable.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().ToList();
                        if (cells.Count >= 2)
                        {
                            var key   = GetCellText(cells[0]).Trim();
                            var value = GetCellText(cells[1]).Trim();
                            if (key.Contains("Owner",      StringComparison.OrdinalIgnoreCase)) owner     = value;
                            else if (key.Contains("Event Type", StringComparison.OrdinalIgnoreCase)) eventType = value;
                            else if (key.Contains("Currency",   StringComparison.OrdinalIgnoreCase)) currency  = value;
                        }
                    }

                    string publishDate = null, dueDate = null;
                    var timingTable = tables[2];
                    foreach (var row in timingTable.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().ToList();
                        if (cells.Count >= 2)
                        {
                            var key   = GetCellText(cells[0]).Trim();
                            var value = GetCellText(cells[1]).Trim();
                            if (key.Contains("Publish",  StringComparison.OrdinalIgnoreCase)) publishDate = value;
                            else if (key.Contains("Due date", StringComparison.OrdinalIgnoreCase)) dueDate  = value;
                        }
                    }

                    var itemsTable = tables[4];
                    var items = new List<LeadItemData>();
                    var rows = itemsTable.Elements<TableRow>().ToList();
                    _logger.LogInformation("Processing {RowCount} rows from items table", rows.Count);

                    for (int i = 0; i < rows.Count; i++)
                    {
                        var cells    = rows[i].Elements<TableCell>().ToList();
                        if (cells.Count == 0) continue;
                        var cellText = GetCellText(cells[0]).Trim();

                        var itemHeaderMatch = Regex.Match(cellText, @"^(\d+\.\d+)\s+(.+)$");
                        if (itemHeaderMatch.Success)
                        {
                            var lineItemNo = itemHeaderMatch.Groups[1].Value;
                            var shortName  = itemHeaderMatch.Groups[2].Value.Trim();

                            string fullDescription = null, materialNumber = null,
                                   quantityStr = null, uom = null,
                                   manufacturerRef = null, packingSpec = null;
                            int quantity = 0;

                            int rowOffset = 1;
                            while (i + rowOffset < rows.Count && rowOffset < 30)
                            {
                                var nextCells = rows[i + rowOffset].Elements<TableCell>().ToList();
                                if (nextCells.Count == 0) { rowOffset++; continue; }

                                var fieldName  = GetCellText(nextCells[0]).Trim();
                                string fieldValue = nextCells.Count >= 3 ? GetCellText(nextCells[2]).Trim() : "";

                                if (Regex.IsMatch(fieldName, @"^\d+\.\d+\s+")) break;

                                if (rowOffset == 1 && !fieldName.Contains("Material Number"))
                                    fullDescription = fieldName;
                                else if (fieldName.Contains("Material Number", StringComparison.OrdinalIgnoreCase))
                                    materialNumber = fieldValue;
                                else if (fieldName.Contains("Quantity", StringComparison.OrdinalIgnoreCase))
                                {
                                    quantityStr = fieldValue;
                                    var qm = Regex.Match(quantityStr, @"(\d+)\s*(\w+)");
                                    if (qm.Success) { int.TryParse(qm.Groups[1].Value, out quantity); uom = qm.Groups[2].Value; }
                                }
                                else if (fieldName.Contains("Manufacturer Reference", StringComparison.OrdinalIgnoreCase))
                                    manufacturerRef = fieldValue;
                                else if (fieldName.Contains("Packing Specs One", StringComparison.OrdinalIgnoreCase))
                                    packingSpec = fieldValue;

                                rowOffset++;
                            }

                            string manufacturer = null, partNumber = null;
                            if (!string.IsNullOrEmpty(manufacturerRef))
                            {
                                var mfr = ExtractManufacturerFromAramcoRef(manufacturerRef);
                                manufacturer = mfr.manufacturer;
                                partNumber   = mfr.partNumber;
                            }

                            if (!string.IsNullOrEmpty(materialNumber) && quantity > 0)
                            {
                                items.Add(new LeadItemData(
                                    owner, 0.9, null, 0,
                                    fileName, 0.95,
                                    materialNumber, 0.98,
                                    null, 0,
                                    owner, 0.9,
                                    lineItemNo, 0.98,
                                    shortName, 0.9,
                                    null, 0,
                                    fullDescription ?? shortName, 0.85,
                                    currency, string.IsNullOrEmpty(currency) ? 0 : 0.9,
                                    uom, string.IsNullOrEmpty(uom) ? 0 : 0.95,
                                    null, 0,
                                    quantity, 0.98,
                                    packingSpec, string.IsNullOrEmpty(packingSpec) ? 0 : 0.8,
                                    manufacturer, string.IsNullOrEmpty(manufacturer) ? 0 : 0.9,
                                    partNumber, string.IsNullOrEmpty(partNumber) ? 0 : 0.85,
                                    null, 0,
                                    null, 0,
                                    manufacturerRef, string.IsNullOrEmpty(manufacturerRef) ? 0 : 0.9,
                                    null, 0,
                                    null, 0,
                                    publishDate, string.IsNullOrEmpty(publishDate) ? 0 : 0.9,
                                    dueDate, string.IsNullOrEmpty(dueDate) ? 0 : 0.9,
                                    0.88));
                            }

                            i += (rowOffset - 1);
                        }
                    }

                    _logger.LogInformation("Extracted {ItemCount} items from Aramco document", items.Count);

                    var remarks = new StringBuilder();
                    remarks.AppendLine("Aramco RFP Processing");
                    remarks.AppendLine($"Owner: {owner}");
                    remarks.AppendLine($"Event Type: {eventType}");
                    remarks.AppendLine($"Currency: {currency}");
                    remarks.AppendLine($"Publish Date: {publishDate}");
                    remarks.AppendLine($"Due Date: {dueDate}");
                    remarks.AppendLine($"Total Items: {items.Count}");

                    return new LeadExtractionResult(
                        fileName, 0.95,
                        owner, string.IsNullOrEmpty(owner) ? 0 : 0.9,
                        publishDate, string.IsNullOrEmpty(publishDate) ? 0 : 0.9,
                        dueDate, string.IsNullOrEmpty(dueDate) ? 0 : 0.9,
                        null, 0, null, 0,
                        publishDate, string.IsNullOrEmpty(publishDate) ? 0 : 0.9,
                        remarks.ToString(), 0.9,
                        null, 0,
                        eventType ?? "RFP", 0.9,
                        null, 0,
                        0.9, items);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Aramco RFP document: {FileName}", fileName);
                return null;
            }
        }

        private (string manufacturer, string partNumber) ExtractManufacturerFromAramcoRef(string manufacturerRef)
        {
            if (string.IsNullOrEmpty(manufacturerRef))
                return (null, null);

            string manufacturer = null;
            string partNumber   = null;

            try
            {
                var mfrMatch = Regex.Match(manufacturerRef,
                    @"\d{10}\s+\d{8}\s+([A-Z][A-Z0-9\s\-&/\.;,]+?)\s+(US|SA|GB|DE|FR|IT|JP|CN|IN|AE|KR|NL|CH|SE|NO|DK|FI|BE|AT|ES|PT|IE|PL|CZ|HU|RO|BG|GR|TR|IL|ZA|AU|NZ|CA|MX|BR|AR|CL|CO|PE|VE|EG|NG|KE|MA|DZ|TN|LY|SD|ET|UG|TZ|GH|SN|CI|CM|AO|ZM|ZW|MW|MZ|BW|NA|LS|SZ)\s+",
                    RegexOptions.IgnoreCase);

                if (mfrMatch.Success)
                {
                    manufacturer = mfrMatch.Groups[1].Value.Trim();
                    manufacturer = Regex.Replace(manufacturer, @"[;,\s]+$", "");
                }

                var pnMatch = Regex.Match(manufacturerRef,
                    @"(?:PART\s+NUMB(?:ER)?|NUMBER)\s+([A-Z0-9\-/\.]+)",
                    RegexOptions.IgnoreCase);

                if (pnMatch.Success)
                    partNumber = pnMatch.Groups[1].Value.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract manufacturer from Aramco ref");
            }

            return (manufacturer, partNumber);
        }

        private string GetCellText(TableCell cell)
        {
            var texts = new List<string>();
            foreach (var text in cell.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>())
                texts.Add(text.Text);
            return string.Join("", texts);
        }

        /// <summary>
        /// Persists watched-folder attachments through <see cref="ILegacyDocumentStore"/> (disk
        /// or object store on one switch). NOTE: no caller remains in this class since the watched
        /// folder was rerouted through the unified document queue; converted with the other
        /// three doors so the switch is complete, not because it is reached today.
        /// </summary>
        private async Task SaveAttachmentsAsync(List<(string FileName, MemoryStream Stream, string Extension, string OriginalPath)> files, long leadId)
        {
            var businessUnitId = await _context.Leads.IgnoreQueryFilters()
                .Where(l => l.Id == leadId).Select(l => l.BusinessUnitId).SingleAsync();

            foreach (var item in files)
            {
                var safeName = SanitizeFileName(item.FileName);
                var fileName = $"{leadId}_{Guid.NewGuid()}_{safeName}";
                try
                {
                    item.Stream.Position = 0;
                    var bytes = item.Stream.ToArray();
                    var stored = await _legacyDocuments.StoreAttachmentAsync(
                        businessUnitId, LegacyDocumentFolders.WatchedFolderAttachments, fileName, bytes);

                    _context.Attachments.Add(new Attachment
                    {
                        ParentType   = "Lead",
                        ParentId     = leadId,
                        FileName     = safeName,
                        FilePath     = stored.FilePath,
                        MimeType     = GetMimeType(item.Extension),
                        FileSize     = stored.ByteSize,
                        ContentType  = GetContentType(item.Extension),
                        ContentSha256 = stored.ContentSha256,
                        CreatedOn    = DateTime.UtcNow,
                        UploadedDate = DateTime.UtcNow
                    });

                    var processedPath = Path.Combine(_processedFolderPath, item.FileName);
                    File.Move(item.OriginalPath, processedPath, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save/move attachment: {FileName}", safeName);
                }
            }
        }

        private string BuildHeaderRemarks(LeadExtractionResult ai, string extracted)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SEC Folder Processing");
            sb.AppendLine();
            sb.AppendLine("Extraction Summary:");
            sb.AppendLine($"RFQ No: {ai.Rfqno}");
            sb.AppendLine($"Buyer: {ai.BuyersName}");
            sb.AppendLine($"Items Count: {ai.Items.Count}");
            sb.AppendLine($"Items with Manufacturer: {ai.Items.Count(x => !string.IsNullOrEmpty(x.ManufacturerName))}");
            sb.AppendLine();
            sb.AppendLine("Note: Manufacturer information may not be present in original RFQ documents.");
            sb.AppendLine("SEC typically requires vendors to provide manufacturer details in their response.");
            return sb.ToString();
        }

        // DRIFT GUARD: shared with the email, manual-upload and async-worker doors — see
        // LeadItemMapper. This door's date reader accepts several extra document conventions
        // ("12 Mar 2024"), which is the only part that is genuinely door-specific.
        private LeadItem CreateLeadItem(long leadId, LeadItemData aiItem)
            => LeadItemMapper.Map(aiItem, ParseDate, leadId);

        /// <summary>
        /// A human label for the format a folder document arrived in. It is shown to reviewers
        /// as the Lead's source, so it names the FORMAT and never a customer: ".doc" used to
        /// read "SEC Word Document", which mislabelled every legacy Word file that came from
        /// anyone else — and the Aramco corpus is entirely .doc.
        ///
        /// <para>Every extension the folder door now accepts has an entry. "Unknown" previously
        /// covered everything but Word, so the moment the door widened it would have become the
        /// most common answer on the screen.</para>
        /// </summary>
        private string GetFileTypeLabel(string ext) => ext switch
        {
            ".doc"                  => "Word Document (legacy)",
            ".docx"                 => "Word Document",
            ".pdf"                  => "PDF Document",
            ".xls"                  => "Excel Workbook (legacy)",
            ".xlsx" or ".xlsm"      => "Excel Workbook",
            ".csv"                  => "CSV Spreadsheet",
            ".txt"                  => "Text Document",
            ".htm" or ".html"       => "Web Page",
            ".eml" or ".msg"        => "Email Message",
            ".png" or ".jpg" or ".jpeg" or ".gif"
                or ".bmp" or ".tif" or ".tiff" or ".webp" => "Scanned Image",
            _                       => "Document"
        };

        private bool IsSupportedExtension(string ext) => ext == ".doc";

        private string SanitizeFileName(string fileName) =>
            string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(),
                StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");

        private string? GetMimeType(string ext) => ext switch
        {
            ".doc"  => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _       => "application/octet-stream"
        };

        private string? GetContentType(string ext) => ext switch
        {
            ".doc"  => "application",
            ".docx" => "application",
            _       => null
        };

        private string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        // Shared with every other ingestion door — see RfqDateParser. The embedded-date search
        // this method used to do inline is now part of the shared parser, so the spelled-month
        // forms it accepted are available to the email and upload doors too.
        private DateTime? ParseDate(string? s) => Extraction.RfqDateParser.Parse(s);

        private LeadExtractionResult BuildExtraction(string text)
        {
            // Normalize line endings
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            
            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .Where(l => !IsGarbageLine(l))  // Filter garbage early
                .ToList();

            _logger.LogInformation("BuildExtraction: Processing {LineCount} clean lines from SEC document", lines.Count);

            int vendorCodeIdx = lines.FindIndex(l => l.Contains("Vendor Code"));
            int buyerIdx      = lines.FindIndex(l => l.Contains("Buyer") && !l.Contains("Tel"));

            string vendorCode = null, vendorName = null, bidNo = null,
                   bidDate    = null, bidClose    = null, buyerName  = null;
            string rfqType    = "Low Value Bid";

            var bidTypeMatch = Regex.Match(text, @"Bid\s+Materials\s+List\s*\(\s*([^)]+?)\s*\)", RegexOptions.IgnoreCase);
            if (bidTypeMatch.Success)
                rfqType = bidTypeMatch.Groups[1].Value.Trim();

            if (vendorCodeIdx >= 0)
            {
                for (int i = vendorCodeIdx + 1; i < Math.Min(vendorCodeIdx + 10, lines.Count); i++)
                {
                    var line = lines[i];
                    if (vendorCode == null && Regex.IsMatch(line, @"^\d{7,}$"))
                    {
                        vendorCode = line; continue;
                    }
                    if (vendorName == null && Regex.IsMatch(line, @"^[A-Z].*[A-Z&]", RegexOptions.IgnoreCase)
                        && !line.StartsWith("C") && !Regex.IsMatch(line, @"^\d"))
                    {
                        vendorName = line; continue;
                    }
                    if (bidNo == null && Regex.IsMatch(line, @"^C\d{9,}$"))
                    {
                        bidNo = line; continue;
                    }
                    if (Regex.IsMatch(line, @"^\d{1,2}/\d{1,2}/\d{4}$"))
                    {
                        if (bidDate == null) bidDate = line;
                        else if (bidClose == null) bidClose = line;
                    }
                }
            }

            if (buyerIdx >= 0)
            {
                for (int i = buyerIdx + 1; i < Math.Min(buyerIdx + 10, lines.Count); i++)
                {
                    var line = lines[i];
                    if (line.Contains("Buyer Tel") || line.Contains("Bid Line") || line.Contains("Item No")) continue;
                    if (line.Contains("Saudi") || line.Contains("Arabia") || line.Equals("Address", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Regex.IsMatch(line, @"^\d+-\d+-?$") || Regex.IsMatch(line, @"^\d+\s*-\s*\d+")) continue;
                    if (Regex.IsMatch(line, @"^[\dA-Z].*[-A-Z]", RegexOptions.IgnoreCase) && line.Length > 3)
                    {
                        buyerName = line; break;
                    }
                }
            }

            _logger.LogInformation("SEC Header: BidNo={BidNo}, Buyer={Buyer}, Vendor={Vendor}",
                bidNo ?? "N/A", buyerName ?? "N/A", vendorName ?? "N/A");

            string remarks = "";
            var remarksMatch = Regex.Match(text, @"For Foreign Suppliers.*?Packing list\.", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (remarksMatch.Success)
                remarks = remarksMatch.Value.Trim();

            var items = new List<LeadItemData>();
            int itemsStartIdx = lines.FindIndex(l => l.Contains("Resp Qty") || l.Contains("Bid Line"));
            
            if (itemsStartIdx >= 0)
            {
                itemsStartIdx = lines.FindIndex(itemsStartIdx, l => Regex.IsMatch(l, @"^\d{1,3}$"));

                if (itemsStartIdx >= 0)
                {
                    int itemsParsed = 0;
                    
                    for (int i = itemsStartIdx; i < lines.Count; )
                    {
                        var line = lines[i];
                        
                        if (!Regex.IsMatch(line, @"^\d{1,3}$")) { i++; continue; }

                        string lineItemNo = line;
                        string materialCode = null;
                        string uom = null;
                        int quantity = 0;
                        var descriptionLines = new List<string>();

                        i++;
                        int fieldCount = 0;
                        
                        while (i < lines.Count && fieldCount < 15)
{
    line = lines[i];

    // Column order in the document is:
    // Bid Line | Item No (9-digit) | Ship To (4-digit location) | Req Unit | Req Qty | Resp Qty
    // We must find UOM before accepting any number as quantity.

    // 1. Material code: exactly 9 digits
    if (materialCode == null && Regex.IsMatch(line, @"^\d{9}$"))
    {
        materialCode = line; fieldCount++; i++; continue;
    }

    // 2. Ship To location code: 3-5 digit number that appears BEFORE UOM
    //    Skip it explicitly so it never gets mistaken for quantity.
    if (uom == null && Regex.IsMatch(line, @"^\d{3,5}$"))
    {
        // This is the "Ship To" field — skip it silently
        fieldCount++; i++; continue;
    }

    // 3. Unit of measure — must come before quantity
    if (uom == null && Regex.IsMatch(line, @"^(EA|ST|ASM|PC|SET|KIT|MT|LT|M|TON|KG|L|NOS|BOX|RL|PR)$",
            RegexOptions.IgnoreCase))
    {
        uom = line; fieldCount++; i++; continue;
    }

    // 4. Quantity — only accept AFTER UOM has been found to avoid Ship To confusion
    if (uom != null && quantity == 0 && Regex.IsMatch(line, @"^\d+$") && line.Length <= 6)
    {
        int.TryParse(line, out quantity); fieldCount++; i++;
        // Skip "Resp Qty" (response quantity column — usually blank but sometimes a number)
        if (i < lines.Count && Regex.IsMatch(lines[i], @"^\d+$") && lines[i].Length <= 6)
            i++;
        break;
    }

    i++;
}
                        while (i < lines.Count)
                        {
                            line = lines[i];
                            
                            if (Regex.IsMatch(line, @"^\d{1,3}$") && i + 1 < lines.Count
                                && Regex.IsMatch(lines[i + 1], @"^\d{9}$"))
                                break;
                            
                            if (IsGarbageLine(line))
                            {
                                _logger.LogDebug("Skipping garbage line in item {ItemNo}: {Line}", lineItemNo, line.Length > 50 ? line.Substring(0, 50) : line);
                                i++;
                                continue;
                            }
                            
                            descriptionLines.Add(line);
                            i++;
                        }

                        string description = string.Join(" ", descriptionLines).Trim();
                        string shortName = description.Length > 100 ? description.Substring(0, 100) : description;
                        
                        int firstSemi = description.IndexOf(';');
                        if (firstSemi > 0 && firstSemi < 200)
                            shortName = description.Substring(0, firstSemi).Trim();

                        var mfr = ExtractManufacturerFromDescription(description);

                if (!string.IsNullOrEmpty(materialCode) && quantity > 0)
{
    items.Add(new LeadItemData(
        vendorName, 0.9,
        vendorCode, string.IsNullOrEmpty(vendorCode) ? 0 : 0.9,
        bidNo, string.IsNullOrEmpty(bidNo) ? 0 : 0.9,
        materialCode, 0.95,
        null, 0,
        buyerName, string.IsNullOrEmpty(buyerName) ? 0 : 0.9,
        lineItemNo, 0.95,
        shortName, 0.85,
        null, 0,
        description, 0.9,
        null, 0,
        uom, string.IsNullOrEmpty(uom) ? 0 : 0.95,
        null, 0,
        quantity, 0.95,
        null, 0,
        mfr.manufacturer, mfr.manufacturer != null ? 0.9 : 0,
        mfr.partNumber, mfr.partNumber != null ? 0.9 : 0,
        null, 0,
        mfr.altPartNumber, mfr.altPartNumber != null ? 0.85 : 0,
        null, 0,
        null, 0,
        null, 0,
        // ── NEW: ReceivedDate (per-item) ──
        null, 0,
        // BidClosingDateLine (using the header bidClose date for all items)
        bidClose, bidClose != null ? 0.9 : 0,
        0.85));   // Overall Item Confidence

    itemsParsed++;

    _logger.LogDebug("Parsed item {ItemNo}: {MaterialCode}, Qty={Qty}, Desc={Desc}",
        lineItemNo, materialCode, quantity,
        shortName.Length > 60 ? shortName.Substring(0, 60) + "..." : shortName);
}
                    }

                    _logger.LogInformation("SEC Items: {ItemCount} parsed, {WithMfg} with manufacturer",
                        itemsParsed, items.Count(x => !string.IsNullOrEmpty(x.ManufacturerName)));
                }
                else
                {
                    _logger.LogWarning("Could not find first item number after 'Resp Qty'");
                }
            }
            else
            {
                _logger.LogWarning("Could not find 'Resp Qty' or 'Bid Line' header");
            }

            return new LeadExtractionResult(
                bidNo, bidNo != null ? 0.95 : 0.0,
                buyerName ?? vendorName, buyerName != null ? 0.9 : 0.7,
                bidDate, bidDate != null ? 0.9 : 0.0,
                bidClose, bidClose != null ? 0.9 : 0.0,
                null, 0.0,
                null, 0.0,
                bidDate, bidDate != null ? 0.9 : 0.0,
                remarks, remarks != null ? 0.8 : 0.0,
                null, 0,
                rfqType, 0.9,
                null, 0,
                0.85,
                items);
        }

        private (string manufacturer, string partNumber, string altPartNumber) ExtractManufacturerFromDescription(string description)
        {
            if (string.IsNullOrEmpty(description))
                return (null, null, null);

            string manufacturer = null, partNumber = null, altPartNumber = null;

            try
            {
                var m1 = Regex.Match(description,
                    @"MANUFACTURER[:\s]+([A-Z][A-Z0-9\s\-&/\.;,]+?)(?:\s*;|\s+P/N|\s+PART|\s+MFG|\s+MODEL|\s*$)",
                    RegexOptions.IgnoreCase);
                if (m1.Success)
                    manufacturer = Regex.Replace(m1.Groups[1].Value.Trim(), @"[;,\s]+$", "");

                if (string.IsNullOrEmpty(manufacturer))
                {
                    var m2 = Regex.Match(description,
                        @"(?:MFG|MFR)[:\s]+([A-Z][A-Z0-9\s\-&/\.;,]+?)(?:\s*;|\s+P/N|\s+PART|\s+MODEL|\s*$)",
                        RegexOptions.IgnoreCase);
                    if (m2.Success)
                        manufacturer = Regex.Replace(m2.Groups[1].Value.Trim(), @"[;,\s]+$", "");
                }

                if (string.IsNullOrEmpty(manufacturer))
                {
                    var m3 = Regex.Match(description, @"([A-Z][A-Z\s\-&]+)\s+P\s*/\s*N", RegexOptions.IgnoreCase);
                    if (m3.Success)
                    {
                        var p = m3.Groups[1].Value.Trim();
                        if (p.Length >= 2 && p.Length <= 50
                            && !p.Contains("SPECIFICATION", StringComparison.OrdinalIgnoreCase)
                            && !p.Contains("MATERIAL", StringComparison.OrdinalIgnoreCase))
                            manufacturer = p;
                    }
                }

                var pnMatches = Regex.Matches(description,
                    @"(?:P\s*/\s*N|PART\s+(?:NUMBER|NO|#))[:\s]*([A-Z0-9\-/\.]+)",
                    RegexOptions.IgnoreCase);
                if (pnMatches.Count > 0)
                {
                    partNumber = pnMatches[0].Groups[1].Value.Trim();
                    if (pnMatches.Count > 1)
                        altPartNumber = pnMatches[1].Groups[1].Value.Trim();
                }

                if (string.IsNullOrEmpty(partNumber))
                {
                    var mm = Regex.Match(description,
                        @"MODEL\s+(?:NO|#)?[:\s]*([A-Z0-9\-/\.]+)", RegexOptions.IgnoreCase);
                    if (mm.Success)
                        partNumber = mm.Groups[1].Value.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract manufacturer from description");
            }

            return (manufacturer, partNumber, altPartNumber);
        }

        /// <summary>
        /// Enhanced garbage line detection
        /// </summary>
        private static bool IsGarbageLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length < 3) return false;

            // Known Word format-code patterns
            string[] garbagePatterns = {
                "OJPJQJCJ", "OJPJQJCj", "56phOJPJ", "56ph",
                "$If$If", "Ö$$If", "ÖrÄ½", "tQÄ½", "VUVÄ½",
                "ÿÿÿÿ", "24FHLNPRTVXZjlwxue", "56phnOJPJ"
            };

            foreach (var pattern in garbagePatterns)
                if (line.Contains(pattern, StringComparison.Ordinal))
                    return true;

            // Repeating character patterns
            if (Regex.IsMatch(line, @"[bdfhjlnprtvxz]{6,}", RegexOptions.IgnoreCase))
                return true;

            // High non-ASCII ratio (40%+)
            int nonAscii = line.Count(c => c > 0x7E);
            if (line.Length > 5 && nonAscii * 100 / line.Length > 40)
                return true;

            // Short repeating patterns
            if (Regex.IsMatch(line, @"(.{3,8})\1{2,}"))
                return true;

            // Just special characters
            if (Regex.IsMatch(line, @"^[\x00-\x1F\x7F-\x9F\xA0-\xBF]{3,}$"))
                return true;

            return false;
        }

        private string GetFolderPath(string folderType) => folderType switch
        {
            "SEC"       => _secFolderPath,
            "Customer1" => _secFolderPath,
            "Aramco"    => _aramcoFolderPath,
            "Customer2" => _aramcoFolderPath,
            _           => _sharedFolderPath
        };

        private string GetTenantFolderPath(long businessUnitId, string folderType)
        {
            var folder = folderType.Trim().ToUpperInvariant() switch
            {
                "SHARED" => "Shared",
                "SEC" or "CUSTOMER1" => "SEC",
                "ARAMCO" or "CUSTOMER2" => "Aramco",
                _ => throw new ArgumentException("Folder type must be Shared, SEC, or Aramco.", nameof(folderType))
            };
            return _storage.GetPath(
                "Tenants", businessUnitId.ToString(CultureInfo.InvariantCulture), "Watched", folder);
        }

        /// <summary>
        /// What a watched folder accepts: EXACTLY what every other intake door accepts.
        ///
        /// <para>This is the allow-list object itself, not a copy of it, so the folder door
        /// cannot drift from inspection and from the extraction reader the way it previously
        /// did.</para>
        ///
        /// <para><b>Why the per-folder lists went away.</b> Each folder used to carry its own
        /// narrow filter — SEC accepted only <c>.doc</c>, Aramco only <c>.docx</c>, Shared five
        /// document types — on the reasoning that a customer-specific door should be tight. In
        /// practice it silently discarded real work: every Aramco bid list in the live corpus is
        /// a <c>.doc</c>, so the folder named after that customer rejected that customer's own
        /// documents, and the file was skipped with nothing raised to anyone.</para>
        ///
        /// <para><b>Why the extension gate was not protecting anything.</b> A filename is a
        /// claim, and this gate believed it in both directions: a hostile <c>.exe</c> renamed to
        /// <c>.doc</c> passed it, and a genuine <c>.doc</c> named <c>.docx</c> was refused. The
        /// gate that actually decides is <c>DocumentFileInspectionService</c>, which types every
        /// file from its BYTES and is unaffected by this change. Removing a filter that a rename
        /// defeats costs no security and stops discarding readable documents.</para>
        ///
        /// <para>Narrowing a door again is a legitimate decision, but it belongs in tenant
        /// configuration where a customer can see and change it — not compiled into three
        /// hard-coded lists whose disagreement is invisible until a bid goes missing.</para>
        /// </summary>
        internal static readonly IReadOnlySet<string> WatchedFolderExtensions =
            ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList.Extensions;

        /// <summary>
        /// What may be uploaded INTO a watched folder. The folder chosen says who the document
        /// is from; it does not say what a document is allowed to be, so every folder takes the
        /// same set — the one the sweep will later read. When these two disagreed, a file could
        /// be accepted by the upload and then skipped for ever by the sweep that followed it.
        /// </summary>
        private static bool IsAllowedUploadExtension(string folderType, string extension)
            => folderType.Trim().ToUpperInvariant() is "SEC" or "CUSTOMER1"
                or "ARAMCO" or "CUSTOMER2" or "SHARED"
               && WatchedFolderExtensions.Contains(extension);

        public IReadOnlyList<long> DiscoverTenantFolderIds()
        {
            var tenantsRoot = _storage.GetPath("Tenants");
            if (!Directory.Exists(tenantsRoot)) return Array.Empty<long>();
            return Directory.GetDirectories(tenantsRoot)
                .Select(Path.GetFileName)
                .Select(x => long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
        }

        /// <summary>
        /// Records one staging failure and returns the running attempt count.
        ///
        /// The counter lives in the DATABASE (FolderIngestionRetryStates), not in a
        /// "&lt;file&gt;.nexora-retry.json" sidecar next to the document. On Render the
        /// upload root is an ephemeral, per-instance disk, so sidecars reset the counter
        /// on every restart (a poison document could retry forever) and were invisible to
        /// every other instance (the three-strikes quarantine rule was per-instance).
        /// </summary>
        private async Task<int> RecordRetryAsync(
            long businessUnitId,
            string sourceLabel,
            string fileName,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var key = RetryKey(fileName);
            try
            {
                var now = DateTime.UtcNow;
                var state = await _context.Set<FolderIngestionRetryState>()
                    .FirstOrDefaultAsync(
                        x => x.BusinessUnitId == businessUnitId
                             && x.SourceLabel == sourceLabel
                             && x.FileName == key,
                        cancellationToken);

                if (state is null)
                {
                    state = new FolderIngestionRetryState
                    {
                        BusinessUnitId = businessUnitId,
                        SourceLabel = sourceLabel,
                        FileName = key,
                        Attempts = 1,
                        LastErrorType = exception.GetType().Name,
                        FirstFailedOn = now,
                        LastFailedOn = now
                    };
                    _context.Set<FolderIngestionRetryState>().Add(state);
                }
                else
                {
                    state.Attempts++;
                    state.LastErrorType = exception.GetType().Name;
                    state.LastFailedOn = now;
                }

                await _context.SaveChangesAsync(cancellationToken);
                return state.Attempts;
            }
            catch (Exception persistException) when (persistException is not OperationCanceledException)
            {
                // The retry ledger must never be the reason a sweep dies. Falling back to
                // "one attempt" keeps the file in place for the next sweep instead of
                // quarantining a document we could not account for.
                _logger.LogWarning(persistException,
                    "Could not persist folder retry state for {FileName}; treating as a single attempt.", fileName);
                return 1;
            }
        }

        private async Task ClearRetryStateAsync(
            long businessUnitId, string sourceLabel, string fileName, CancellationToken cancellationToken)
        {
            var key = RetryKey(fileName);
            try
            {
                await _context.Set<FolderIngestionRetryState>()
                    .Where(x => x.BusinessUnitId == businessUnitId
                                && x.SourceLabel == sourceLabel
                                && x.FileName == key)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not clear folder retry state for {FileName}.", fileName);
            }
        }

        private static string RetryKey(string fileName)
        {
            var name = Path.GetFileName(fileName);
            return name.Length <= 400 ? name : name[^400..];
        }

        private async Task QuarantineAsync(
            string filePath,
            long businessUnitId,
            string sourceLabel,
            string reason,
            int attempts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath)) return;
            _storage.ResolvePath(Path.GetDirectoryName(filePath)!);
            var folderName = sourceLabel.Replace(' ', '_');
            var quarantineFolder = _storage.GetPath(
                "Tenants", businessUnitId.ToString(CultureInfo.InvariantCulture), "Quarantine", folderName);
            Directory.CreateDirectory(quarantineFolder);
            var quarantineName = $"{Guid.NewGuid():N}_{Path.GetFileName(filePath)}";
            var quarantinePath = Path.Combine(quarantineFolder, quarantineName);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                businessUnitId,
                source = sourceLabel,
                originalFileName = Path.GetFileName(filePath),
                quarantinedFileName = quarantineName,
                reason,
                attempts,
                quarantinedAt = DateTime.UtcNow
            });
            var metadataPath = quarantinePath + ".json";
            var stagedMetadataPath = metadataPath + ".pending";
            await File.WriteAllBytesAsync(stagedMetadataPath, metadata, cancellationToken);
            try
            {
                File.Move(filePath, quarantinePath, false);
                File.Move(stagedMetadataPath, metadataPath, false);
                DeleteLegacyRetrySidecar(filePath);
            }
            catch
            {
                // A .pending manifest is intentionally retained for the scheduled sweep
                // or an operator to reconcile if the document move already occurred.
                throw;
            }
        }

        /// <summary>
        /// Removes a legacy on-disk retry sidecar if one is still present from before the
        /// counter moved into the database. Best-effort: the file is on ephemeral storage
        /// and may already be gone.
        /// </summary>
        private static void DeleteLegacyRetrySidecar(string filePath)
        {
            try
            {
                var statePath = filePath + ".nexora-retry.json";
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void RecoverStaleClaims(string processingFolder, string watchedFolder)
        {
            var staleBefore = DateTime.UtcNow.AddMinutes(-5);
            foreach (var path in Directory.GetFiles(processingFolder))
            {
                if (File.GetLastWriteTimeUtc(path) >= staleBefore) continue;
                var name = Path.GetFileName(path);
                var destination = Path.Combine(watchedFolder, name);
                if (File.Exists(destination))
                    destination = Path.Combine(watchedFolder, $"{Guid.NewGuid():N}_{name}");
                try
                {
                    File.Move(path, destination, false);
                }
                catch (FileNotFoundException) when (!File.Exists(path))
                {
                    // Another worker recovered the same stale claim after enumeration.
                }
            }
        }

        private static StringComparison PathComparison()
            => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
