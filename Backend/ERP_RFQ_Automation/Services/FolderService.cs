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

namespace ERP_RFQ_Automation.Services
{
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
        private const long MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024; // 25 MB

        // ING-05: when true (default) folder files are enqueued as durable extraction
        // jobs (one per file) instead of the in-place SEC/Aramco parsers below.
        // Config: Ingestion:UseUnifiedQueue (set false to restore the legacy parsers).
        private readonly bool _useUnifiedQueue;
        private readonly ERP_RFQ_Automation.Extraction.IDocumentIngestion? _ingestion;

        public FolderService(
            ErpRfqAutomationContext context,
            IWebHostEnvironment env,
            ILogger<FolderService> logger,
            ILLMService llmService,
            IConfiguration configuration,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion? ingestion = null)
        {
            _context = context;
            _env = env;
            _logger = logger;
            _llmService = llmService;
            _useUnifiedQueue = configuration.GetValue("Ingestion:UseUnifiedQueue", true);
            _ingestion = ingestion;
            _sharedFolderPath = Path.Combine(_env.ContentRootPath, "Uploads", "Shared_Leads_Folder");
            _secFolderPath = Path.Combine(_env.ContentRootPath, "Uploads", "SEC_Leads_Folder");
            _aramcoFolderPath = Path.Combine(_env.ContentRootPath, "Uploads", "Aramco_Leads_Folder");
            _processedFolderPath = Path.Combine(_env.ContentRootPath, "Uploads", "Processed_Leads_Folder");
            _attachmentPath = Path.Combine(_env.ContentRootPath, "Uploads", "Leads_Folder_Attachments");
            
            Directory.CreateDirectory(_sharedFolderPath);
            Directory.CreateDirectory(_secFolderPath);
            Directory.CreateDirectory(_aramcoFolderPath);
            Directory.CreateDirectory(_processedFolderPath);
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

        public async Task SaveFilesToSharedFolderAsync(List<Microsoft.AspNetCore.Http.IFormFile> files, string folderType)
        {
            var targetFolder = GetFolderPath(folderType);
            
            foreach (var file in files)
            {
                if (file.Length > 0)
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
                    var filePath = Path.Combine(targetFolder, safeName);
                    var fullTarget = Path.GetFullPath(targetFolder);
                    var fullPath = Path.GetFullPath(filePath);
                    if (!fullPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Rejected path-traversal filename '{FileName}'.", file.FileName);
                        continue;
                    }
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    _logger.LogInformation("Saved file {FileName} to folder {FolderType}.", safeName, folderType);
                }
            }
        }

        public async Task ProcessAllFoldersAsync()
        {
            await ProcessSECFoldersAsync();
            await ProcessAramcoFoldersAsync();
        }

        /// <summary>
        /// ING-05: routes one watched folder through the unified extraction queue —
        /// each matching file becomes its own content-addressed job (shared batch); the
        /// original is moved to the Processed folder once enqueued (the queue holds an
        /// immutable copy). Per-file failures are isolated and the file is LEFT IN PLACE
        /// so the next run retries it; re-enqueueing already-seen content is a no-op via
        /// the (BusinessUnitId, ContentHash) idempotency.
        /// </summary>
        private async Task EnqueueFolderFilesAsync(string folder, string leadSourceLabel, Func<string, bool> extFilter)
        {
            var filePaths = Directory.GetFiles(folder);
            if (!filePaths.Any())
            {
                _logger.LogInformation("No files found in {Label} folder.", leadSourceLabel);
                return;
            }

            var defaultConfig = await _context.EmailConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.IsActive);
            if (defaultConfig == null)
            {
                _logger.LogWarning("No active email configuration found for {Label} folder processing. Aborting.", leadSourceLabel);
                return;
            }

