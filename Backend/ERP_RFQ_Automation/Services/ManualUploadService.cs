using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Tesseract;
using UglyToad.PdfPig;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Services
{
    public class ManualUploadService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ManualUploadService> _logger;
        private readonly ILLMService _llmService;
        private readonly string _attachmentPath;
        private readonly string _tessDataPath;
        
        // Token limiting constants (same as EmailService)
        private const int MAX_CHARS_FOR_LLM = 120000;
        private const int MAX_CHARS_PER_ATTACHMENT = 50000;
        private const int PRIORITY_EMAIL_BODY_CHARS = 10000;
        // ING-04: internal marker returned by PDF extraction when a scanned/image-only PDF could
        // not be OCR'd. Never fed to the LLM; used only to surface a clear scanned-PDF outcome.
        private const string SCANNED_PDF_SENTINEL = "SCANNED_PDF_NO_TEXT";
        // pdfium (Docnet) and the Tesseract native engine are not thread-safe; serialize OCR.
        private static readonly object _ocrLock = new object();

        // ING-05: when true (default) the document-extraction path enqueues each file as
        // its own durable extraction job instead of the synchronous direct-LLM flow below.
        // The Excel-template path (ProcessCustomerRfqExcelAsync) is deterministic and is
        // NOT affected. Config: Ingestion:UseUnifiedQueue.
        private readonly bool _useUnifiedQueue;
        private readonly ERP_RFQ_Automation.Extraction.IDocumentIngestion? _ingestion;

        private readonly ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService _identity;

        public ManualUploadService(
            ErpRfqAutomationContext context,
            IWebHostEnvironment env,
            ILogger<ManualUploadService> logger,
            ILLMService llmService,
            IConfiguration configuration,
            IFileStorage storage,
            ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService identity,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion? ingestion = null)
        {
            // Required, not optional-with-null: an optional collaborator is always supplied in
            // production and always absent in tests, which is exactly how a step that must always
            // run becomes a step nothing ever exercises.
            _identity = identity;
            _context = context;
            _env = env;
            _logger = logger;
            _llmService = llmService;
            _useUnifiedQueue = configuration.GetValue("Ingestion:UseUnifiedQueue", true);
            _ingestion = ingestion;
            _attachmentPath = storage.GetPath("Manual_Attachments");
            _tessDataPath = Path.Combine(_env.ContentRootPath, "tessdata");
            Directory.CreateDirectory(_attachmentPath);
        }

        /// <summary>
        /// Processes multiple uploaded files, extracts lead data, and saves to the database.
        /// </summary>
        /// <param name="files">List of uploaded files.</param>
        /// <param name="businessUnitId">The BusinessUnitId for the lead.</param>
        /// <returns>A ServiceResult containing the ID of the created Lead.</returns>
        public async Task<ServiceResult<long>> ProcessUploadedFilesAsync(List<Microsoft.AspNetCore.Http.IFormFile> files, long businessUnitId)
        {
            if (files == null || !files.Any())
            {
                _logger.LogWarning("No files provided for manual upload.");
                return ServiceResult<long>.CreateFailure("No files were uploaded.");
            }

            // ING-05: unified queue — each document becomes its own durable extraction
            // job (one lead per document; files are never merged into one lead). The
            // synchronous direct-LLM path below remains available via
            // Ingestion:UseUnifiedQueue=false.
            if (_useUnifiedQueue && _ingestion != null)
                return await EnqueueUploadedFilesAsync(files, businessUnitId);

            const double minConfidence = 0.60;
            string combinedExtractedText = "";
            var fileTypes = new HashSet<string>();
            var attachmentStreams = new List<(string FileName, MemoryStream Stream, string Extension)>();

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                var fileName = file.FileName;
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext))
                {
                    // ING-06: a dropped document is never silent.
                    _logger.LogWarning("Skipping uploaded file {FileName}: unsupported file type '{Extension}'.",
                        fileName, ext);
                    continue;
                }

                string fileType = ext switch
                {
                    ".pdf" => "PDF",
                    ".doc" or ".docx" => "Word",
                    ".xls" or ".xlsx" or ".xlsm" => "Excel",
                    ".csv" => "CSV",
                    ".txt" => "Text",
                    ".jpg" or ".jpeg" => "JPEG",
                    ".png" => "PNG",
                    ".bmp" => "BMP",
                    ".gif" => "GIF",
                    ".tif" or ".tiff" => "TIFF",
                    ".webp" => "WebP",
                    _ => "Unknown"
                };
                fileTypes.Add(fileType);

                try
                {
                    var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    ms.Position = 0;
                    attachmentStreams.Add((fileName, ms, ext));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read uploaded file: {FileName}", fileName);
                }
            }

            bool scannedPdfNeedsReview = false;
            foreach (var (fileName, ms, ext) in attachmentStreams)
            {
                try
                {
                    ms.Position = 0;
                    var text = ext switch
                    {
                        ".pdf" => ExtractTextFromPdf(ms.ToArray()),
                        ".doc" or ".docx" => ExtractTextFromDocx(ms),
                        ".xlsx" => ExtractTextFromExcel(ms),
                        ".pptx" => ExtractTextFromPptx(ms),
                        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff" => ExtractTextFromImage(ms.ToArray()),
                        _ => ""
                    };
                    // ING-04: a scanned/image-only PDF that could not be OCR'd is flagged (never fed
                    // to the LLM) so we can return a clear scanned-PDF outcome instead of an empty lead.
                    if (!string.IsNullOrEmpty(text) && text.Contains(SCANNED_PDF_SENTINEL))
                    {
                        scannedPdfNeedsReview = true;
                        text = "";
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                        combinedExtractedText += $"\n\n[File: {fileName}]\n{text}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract text from: {FileName}", fileName);
                }
                finally
                {
                    ms.Dispose();
                }
            }

            if (string.IsNullOrWhiteSpace(combinedExtractedText))
            {
                if (scannedPdfNeedsReview)
                {
                    _logger.LogWarning("Uploaded PDF appears to be scanned/image-only and could not be OCR'd.");
                    return ServiceResult<long>.CreateFailure("The uploaded PDF appears to be a scanned/image-only document and no text could be extracted (OCR unavailable or produced no text). Please upload a text-based PDF or a clearer scan.");
                }
                _logger.LogWarning("No text extracted from uploaded files.");
                return ServiceResult<long>.CreateFailure("Could not extract any text from the uploaded files. Please check the file content and try again.");
            }

            // --- VALIDATION STEP 1: Content Check ---
            // Check if extracted text looks like an RFQ
            if (!IsLikelyRFQContent(combinedExtractedText))
            {
                _logger.LogWarning("Uploaded files do not appear to contain RFQ content. Processing aborted.");
                return ServiceResult<long>.CreateFailure("The uploaded documents do not appear to be RFQ-related. Please upload a valid Request for Quotation.");
            }

            var defaultConfig = await _context.EmailConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.IsActive && e.BusinessUnitId == businessUnitId);

            if (defaultConfig == null)
            {
                defaultConfig = await _context.EmailConfigurations
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.IsActive);
            }

            if (defaultConfig == null)
            {
                _logger.LogError("No active email configuration found for manual upload.");
                return ServiceResult<long>.CreateFailure("System configuration error: No active email configuration found.");
            }

            var dummyIngest = new EmailIngest
            {
                MessageId = $"Manual_{Guid.NewGuid()}",
                EmailSubject = "Manual Upload",
                FromEmail = "manual@upload.com",
                ToEmail = "system@rfq.com",
                EmailConfigurationId = defaultConfig.Id,
                CreatedOn = DateTime.UtcNow,
                ParseStatus = "Manual",
                RawEmailPath = null,
                ParsedAt = DateTime.UtcNow
            };
            _context.EmailIngests.Add(dummyIngest);
            await _context.SaveChangesAsync();

            string emailSource = fileTypes.Count > 0
                ? string.Join(", ", fileTypes.OrderBy(x => x))
                : "Text Only";

            try
            {
                LeadExtractionResult? ai = null;
                try
                {
                    // Apply smart token limiting before sending to LLM
                    string limitedText = LimitTextForLLM(combinedExtractedText);
                    ai = await _llmService.ExtractLeadDataAsync(limitedText,
                        new AiCallContext(businessUnitId, AiPurposes.RfqExtraction,
                            $"manual-ingest:{dummyIngest.Id}", AiPromptVersions.StructuredRfqExtraction));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI extraction failed – using regex fallback");
                }

                if (ai == null)
                {
                    ai = BuildFallbackExtraction(combinedExtractedText);
                }

                // --- VALIDATION STEP 2: AI Confidence Check ---
                // ... (existing confidence check code) ...
                if (ai == null || ai.OverallConfidence < minConfidence)
                {
                    _logger.LogWarning(
                        "Manual upload AI confidence too low. Confidence: {Confidence:F2}, Required: {Required:F2}",
                        ai?.OverallConfidence ?? 0, minConfidence);
                    
                    // Mark ingestion as failed
                    dummyIngest.ParseStatus = "Failed - Low Confidence";
                    await _context.SaveChangesAsync();
                    
                    return ServiceResult<long>.CreateFailure($"AI analysis confidence low ({ai?.OverallConfidence:P0}). Please ensure the document is clear and readable.");
                }

                // --- DUPLICATE DETECTION ---
                bool isDuplicate = false;

                if (!string.IsNullOrWhiteSpace(ai.Rfqno))
                {
                    // Strict check: Same RFQ No and Buyer
                    isDuplicate = await _context.Leads
                        .AsNoTracking()
                        .AnyAsync(l =>
                            l.Rfqno == ai.Rfqno &&
                            l.BuyersName == ai.BuyersName);
                }
                else if (!string.IsNullOrWhiteSpace(ai.BuyersName))
                {
                    // Fuzzy check: Same Buyer, same item count, and at least one matching item (Quantity + Product)
                    var firstItem = ai.Items.FirstOrDefault();
                    if (firstItem != null && firstItem.Quantity > 0)
                    {
                        isDuplicate = await _context.Leads
                            .AsNoTracking()
                            .AnyAsync(l =>
                                l.BuyersName == ai.BuyersName &&
                                l.NoOfLineItems == ai.Items.Count &&
                                l.LeadItems.Any(li =>
                                    li.CommodityProduct == firstItem.CommodityProduct &&
                                    li.Quantity == firstItem.Quantity));
                    }
                }

                if (isDuplicate)
                {
                    _logger.LogWarning("Skipping duplicate lead from manual upload. RFQ: {Rfqno}, Buyer: {Buyer}", ai.Rfqno, ai.BuyersName);
                    
                    dummyIngest.ParseStatus = "Skipped - Duplicate";
                    await _context.SaveChangesAsync();

                    return ServiceResult<long>.CreateFailure($"Duplicate lead detected. RFQ '{ai.Rfqno}' from '{ai.BuyersName}' already exists.");
                }

                // Use transaction to ensure atomic Lead + Items + Attachments creation
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    DateTime recDate = ParseDate(ai.RecDate) ?? DateTime.UtcNow;
                    DateTime? bidClosingDate = ParseDate(ai.BidClosingDate);
                    DateTime? acknowledgmentDate = ParseDate(ai.AcknowledgmentDate);
                    DateTime? subDate = ParseDate(ai.SubDate);

                    // Every extracted line is kept. Filtering on Quantity > 0 silently discarded any line
                    // whose quantity the document did not state — the extractor is instructed to return
                    // null in exactly that case — and the line count was taken from the filtered list, so
                    // the loss was self-consistent and invisible. A line a reviewer can see and correct is
                    // always better than a line that never existed.
                    var items = ai.Items.ToList();

                    var lead = new Lead
                    {
                        Rfqno = Truncate(ai.Rfqno, 100),
                        BuyersName = Truncate(ai.BuyersName, 255),
                        RecDate = recDate,
                        BidClosingDate = bidClosingDate,
                        BiddingDecision = Truncate(ai.BiddingDecision, 100),
                        AcknowledgmentDate = acknowledgmentDate,
                        SubDate = subDate,
                        HeaderRemarks = Truncate(
                            $"Manual upload. {(!string.IsNullOrWhiteSpace(ai.HeaderRemarks) ? "\n\nSummary:\n" + ai.HeaderRemarks : "")}", 8000),
                        OpportunityNo = Truncate(ai.OpportunityNo, 100),
                        NoOfLineItems = items.Count,
                        Rfqtype = Truncate(ai.Rfqtype, 50),
                        DurationAgreement = Truncate(ai.DurationAgreement, 100),
                        LeadSource = "Manual",
                        EmailSource = emailSource,
                        Clientemail = null,
                        Aiconfidence = (decimal?)ai.OverallConfidence,
                        ReviewVersion = 1,
                        RequiresCommercialReview = true,
                        CommercialFactsVerified = false,
                        CreatedBy = "System",
                        CreatedDate = DateTime.UtcNow,
                        BusinessUnitId = businessUnitId,
                        EmailIngestsId = dummyIngest.Id
                    };

                    _context.Leads.Add(lead);
                    await _context.SaveChangesAsync();

                    // DRIFT GUARD: shared with the email, folder and async-worker doors — see
                    // LeadItemMapper. One mapper means the unit-of-measure canonicalisation
                    // applies to every ingested row, not to whichever door was edited last.
                    foreach (var aiItem in items)
                        _context.LeadItems.Add(LeadItemMapper.Map(aiItem, ParseDate, lead.Id));

                    if (items.Count > 0)
                        await _context.SaveChangesAsync();

                    // Canonical identity, in the SAME transaction as the lead and its lines.
                    //
                    // This door used to create a Lead with _context.Leads.Add and never touch the
                    // identity service, so the lead was born with line items and NO revision.
                    // Everything that needs the immutable revision then refused it — commercial
                    // line resolution throws, which fails RFQ conversion outright. A lead uploaded
                    // through this screen could never be converted. Called after the lines are
                    // saved so revision 1 records them.
                    await _identity.EstablishBaselineRevisionAsync(businessUnitId, lead.Id,
                        new ERP_RFQ_Automation.LeadIdentity.LeadIdentityBaselineRequest(
                            "ManualUpload",
                            "Manual upload: commercial facts were extracted from the uploaded file. "
                            + "Canonical identity established at creation.",
                            "User", lead.CreatedBy ?? "System", $"manual-upload:{businessUnitId}:{lead.Id}"));

                    await SaveAttachmentsAsync(files, lead.Id);

                    // Update ingest status before commit
                    dummyIngest.ParseStatus = "NeedsReview";
                    await _context.SaveChangesAsync();

                    // Commit transaction - all or nothing
                    await transaction.CommitAsync();

                    return ServiceResult<long>.CreateSuccess(lead.Id, "File processed and lead created successfully.");
                }
                catch (Exception txEx)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(txEx, "Transaction rolled back for manual upload.");
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save lead from manual upload.");
                return ServiceResult<long>.CreateFailure("An unexpected error occurred during processing. Please contact support.");
            }
        }

        /// <summary>
        /// ING-05: fans a manual upload out to the durable queue — one content-addressed
        /// job per supported file, shared batch id, interactive priority. Per-file
        /// failures are isolated. Returns success when at least one file was enqueued
        /// (Data carries 0: leads are produced asynchronously by the extraction worker,
        /// not inline — the message states this explicitly).
        /// </summary>
        private async Task<ServiceResult<long>> EnqueueUploadedFilesAsync(
            List<Microsoft.AspNetCore.Http.IFormFile> files, long businessUnitId)
        {
            const long maxBytes = 25 * 1024 * 1024; // 25 MB, mirrors the legacy path
            var batchId = Guid.NewGuid();
            int queued = 0, duplicates = 0, skipped = 0;

            foreach (var file in files)
            {
                if (file.Length == 0)
                {
                    _logger.LogWarning("Skipping uploaded file {FileName}: file is empty.", file.FileName);
                    skipped++;
                    continue;
                }
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext))
                {
                    // ING-06: a dropped document is never silent.
                    _logger.LogWarning("Skipping uploaded file {FileName}: unsupported file type '{Extension}'.",
                        file.FileName, ext);
                    skipped++;
                    continue;
                }
                if (file.Length > maxBytes)
                {
                    _logger.LogWarning("Skipping large file: {File} ({Size} bytes)", file.FileName, file.Length);
                    skipped++;
                    continue;
                }

                try
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    var result = await _ingestion!.IngestAsync(
                        ms.ToArray(), file.FileName, businessUnitId,
                        ERP_RFQ_Automation.Extraction.ExtractionSourceType.ManualUpload,
                        batchId, priority: 10 /* interactive */);
                    if (result.Outcome == ERP_RFQ_Automation.Extraction.EnqueueOutcome.Duplicate)
                        duplicates++;
                    else
                        queued++;
                }
                catch (Exception ex)
                {
                    // Poison-file isolation: one bad file never fails the batch.
                    _logger.LogError(ex, "Failed to enqueue manual upload {FileName}.", file.FileName);
                    skipped++;
                }
            }

            if (queued == 0 && duplicates == 0)
                return ServiceResult<long>.CreateFailure(
                    "No supported documents could be queued for extraction. Please check the file types and try again.");

            var parts = new List<string> { $"{queued} document(s) queued for extraction" };
            if (duplicates > 0) parts.Add($"{duplicates} already processed (duplicate content)");
            if (skipped > 0) parts.Add($"{skipped} skipped");
            return ServiceResult<long>.CreateSuccess(0,
                $"{string.Join(", ", parts)}. Leads will appear in the list once extraction completes (batch {batchId:N}).");
        }

        // SEC-11: image content types accepted by the manual-upload attachment path. These mirror
        // the image extensions the DocumentIntakeAllowList admits (jpg/jpeg/png/gif/bmp/tif/tiff/webp)
        // and are validated by magic bytes below so a disguised payload can never be persisted under
        // a spoofed extension.
        private static readonly IReadOnlyCollection<string> AllowedAttachmentImageTypes = new[]
        {
            "image/jpeg", "image/png", "image/gif", "image/bmp", "image/tiff", "image/webp"
        };

        private static bool IsImageExtension(string ext) => ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" => true,
            _ => false
        };

        private async Task SaveAttachmentsAsync(List<Microsoft.AspNetCore.Http.IFormFile> files, long leadId)
        {
            const long maxBytes = 25 * 1024 * 1024; // 25 MB limit

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                // SECURITY FIX: Check file size BEFORE writing to disk
                if (file.Length > maxBytes)
                {
                    _logger.LogWarning("Skipping large file: {File} ({Size} bytes)", file.FileName, file.Length);
                    continue;
                }

                // SEC-11: for files claiming to be images (by extension), verify the actual content
                // by magic bytes before writing to disk. A file whose bytes don't match a whitelisted
                // image signature is skipped (never persisted) rather than trusting the client name.
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (IsImageExtension(ext))
                {
                    var validation = await FileContentValidator.ValidateImageAsync(file, AllowedAttachmentImageTypes);
                    if (!validation.IsValid)
                    {
                        _logger.LogWarning("Skipping attachment with mismatched image content: {File}. {Reason}",
                            file.FileName, validation.Error);
                        continue;
                    }
                }

                var safeName = SanitizeFileName(file.FileName);
                var fileName = $"{leadId}_{Guid.NewGuid()}_{safeName}";
                var relativePath = Path.Combine("Uploads", "Manual_Attachments", fileName);
                var physicalPath = Path.Combine(_attachmentPath, fileName);

                try
                {
                    // Write to disk only if size is acceptable
                    await using var fileStream = File.Create(physicalPath);
                    await file.CopyToAsync(fileStream);
                    await fileStream.FlushAsync();
                    var size = fileStream.Length;

                    _context.Attachments.Add(new Attachment
                    {
                        ParentType = "Lead",
                        ParentId = leadId,
                        FileName = safeName,
                        FilePath = relativePath,
                        MimeType = file.ContentType,
                        FileSize = size,
                        ContentType = file.ContentType?.Split('/')[0],
                        CreatedOn = DateTime.UtcNow,
                        UploadedDate = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save file: {File}", safeName);
                    // Clean up file if it was created
                    if (File.Exists(physicalPath))
                    {
                        try { File.Delete(physicalPath); } catch { /* Ignore cleanup errors */ }
                    }
                }
            }

            if (files.Any())
                await _context.SaveChangesAsync();
        }

        // Reused methods from EmailService (duplicated here for independence; consider refactoring to a shared utility class if needed)
        private LeadExtractionResult BuildFallbackExtraction(string extracted)
        {
            var rfqNo = ExtractRFQNo(extracted);
            var rfqNoConf = rfqNo != null ? 0.8 : 0.0;
            var buyersName = ExtractCompany(extracted);
            var buyersNameConf = buyersName != null ? 0.8 : 0.0;
            var bidClosing = ExtractBidClosing(extracted);
            var bidClosingConf = bidClosing != null ? 0.8 : 0.0;
            var biddingDecision = ExtractBiddingDecision(extracted);
            var biddingDecisionConf = biddingDecision != null ? 0.8 : 0.0;
            var acknowledgmentDate = ExtractAcknowledgmentDate(extracted);
            var acknowledgmentDateConf = acknowledgmentDate != null ? 0.8 : 0.0;
            var subDate = ExtractSubDate(extracted);
            var subDateConf = subDate != null ? 0.8 : 0.0;
            var opportunityNo = ExtractOpportunityNo(extracted);
            var opportunityNoConf = opportunityNo != null ? 0.8 : 0.0;
            var rfqType = ExtractRFQType(extracted);
            var rfqTypeConf = rfqType != null ? 0.8 : 0.0;
            var durationAgreement = ExtractDurationAgreement(extracted);
            var durationAgreementConf = durationAgreement != null ? 0.8 : 0.0;
            string? recDate = null;
            var recDateConf = 0.0;
            string? headerRemarks = null;
            var headerRemarksConf = 0.0;
            var items = BuildFallbackItems(extracted);
            var confs = new List<double>
            {
                rfqNoConf, buyersNameConf, recDateConf, bidClosingConf, biddingDecisionConf,
                acknowledgmentDateConf, subDateConf, headerRemarksConf, opportunityNoConf,
                rfqTypeConf, durationAgreementConf
            };
            confs.AddRange(items.Select(i => i.ItemConfidence ?? 0.0));
            double overall = confs.Count > 0 ? confs.Average() : 0.0;
            return new LeadExtractionResult(
                rfqNo, rfqNoConf,
                buyersName, buyersNameConf,
                recDate, recDateConf,
                bidClosing, bidClosingConf,
                biddingDecision, biddingDecisionConf,
                acknowledgmentDate, acknowledgmentDateConf,
                subDate, subDateConf,
                headerRemarks, headerRemarksConf,
                opportunityNo, opportunityNoConf,
                rfqType, rfqTypeConf,
                durationAgreement, durationAgreementConf,
                overall,
                items
            );
        }

        private List<LeadItemData> BuildFallbackItems(string body)
        {
            var items = new List<LeadItemData>();
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(line,
                    @"item[:\s]+(.*?)\s*-\s*qty[:\s]+(\d+)(?:\s*-\s*price[:\s]+([\d.]+))?(?:\s*-\s*currency[:\s]+(\w+))?(?:\s*-\s*unit[:\s]+(\w+))?",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    var nameConf = !string.IsNullOrWhiteSpace(name) ? 0.8 : 0.0;
                    var qtyStr = m.Groups[2].Value;
                    int quantity = int.TryParse(qtyStr, out int q) ? q : 0;
                    var qtyConf = quantity > 0 ? 0.8 : 0.0;
                    var priceStr = m.Groups[3].Value;
                    decimal? unitPrice = decimal.TryParse(priceStr, out decimal p) ? p : null;
                    var priceConf = unitPrice != null ? 0.8 : 0.0;
                    var currency = m.Groups[4].Value.Trim();
                    var currencyConf = !string.IsNullOrWhiteSpace(currency) ? 0.8 : 0.0;
                    var unit = m.Groups[5].Value.Trim();
                    var unitConf = !string.IsNullOrWhiteSpace(unit) ? 0.8 : 0.0;
                    var otherConf = 0.0;
                    var confs = new List<double> { nameConf, qtyConf, priceConf, currencyConf, unitConf };
                    confs.AddRange(Enumerable.Repeat(0.0, 19));
                    double itemConf = confs.Average();
                    items.Add(new LeadItemData(
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        name, nameConf,
                        null, otherConf,
                        null, otherConf,
                        currency, currencyConf,
                        unit, unitConf,
                        unitPrice, priceConf,
                        quantity, qtyConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        null, otherConf,
                        itemConf
                    ));
                }
            }
            return items;
        }

        private string ExtractTextFromPdf(byte[] bytes)
        {
            string pdfPigText = "";
            try
            {
                using var doc = PdfDocument.Open(bytes);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                    sb.AppendLine(page.Text);
                pdfPigText = sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF text extraction failed");
            }

            // Fast path: the PDF already has an embedded text layer.
            if (!IsNearEmptyText(pdfPigText))
                return pdfPigText;

            // ING-04: image-only / scanned PDF -> rasterize with Docnet and OCR with Tesseract.
            _logger.LogInformation(
                "PDF has little/no embedded text ({Chars} non-whitespace chars); attempting OCR fallback.",
                CountNonWhitespace(pdfPigText));
            var ocrText = TryOcrScannedPdf(bytes);
            if (!IsNearEmptyText(ocrText))
            {
                // OCR text is inherently lower-confidence: label it so the LLM/reviewers use caution.
                return "[OCR-EXTRACTED TEXT FROM SCANNED PDF - lower confidence, may contain recognition errors]\n" + ocrText;
            }

            // Scanned PDF with no recoverable text -> emit a marker so the caller can return a clear
            // "scanned PDF, OCR needed" outcome instead of silently producing an empty lead.
            _logger.LogWarning("Scanned PDF could not be OCR'd (OCR unavailable or produced no text).");
            return SCANNED_PDF_SENTINEL;
        }

        /// <summary>
        /// ING-04: rasterizes a scanned/image-only PDF with Docnet.Core and OCRs each page with the
        /// existing Tesseract engine. Returns recognized text, or "" if OCR is unavailable/failed.
        /// </summary>
        private string TryOcrScannedPdf(byte[] pdfBytes)
        {
            const int MAX_OCR_PAGES = 10;      // bound runtime for large documents
            const double RENDER_SCALE = 2.0;   // ~144 DPI: balances OCR accuracy vs. memory/time
            try
            {
                var sb = new StringBuilder();
                lock (_ocrLock)
                {
                    using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(RENDER_SCALE));
                    int pageCount = docReader.GetPageCount();
                    int pagesToProcess = Math.Min(pageCount, MAX_OCR_PAGES);
                    if (pageCount > MAX_OCR_PAGES)
                        _logger.LogWarning("Scanned PDF has {Total} pages; OCR limited to first {Limit}.", pageCount, MAX_OCR_PAGES);

                    using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                    for (int i = 0; i < pagesToProcess; i++)
                    {
                        try
                        {
                            using var pageReader = docReader.GetPageReader(i);
                            var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover()); // BGRA over white
                            int width = pageReader.GetPageWidth();
                            int height = pageReader.GetPageHeight();
                            if (rawBytes == null || width <= 0 || height <= 0 ||
                                rawBytes.Length < width * height * 4)
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
                            _logger.LogWarning(exPage, "OCR failed for scanned PDF page {Page}", i);
                        }
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanned-PDF OCR fallback unavailable or failed.");
                return "";
            }
        }

        /// <summary>Encodes a top-down BGRA buffer as a 24-bit (BGR) bottom-up BMP for Leptonica/Tesseract.</summary>
        private static byte[] BgraToBmp24(byte[] bgra, int width, int height)
        {
            int rowSize = ((24 * width + 31) / 32) * 4; // rows padded to a 4-byte boundary
            int pixelDataSize = rowSize * height;
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
            WriteInt32LE(bmp, 30, 0);      // BI_RGB
            WriteInt32LE(bmp, 34, pixelDataSize);
            WriteInt32LE(bmp, 38, 2835);
            WriteInt32LE(bmp, 42, 2835);

            int srcStride = width * 4;
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * srcStride;
                int dst = headerSize + (height - 1 - y) * rowSize;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 4;
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
            int n = 0;
            foreach (var c in s) if (!char.IsWhiteSpace(c)) n++;
            return n;
        }

        // A PDF that yields fewer than this many non-whitespace characters is treated as scanned/image-only.
        private static bool IsNearEmptyText(string? s) => CountNonWhitespace(s) < 20;

        private string ExtractTextFromDocx(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var doc = WordprocessingDocument.Open(ms, false);
                var sb = new StringBuilder();
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body != null)
                {
                    foreach (var text in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>())
                        sb.Append(text.Text + " ");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DOCX extraction failed");
                return "";
            }
        }

        private string ExtractTextFromExcel(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var doc = SpreadsheetDocument.Open(ms, false);
                var sb = new StringBuilder();
                var workbookPart = doc.WorkbookPart;
                var sstPart = workbookPart?.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
                var sst = sstPart?.SharedStringTable;
                foreach (var sheet in workbookPart.Workbook.Descendants<Sheet>())
                {
                    var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
                    foreach (var cell in worksheetPart.Worksheet.Descendants<Cell>())
                    {
                        if (cell.CellValue == null) continue;
                        string value = cell.CellValue.Text;
                        if (cell.DataType != null && cell.DataType == CellValues.SharedString && sst != null)
                        {
                            int index = int.Parse(value);
                            value = sst.ElementAt(index).InnerText;
                        }
                        sb.Append(value + " ");
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "XLSX extraction failed");
                return "";
            }
        }

        private string ExtractTextFromPptx(MemoryStream ms)
        {
            try
            {
                ms.Position = 0;
                using var doc = PresentationDocument.Open(ms, false);
                var sb = new StringBuilder();
                var presentationPart = doc.PresentationPart;
                if (presentationPart == null) return "";
                foreach (var slidePart in presentationPart.SlideParts)
                {
                    foreach (var text in slidePart.Slide.Descendants<DocumentFormat.OpenXml.Presentation.Text>())
                    {
                        sb.Append(text.Text + " ");
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PPTX extraction failed");
                return "";
            }
        }

        private string ExtractTextFromImage(byte[] bytes)
        {
            try
            {
                using var engine = new TesseractEngine(_tessDataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromMemory(bytes);
                using var page = engine.Process(img);
                return page.GetText();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR failed");
                return "";
            }
        }

        private string? ExtractField(string text, string pattern, int maxLength)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!m.Success) return null;
            var value = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        private string? ExtractRFQNo(string text) => ExtractField(text, @"RFQ[:#\s]*([A-Z0-9-]+)", 100);
        private string? ExtractCompany(string text) => ExtractField(text, @"company[:\s]+(.+?)(?=\r?\n|$)", 510);
        private string? ExtractBidClosing(string text) => ExtractField(text, @"bid closing[:\s]+(.+?)(?=\r?\n|$)", 100);
        private string? ExtractBiddingDecision(string text) => ExtractField(text, @"bidding decision[:\s]+(.+?)(?=\r?\n|$)", 100);
        private string? ExtractAcknowledgmentDate(string text) => ExtractField(text, @"acknowledgment date[:\s]+(.+?)(?=\r?\n|$)", 100);
        private string? ExtractSubDate(string text) => ExtractField(text, @"submission date[:\s]+(.+?)(?=\r?\n|$)", 100);
        private string? ExtractOpportunityNo(string text) => ExtractField(text, @"opportunity[:#\s]*([A-Z0-9-]+)", 100);
        private string? ExtractRFQType(string text) => ExtractField(text, @"type[:\s]+(Agreement|Direct)", 50);
        private string? ExtractDurationAgreement(string text) => ExtractField(text, @"duration[:\s]+(.+?)(?=\r?\n|$)", 100);

        private string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        // Shared with every other ingestion door — see RfqDateParser. The sentinel guard this
        // method used to own now applies uniformly, and this path no longer silently rejects
        // the yyyy/MM/dd form that the email and folder doors accepted.
        private DateTime? ParseDate(string? s) => Extraction.RfqDateParser.Parse(s);

        private string SanitizeFileName(string fileName)
        {
            return string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");
        }

        // Smart token limiting for LLM input (same as EmailService)
        private string LimitTextForLLM(string fullText)
        {
            if (string.IsNullOrEmpty(fullText) || fullText.Length <= MAX_CHARS_FOR_LLM)
                return fullText;

            _logger.LogWarning("Text too long for LLM ({Length} chars), truncating to {Max} chars", 
                fullText.Length, MAX_CHARS_FOR_LLM);

            // Simple truncation with ellipsis
            return fullText.Substring(0, MAX_CHARS_FOR_LLM - 100) + "\n\n[... Text truncated due to length ...]";
        }

        // DRIFT GUARD: derived from the security inspection allow-list — never keep a
        // private copy (a private list here previously accepted .pptx, which inspection
        // then rejected, and dropped .xls/.csv uploads inspection would have accepted).
        // See DocumentIntakeAllowList and its tests.
        internal static bool IsSupportedExtension(string ext) =>
            ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList.IsAllowed(ext);
        /// <summary>
        /// Processes a specialized RFQ Excel file for a specific customer, bypassing AI.
        /// </summary>
        public async Task<ServiceResult<long>> ProcessCustomerRfqExcelAsync(Microsoft.AspNetCore.Http.IFormFile file, long businessUnitId, string createdBy)
        {
            if (file == null || file.Length == 0)
            {
                return ServiceResult<long>.CreateFailure("No file uploaded.");
            }

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                ms.Position = 0;

                using var doc = SpreadsheetDocument.Open(ms, false);
                var workbookPart = doc.WorkbookPart;
                var sheet = workbookPart?.Workbook.Descendants<Sheet>().FirstOrDefault();
                if (sheet == null) return ServiceResult<long>.CreateFailure("No sheets found in Excel.");

                var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
                var rows = worksheetPart.Worksheet.Descendants<Row>().ToList();

                if (rows.Count <= 1) return ServiceResult<long>.CreateFailure("Excel file is empty or contains only headers.");

                var sstPart = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault();
                var sst = sstPart?.SharedStringTable;

                // Load currencies for the business unit to map the string from Excel
                var currencies = await _context.Currencies
                    .AsNoTracking()
                    .Where(c => c.BusinessUnitId == businessUnitId && c.IsActive == true)
                    .ToListAsync();

                var rfqItems = new List<Rfqitem>();
                
                // Assuming Row 1 is header, data starts from Row 2
                foreach (var row in rows.Skip(1))
                {
                    var cells = row.Elements<Cell>().ToList();
                    if (!cells.Any()) continue;

                    var item = new Rfqitem
                    {
                        LineItemNo = GetCellValue(workbookPart, cells, "A", sst),        // Number (Col 0)
                        ProductShortName = GetCellValue(workbookPart, cells, "B", sst),   // Name (Col 1)
                        Alternative = GetCellValue(workbookPart, cells, "C", sst),        // Alternative (Col 2)
                        ProductShortDescription = GetCellValue(workbookPart, cells, "F", sst), // Description (Col 5)
                        Currency = GetCellValue(workbookPart, cells, "I", sst),           // Currency (Col 8)
                        UnitOfMeasure = GetCellValue(workbookPart, cells, "J", sst),      // Unit of Measure (Col 9)
                        ItemMaterialCode = GetCellValue(workbookPart, cells, "K", sst),    // Material Number (Col 10)
                        UnitPrice = decimal.TryParse(GetCellValue(workbookPart, cells, "M", sst), out var price) ? price : 0, // * Price (Col 12)
                        Quantity = int.TryParse(GetCellValue(workbookPart, cells, "N", sst), out var qty) ? qty : 0,          // Quantity (Col 13)
                        LeadTime = int.TryParse(GetCellValue(workbookPart, cells, "O", sst), out var lt) ? lt : null,         // * Lead Time (Col 14)
                        ManufacturerPartNumber = GetCellValue(workbookPart, cells, "Q", sst), // * MPN (Col 16)
                        ManufacturerName = GetCellValue(workbookPart, cells, "S", sst),       // Manufacturer (Col 18)
                        CreatedBy = createdBy,
                        CreatedDate = DateTime.UtcNow
                    };

                    // Map Currency string to CurrencyId
                    if (!string.IsNullOrWhiteSpace(item.Currency))
                    {
                        var matchingCurrency = currencies.FirstOrDefault(c => 
                            c.Code.Equals(item.Currency, StringComparison.OrdinalIgnoreCase) || 
                            c.CurrencyName.Equals(item.Currency, StringComparison.OrdinalIgnoreCase));
                        
                        if (matchingCurrency != null)
                        {
                            item.CurrencyId = matchingCurrency.Id;
                        }
                    }


                    if (!string.IsNullOrWhiteSpace(item.ProductShortName) || !string.IsNullOrWhiteSpace(item.ItemMaterialCode))
                    {
                        rfqItems.Add(item);
                    }
                }

                if (!rfqItems.Any())
                {
                    return ServiceResult<long>.CreateFailure("No valid items found in the Excel file.");
                }

                var rfq = new Rfq
                {
                    Rfqno = $"RFQ_{Path.GetFileNameWithoutExtension(file.FileName)}_{DateTime.UtcNow:yyyyMMddHHmm}",
                    // Unknown buyer stays null (never a fabricated placeholder) so the
                    // UI can style it honestly as "Buyer not identified yet".
                    BuyersName = null,
                    RecDate = DateTime.UtcNow,
                    BusinessUnitId = businessUnitId,
                    CreatedBy = createdBy,
                    CreatedDate = DateTime.UtcNow,
                    NoOfLineItems = rfqItems.Count,
                    RfqstatusId = await ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecycleStatusCatalog.ResolveIdAsync(
                        _context, businessUnitId, "Rfq", "DRAFT"),
                    Rfqitems = rfqItems
                };

                _context.Rfqs.Add(rfq);
                await _context.SaveChangesAsync();

                return ServiceResult<long>.CreateSuccess(rfq.Id, "RFQ created successfully from Excel.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing customer RFQ Excel.");
                return ServiceResult<long>.CreateFailure($"Error: {ex.Message}");
            }
        }

        private string GetCellValue(WorkbookPart workbookPart, List<Cell> cells, string columnLetter, SharedStringTable sst)
        {
            // Find cell with exact column letter (e.g. A1, A2, but not AA1)
            var cell = cells.FirstOrDefault(c => {
                if (c.CellReference == null) return false;
                string reference = c.CellReference.Value!;
                string column = new string(reference.TakeWhile(ch => char.IsLetter(ch)).ToArray());
                return column == columnLetter;
            });

            if (cell == null || cell.CellValue == null) return "";

            string value = cell.CellValue.Text;
            if (cell.DataType != null && cell.DataType == CellValues.SharedString && sst != null)
            {
                int index = int.Parse(value);
                value = sst.ElementAt(index).InnerText;
            }
            return value.Trim();
        }

        private bool IsLikelyRFQContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.ToLowerInvariant();

            var strongIndicators = new[]
            {
                "rfq", "request for quotation", "request for quote",
                "tender", "bid invitation", "procurement notice", "quotation request"
            };

            var weakIndicators = new[]
            {
                "quote", "bid", "proposal",
                "purchase", "pricing", "delivery", "line item", "part no", "qty"
            };

            // Check strong indicators
            if (strongIndicators.Any(ind => text.Contains(ind)))
                return true;

            // Check weak indicators (need multiple)
            var weakMatches = weakIndicators.Count(ind => text.Contains(ind));
            if (weakMatches >= 2)
                return true;

            // Check for RFQ numbers
            if (Regex.IsMatch(text, @"RFQ[#:\s-]*[A-Z0-9-]+", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

        private bool HasStrongRFQIndicators(string text, LeadExtractionResult? ai)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.ToLowerInvariant();

            // Check for RFQ number pattern in text
            if (Regex.IsMatch(text, @"rfq\s*#?\s*([A-Z0-9-]+)", RegexOptions.IgnoreCase))
                return true;

            // Check if AI found an RFQ number
            if (!string.IsNullOrWhiteSpace(ai?.Rfqno))
                return true;

            // Check for explicit RFQ keywords
            var strongKeywords = new[] { "request for quotation", "rfq", "tender invitation" };
            if (strongKeywords.Any(k => text.Contains(k)))
                return true;

            return false;
        }
    }
}