            var batchId = Guid.NewGuid();
            foreach (var filePath in filePaths)
            {
                var fileName = Path.GetFileName(filePath);
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (!extFilter(ext))
                {
                    _logger.LogInformation("Skipping unsupported file in {Label} folder: {FileName}", leadSourceLabel, fileName);
                    continue;
                }

                try
                {
                    var bytes = await File.ReadAllBytesAsync(filePath);
                    if (bytes.Length == 0) continue;
                    if (bytes.Length > MAX_ATTACHMENT_SIZE)
                    {
                        _logger.LogWarning("File {FileName} exceeds max size. Skipping.", fileName);
                        continue;
                    }

                    var result = await _ingestion!.IngestAsync(
                        bytes, fileName, defaultConfig.BusinessUnitId,
                        ERP_RFQ_Automation.Extraction.ExtractionSourceType.Folder,
                        batchId, priority: 0,
                        new ERP_RFQ_Automation.Extraction.ExtractionJobMetadata
                        {
                            ClientEmail = "",
                            LeadSource = leadSourceLabel,
                            EmailSource = leadSourceLabel == "Aramco Leads" ? "Aramco RFP Document" : GetFileTypeLabel(ext)
                        });
                    _logger.LogInformation("Enqueued {Label} file {FileName} as job {JobId} ({Outcome}).",
                        leadSourceLabel, fileName, result.JobId, result.Outcome);

                    // The queue owns an immutable copy now — archive the original.
                    var processedPath = Path.Combine(_processedFolderPath, fileName);
                    File.Move(filePath, processedPath, true);
                }
                catch (Exception ex)
                {
                    // Poison-file isolation; the file stays in place for the next run.
                    _logger.LogError(ex, "Failed to enqueue {Label} file {FileName}.", leadSourceLabel, fileName);
                }
            }
        }

        public async Task ProcessAramcoFoldersAsync()
        {
            if (_useUnifiedQueue && _ingestion != null)
            {
                await EnqueueFolderFilesAsync(_aramcoFolderPath, "Aramco Leads", ext => ext == ".docx");
                return;
            }

            var targetFolder = _aramcoFolderPath;
            _logger.LogInformation("Processing Aramco folder: {Path}", targetFolder);
            
            var filePaths = Directory.GetFiles(targetFolder);
            if (!filePaths.Any())
            {
                _logger.LogInformation("No files found in Aramco folder.");
                return;
            }

            var defaultConfig = await _context.EmailConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.IsActive);

            if (defaultConfig == null)
            {
                _logger.LogWarning("No active email configuration found for Aramco folder processing. Aborting.");
                return;
            }

            foreach (var filePath in filePaths)
            {
                var fileName = Path.GetFileName(filePath);
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                
                if (ext != ".docx")
                {
                    _logger.LogInformation("Skipping non-docx file: {FileName}", fileName);
                    continue;
                }

                _logger.LogInformation("Processing Aramco file: {FileName}", fileName);

                try
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath);
                    if (fileBytes.Length > MAX_ATTACHMENT_SIZE)
                    {
                        _logger.LogWarning("File {FileName} exceeds max size. Skipping.", fileName);
                        continue;
                    }

                    LeadExtractionResult extractionResult;
                    using (var ms = new MemoryStream(fileBytes))
                    {
                        extractionResult = ParseAramcoRFP(ms, fileName);
                    }

                    if (extractionResult == null || !extractionResult.Items.Any())
                    {
                        _logger.LogWarning("No items extracted from {FileName}. Skipping.", fileName);
                        continue;
                    }

                    var dummyIngest = new EmailIngest
                    {
                        MessageId = $"AramcoLead_{Guid.NewGuid()}_{fileName}",
                        EmailSubject = $"Aramco RFP: {fileName}",
                        FromEmail = "aramco@system.com",
                        ToEmail = "system@rfq.com",
                        EmailConfigurationId = defaultConfig.Id,
                        CreatedOn = DateTime.UtcNow,
                        ParseStatus = "Pending",
                        RawEmailPath = null
                    };
                    _context.EmailIngests.Add(dummyIngest);
                    await _context.SaveChangesAsync();

                    try
                    {
                        DateTime recDate = ParseDate(extractionResult.RecDate) ?? DateTime.UtcNow;
                        DateTime? bidClosingDate = ParseDate(extractionResult.BidClosingDate);
                        DateTime? acknowledgmentDate = ParseDate(extractionResult.AcknowledgmentDate);
                        DateTime? subDate = ParseDate(extractionResult.SubDate);

                        var items = extractionResult.Items.Where(x => x.Quantity > 0).ToList();

                        var lead = new Lead
                        {
                            Rfqno = Truncate(extractionResult.Rfqno ?? Path.GetFileNameWithoutExtension(fileName), 100),
                            BuyersName = Truncate(extractionResult.BuyersName ?? "Aramco RFP", 255),
                            RecDate = recDate,
                            BidClosingDate = bidClosingDate,
                            BiddingDecision = Truncate(extractionResult.BiddingDecision, 100),
                            AcknowledgmentDate = acknowledgmentDate,
                            SubDate = subDate,
                            HeaderRemarks = Truncate(extractionResult.HeaderRemarks, 8000),
                            OpportunityNo = Truncate(extractionResult.OpportunityNo, 100),
                            NoOfLineItems = items.Count,
                            Rfqtype = Truncate(extractionResult.Rfqtype ?? "RFP", 50),
                            DurationAgreement = Truncate(extractionResult.DurationAgreement, 100),
                            LeadSource = "Aramco Leads",
                            EmailSource = "Aramco RFP Document",
                            Clientemail = "",
                            Aiconfidence = (decimal?)extractionResult.OverallConfidence,
                            CreatedBy = "System",
                            CreatedDate = DateTime.UtcNow,
                            BusinessUnitId = defaultConfig.BusinessUnitId,
                            EmailIngestsId = dummyIngest.Id
                        };

                        _context.Leads.Add(lead);
                        await _context.SaveChangesAsync();

                        int itemsWithManufacturer = 0;
                        foreach (var aiItem in items)
                        {
                            _context.LeadItems.Add(CreateLeadItem(lead.Id, aiItem));
                            if (!string.IsNullOrEmpty(aiItem.ManufacturerName))
                                itemsWithManufacturer++;
                        }

                        if (items.Count > 0)
                            await _context.SaveChangesAsync();

                        var fileStreamData = new List<(string FileName, MemoryStream Stream, string Extension, string OriginalPath)>();
                        fileStreamData.Add((fileName, new MemoryStream(fileBytes), ext, filePath));

                        await SaveAttachmentsAsync(fileStreamData, lead.Id);

                        dummyIngest.ParsedAt = DateTime.UtcNow;
                        dummyIngest.ParseStatus = "Success";
                        await _context.SaveChangesAsync();

                        _logger.LogInformation("Successfully created Aramco lead {LeadId} with {ItemCount} items ({MfgCount} with manufacturer info) from {FileName}.", 
                            lead.Id, items.Count, itemsWithManufacturer, fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process Aramco lead from {FileName}.", fileName);
                        dummyIngest.ParseStatus = "Failed";
                        await _context.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read or parse Aramco file: {FileName}", fileName);
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

        public async Task ProcessSECFoldersAsync()
        {
            if (_useUnifiedQueue && _ingestion != null)
            {
                await EnqueueFolderFilesAsync(_secFolderPath, "SEC Leads", IsSupportedExtension);
                return;
            }

            var targetFolder = _secFolderPath;
            _logger.LogInformation("ProcessSECFoldersAsync started. Folder path: {Path}", Path.GetFullPath(targetFolder));

            var filePaths = Directory.GetFiles(targetFolder);
            _logger.LogInformation("Found {Count} files in folder.", filePaths.Length);

            if (!filePaths.Any())
            {
                _logger.LogInformation("No files found in folder.");
                return;
            }

            var defaultConfig = await _context.EmailConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.IsActive);

            if (defaultConfig == null)
            {
                _logger.LogWarning("No active email configuration found for folder processing. Aborting.");
                return;
            }

            foreach (var filePath in filePaths)
            {
                var fileName = Path.GetFileName(filePath);
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext)) continue;

                _logger.LogInformation("Processing SEC file: {FileName}", fileName);

                try
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath);
                    if (fileBytes.Length > MAX_ATTACHMENT_SIZE)
                    {
                        _logger.LogWarning("File {FileName} exceeds max size. Skipping.", fileName);
                        continue;
                    }

                    string extractedText = "";
                    using (var ms = new MemoryStream(fileBytes))
                    {
                        extractedText = ExtractTextFromDoc(ms);
                    }

                    if (string.IsNullOrWhiteSpace(extractedText))
                    {
                        _logger.LogWarning("No text extracted from {FileName}. Skipping.", fileName);
                        continue;
                    }

                    // Log a sample for debugging
                    var preview = extractedText.Length > 500 ? extractedText.Substring(0, 500) : extractedText;
                    _logger.LogInformation("Extracted text preview: {Preview}...", preview.Replace("\n", " ").Replace("\r", " "));

                    var dummyIngest = new EmailIngest
                    {
                        MessageId = $"SECLead_{Guid.NewGuid()}_{fileName}",
                        EmailSubject = $"SEC Lead: {fileName}",
                        FromEmail = "sec@system.com",
                        ToEmail = "system@rfq.com",
                        EmailConfigurationId = defaultConfig.Id,
                        CreatedOn = DateTime.UtcNow,
                        ParseStatus = "Pending",
                        RawEmailPath = null
                    };
                    _context.EmailIngests.Add(dummyIngest);
                    await _context.SaveChangesAsync();

                    try
                    {
                        var ai = BuildExtraction(extractedText);

                        if (string.IsNullOrWhiteSpace(ai.Rfqno))
                            ai = ai with { Rfqno = Path.GetFileNameWithoutExtension(fileName) };

                        DateTime recDate            = ParseDate(ai.RecDate) ?? DateTime.UtcNow;
                        DateTime? bidClosingDate    = ParseDate(ai.BidClosingDate);
                        DateTime? acknowledgmentDate = ParseDate(ai.AcknowledgmentDate);
                        DateTime? subDate           = ParseDate(ai.SubDate);

                        var items = ai.Items.Where(x => x.Quantity > 0).ToList();

                        var lead = new Lead
                        {
                            Rfqno            = Truncate(ai.Rfqno, 100),
                            BuyersName       = Truncate(ai.BuyersName ?? "SEC", 255),
                            RecDate          = recDate,
                            BidClosingDate   = bidClosingDate,
                            BiddingDecision  = Truncate(ai.BiddingDecision, 100),
                            AcknowledgmentDate = acknowledgmentDate,
                            SubDate          = subDate,
                            HeaderRemarks    = Truncate(BuildHeaderRemarks(ai, extractedText), 8000),
                            OpportunityNo    = Truncate(ai.OpportunityNo, 100),
                            NoOfLineItems    = items.Count,
                            Rfqtype          = Truncate(ai.Rfqtype, 50),
                            DurationAgreement = Truncate(ai.DurationAgreement, 100),
                            LeadSource       = "SEC Leads",
                            EmailSource      = GetFileTypeLabel(ext),
                            Clientemail      = "",
                            Aiconfidence     = (decimal?)ai.OverallConfidence,
                            CreatedBy        = "System",
                            CreatedDate      = DateTime.UtcNow,
                            BusinessUnitId   = defaultConfig.BusinessUnitId,
                            EmailIngestsId   = dummyIngest.Id
                        };

                        _context.Leads.Add(lead);
                        await _context.SaveChangesAsync();

                        int itemsWithManufacturer = 0;
                        foreach (var aiItem in items)
                        {
                            _context.LeadItems.Add(CreateLeadItem(lead.Id, aiItem));
                            if (!string.IsNullOrEmpty(aiItem.ManufacturerName))
                                itemsWithManufacturer++;
                        }

                        if (items.Count > 0)
                            await _context.SaveChangesAsync();

                        var fileStreamData = new List<(string FileName, MemoryStream Stream, string Extension, string OriginalPath)>();
                        fileStreamData.Add((fileName, new MemoryStream(fileBytes), ext, filePath));

                        await SaveAttachmentsAsync(fileStreamData, lead.Id);

                        dummyIngest.ParsedAt   = DateTime.UtcNow;
                        dummyIngest.ParseStatus = "Success";
                        await _context.SaveChangesAsync();

                        _logger.LogInformation("Successfully created SEC lead {LeadId} with {ItemCount} items ({MfgCount} with manufacturer) from {FileName}.", 
                            lead.Id, items.Count, itemsWithManufacturer, fileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process SEC lead from {FileName}.", fileName);
                        dummyIngest.ParseStatus = "Failed";
                        await _context.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read or ingest SEC file: {FileName}", fileName);
                }
            }
        }

        private async Task SaveAttachmentsAsync(List<(string FileName, MemoryStream Stream, string Extension, string OriginalPath)> files, long leadId)
        {
            var attachmentTasks = new List<Task>();

            foreach (var item in files)
            {
                var safeName    = SanitizeFileName(item.FileName);
                var fileName    = $"{leadId}_{Guid.NewGuid()}_{safeName}";
                var relativePath = Path.Combine("Uploads", "Leads_Folder_Attachments", fileName);
                var physicalPath = Path.Combine(_attachmentPath, fileName);

                attachmentTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        item.Stream.Position = 0;
                        var bytes = item.Stream.ToArray();
                        await File.WriteAllBytesAsync(physicalPath, bytes);

                        _context.Attachments.Add(new Attachment
                        {
                            ParentType   = "Lead",
                            ParentId     = leadId,
                            FileName     = safeName,
                            FilePath     = relativePath,
                            MimeType     = GetMimeType(item.Extension),
                            FileSize     = bytes.Length,
                            ContentType  = GetContentType(item.Extension),
                            CreatedOn    = DateTime.UtcNow,
                            UploadedDate = DateTime.UtcNow
                        });

                        var processedPath = Path.Combine(_processedFolderPath, item.FileName);
                        File.Move(item.OriginalPath, processedPath, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save/move attachment: {FileName}", safeName);
                        if (File.Exists(physicalPath)) File.Delete(physicalPath);
                    }
                }));
            }

            await Task.WhenAll(attachmentTasks);
            if (files.Any())
                await _context.SaveChangesAsync();
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

        private LeadItem CreateLeadItem(long leadId, LeadItemData aiItem)
        {
            int? leadTime = int.TryParse(aiItem.LeadTime ?? "", out int lt) ? lt : null;
            DateTime? receivedDate       = ParseDate(aiItem.ReceivedDate);
            DateTime? bidClosingDateLine = ParseDate(aiItem.BidClosingDateLine);
            return new LeadItem
            {
                LeadId                = leadId,
                CompanyRef            = Truncate(aiItem.CompanyRef, 100),
                CustomerAccountPortalId = Truncate(aiItem.CustomerAccountPortalId, 100),
                CustomerRfqno         = Truncate(aiItem.CustomerRfqno, 100),
                ItemMaterialCode      = Truncate(aiItem.ItemMaterialCode, 100),
                CommodityProduct      = Truncate(aiItem.CommodityProduct, 200),
                BuyerName             = Truncate(aiItem.BuyerName, 200),
                LineItemNo            = Truncate(aiItem.LineItemNo, 50),
                ProductShortName      = Truncate(aiItem.ProductShortName, 1000),
                Alternative           = Truncate(aiItem.Alternative, 100),
                ProductShortDescription = Truncate(aiItem.ProductShortDescription, 1000),
                Currency              = Truncate(aiItem.Currency, 10),
                UnitOfMeasure         = Truncate(aiItem.UnitOfMeasure, 100),
                UnitPrice             = aiItem.UnitPrice,
                Quantity              = aiItem.Quantity ?? 0,
                StorageLocation       = Truncate(aiItem.StorageLocation, 100),
                ManufacturerName      = Truncate(aiItem.ManufacturerName, 200),
                ManufacturerPartNumber = Truncate(aiItem.ManufacturerPartNumber, 100),
                AlternateProductName  = Truncate(aiItem.AlternateProductName, 200),
                AlternatePartNumber   = Truncate(aiItem.AlternatePartNumber, 100),
                ItemText              = Truncate(aiItem.ItemText, 2000),
                MaterialPotext        = Truncate(aiItem.MaterialPotext, 2000),
                LeadTime              = leadTime,
                ReceivedDate          = receivedDate,
                BidClosingDateLine    = bidClosingDateLine,
                Aiconfidence          = (decimal?)(aiItem.ItemConfidence ?? 0.0)
            };
        }

        private string GetFileTypeLabel(string ext) => ext switch
        {
            ".doc"  => "SEC Word Document",
            ".docx" => "Word DOCX",
            _       => "Unknown"
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

        private DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var dateMatch = Regex.Match(s, @"(\d{1,2}/\d{1,2}/\d{4})");
            if (dateMatch.Success) s = dateMatch.Groups[1].Value;
            var formats = new[]
            {
                "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "M/d/yyyy",
                "M/dd/yyyy", "MM/d/yyyy", "dd-MM-yyyy", "d/M/yyyy",
                "yyyy/MM/dd", "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy"
            };
            return DateTime.TryParseExact(s.Trim(), formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }

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
    }
}