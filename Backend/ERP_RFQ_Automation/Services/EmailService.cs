using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Text;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Tesseract;
using UglyToad.PdfPig;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Services
{
    public class EmailService : IEmailService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<EmailService> _logger;
        private readonly ILLMService _llmService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _attachmentPath;
        private readonly string _rawEmailPath;
        private readonly string _tessDataPath;
        // Configuration constants
        private const double MIN_CONFIDENCE_THRESHOLD = 0.3; // Minimum AI confidence to accept
        private const double MIN_CONFIDENCE_WITH_VALIDATION = 0.25; // Minimum if email passes validation
        private const int SEARCH_DAYS_BACK = 7; // Days to look back for emails
        private const long MAX_ATTACHMENT_SIZE = 25 * 1024 * 1024; // 25 MB
        // Token/Text limiting for LLM to prevent context length errors and excessive costs
        private const int MAX_CHARS_FOR_LLM = 32000; // ~8k tokens (safe for most models)
        private const int MAX_CHARS_PER_ATTACHMENT = 10000; // Limit per attachment during extraction
        private const int PRIORITY_EMAIL_BODY_CHARS = 5000; // Reserve chars for email body
        // ING-01/ING-04: durable EmailIngest.ParseStatus values (free text, HasMaxLength(50)).
        private const string STATUS_PENDING = "Pending";
        private const string STATUS_SUCCESS = "Success";
        private const string STATUS_FAILED = "Failed";
        // ING-05 (unified queue): body + attachments handed to the durable extraction
        // queue; the LeadPersister flips this to Success/NeedsReview when the job lands.
        private const string STATUS_QUEUED = "Queued";
        private const string STATUS_REJECTED = "Rejected - Low Signal";        // <= 50 chars
        private const string STATUS_SCANNED_PDF = "NeedsReview - Scanned PDF";  // <= 50 chars
        // ING-04: internal marker returned by PDF extraction when a page image-only/scanned PDF
        // could not be OCR'd. Never fed to the LLM; used only to route the ingest to review.
        private const string SCANNED_PDF_SENTINEL = "SCANNED_PDF_NO_TEXT";
        // pdfium (Docnet) and the Tesseract native engine are not thread-safe; serialize OCR so
        // parallel attachment extraction cannot crash the native libraries.
        private static readonly object _ocrLock = new object();
        // ING-05: when true (default) the email door no longer runs its own LLM
        // extraction — the body and every attachment are enqueued as durable extraction
        // jobs instead. Config: Ingestion:UseUnifiedQueue (set false to restore the
        // legacy direct-extraction path unchanged).
        private readonly bool _useUnifiedQueue;
        public EmailService(ErpRfqAutomationContext context, IWebHostEnvironment env,
            ILogger<EmailService> logger, ILLMService llmService, IServiceScopeFactory scopeFactory,
            IConfiguration configuration, IFileStorage storage)
        {
            _context = context;
            _env = env;
            _logger = logger;
            _llmService = llmService;
            _scopeFactory = scopeFactory;
            _useUnifiedQueue = configuration.GetValue("Ingestion:UseUnifiedQueue", true);
            _attachmentPath = storage.GetPath("RFQ_Attachments");
            _rawEmailPath = storage.GetPath("Raw_Emails");
            _tessDataPath = Path.Combine(_env.ContentRootPath, "tessdata");
            Directory.CreateDirectory(_attachmentPath);
            Directory.CreateDirectory(_rawEmailPath);
        }
        public async Task FetchAndSaveLeadsAsync(long? businessUnitId = null)
        {
            var query = _context.EmailConfigurations
                .Where(e => e.IsActive && e.Protocol.ToUpper() == "IMAP");

            if (businessUnitId.HasValue && businessUnitId.Value > 0)
            {
                query = query.Where(e => e.BusinessUnitId == businessUnitId.Value);
            }

            var configs = await query.ToListAsync();
            _logger.LogInformation("Found {Count} active IMAP email configurations to process.", configs.Count);

            foreach (var config in configs)
            {
                try
                {
                    _logger.LogInformation("Starting process for configuration: {Email}", config.EmailAddress);
                    await ProcessConfigAsync(config);
                    _logger.LogInformation("Finished process for configuration: {Email}", config.EmailAddress);
                }
                catch (Exception ex)
                {
                    // This catch ensures that even if ProcessConfigAsync has an unhandled catastrophic failure,
                    // it will not stop the loop from processing the next email configuration.
                    _logger.LogError(ex, "Unexpected failure processing email configuration {Email}. Moving to next.", config.EmailAddress);
                }
            }
        }
        private async Task ProcessConfigAsync(EmailConfiguration config)
        {
            using var scope = _scopeFactory.CreateScope();
            var localContext = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var localLlm = scope.ServiceProvider.GetRequiredService<ILLMService>();
            // ING-05: unified-queue gateway from the SAME scope as localContext (null when
            // not registered -> the legacy direct path below still works).
            var ingestion = scope.ServiceProvider.GetService<ERP_RFQ_Automation.Extraction.IDocumentIngestion>();
            using var client = new ImapClient();
            try
            {
                await client.ConnectAsync(config.Host, config.Port,
                    config.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None);
                await client.AuthenticateAsync(config.EmailAddress, config.Password);
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadWrite);
                var sinceDate = DateTime.Today.AddDays(-SEARCH_DAYS_BACK);
                // More specific RFQ keyword search
                var query = BuildRFQSearchQuery(sinceDate).And(SearchQuery.NotSeen); // Added NotSeen to avoid reprocessing seen emails
                var uids = await inbox.SearchAsync(query);
                _logger.LogInformation("Found {Count} potential RFQ emails for {Email}", uids.Count, config.EmailAddress);
                foreach (var uid in uids)
                {
                    try
                    {
                        var message = await inbox.GetMessageAsync(uid);
                        // ING-01: only mark \Seen once a durable record (EmailIngest + raw .eml)
                        // exists, so a message we fail to persist is retried on the next cycle
                        // instead of vanishing.
                        bool durablyPersisted = await ProcessSingleEmailAsync(message, config, localContext, localLlm, ingestion);

                        // Check if connection is still alive before marking as seen
                        if (!client.IsConnected)
                        {
                            _logger.LogWarning("Connection lost during processing email {UID}. Reconnecting...", uid);
                            await client.ConnectAsync(config.Host, config.Port, config.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None);
                            await client.AuthenticateAsync(config.EmailAddress, config.Password);
                            await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
                        }

                        if (durablyPersisted)
                        {
                            await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true); // Mark as seen only after a durable record exists
                        }
                        else
                        {
                            _logger.LogWarning("Email UID {UID} not persisted durably; leaving unseen for retry next cycle.", uid);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing email UID {UID}", uid);
                    }
                }
                await client.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IMAP error for config: {Email}", config.EmailAddress);
            }
        }
        private SearchQuery BuildRFQSearchQuery(DateTime sinceDate)
        {
            // Expanded keyword set for broader detection
            var keywords = new[] { 
                "rfq", "rfp", "tender","quote" ,"quotation", "procurement", 
                "bid invitation", "itb", "price inquiry", "price request", 
                "quote request", "request for quote", "inquiry", "material request"
            };

            SearchQuery? keywordQuery = null;

            foreach (var kw in keywords)
            {
                var q = SearchQuery.SubjectContains(kw).Or(SearchQuery.BodyContains(kw));
                keywordQuery = keywordQuery == null ? q : keywordQuery.Or(q);
            }

            return SearchQuery.SentSince(sinceDate).And(keywordQuery ?? SearchQuery.All);
        }
        /// <summary>
        /// Processes a single fetched message. Returns true when a durable record
        /// (EmailIngest row + raw .eml) exists for the message, meaning the caller may safely
        /// mark it \Seen; returns false only when nothing could be persisted (retry next cycle).
        /// </summary>
        private async Task<bool> ProcessSingleEmailAsync(MimeMessage message, EmailConfiguration config,
            ErpRfqAutomationContext context, ILLMService llmService,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion? ingestion = null)
        {
            var messageId = message.MessageId ?? Guid.NewGuid().ToString();
            var from = message.From.ToString();
            var to = message.To.ToString();
            var subject = message.Subject ?? "";
            // Check if already processed by messageId or same from/to/subject (likely duplicate send).
            // A durable record already exists from a previous run, so it is safe to mark \Seen.
            if (await context.EmailIngests.AnyAsync(e => e.MessageId == messageId ||
                (e.FromEmail == from && e.ToEmail == to && e.EmailSubject == subject)))
            {
                _logger.LogDebug("Skipping duplicate email: {MessageId} (From: {From}, Subject: {Subject})", messageId, from, subject);
                return true;
            }

            // ING-01: persist a durable record for EVERY fetched message BEFORE classification,
            // so a real RFQ the keyword filter misjudges is never silently dropped.
            var ingest = new EmailIngest
            {
                MessageId = messageId,
                EmailSubject = subject,
                FromEmail = from,
                ToEmail = to,
                EmailConfigurationId = config.Id,
                CreatedOn = DateTime.UtcNow,
                ParseStatus = STATUS_PENDING
            };

            // Save the raw email bytes first so the original is never lost, even for rejected mail.
            var rawPath = Path.Combine(_rawEmailPath, $"{Guid.NewGuid()}.eml");
            try
            {
                message.WriteTo(rawPath);
                ingest.RawEmailPath = rawPath;
            }
            catch (Exception ex)
            {
                // If we cannot even persist the raw bytes, do NOT let the caller mark it seen.
                _logger.LogError(ex, "Failed to persist raw email bytes for {MessageId}. Will retry next cycle.", messageId);
                return false;
            }

            context.EmailIngests.Add(ingest);
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                // A row already exists (concurrent/duplicate delivery) -> durable record present.
                _logger.LogWarning("Duplicate messageId detected: {MessageId}", messageId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist EmailIngest for {MessageId}. Will retry next cycle.", messageId);
                try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { /* best-effort cleanup */ }
                return false;
            }

            // From here a durable record exists: the caller may mark the message \Seen regardless
            // of the classification/extraction outcome below.

            // ING-01: classification no longer drops the message. A low-signal message is retained
            // with a rejected status (raw bytes + DB row preserved) instead of vanishing.
            if (!await IsLikelyRFQEmailAsync(message))
            {
                _logger.LogInformation("Email classified as low-signal (non-RFQ), retained for review: {Subject}", subject);
                ingest.ParseStatus = STATUS_REJECTED;
                ingest.ParsedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
                return true;
            }

            // ING-05: unified queue — the RFQ email's body and each attachment become
            // durable, content-addressed extraction jobs (one batch), replacing the
            // direct LLM extraction below. The pre-created EmailIngest is referenced via
            // the job's provenance sidecar so the produced lead(s) link to the REAL
            // ingest (from/subject) instead of a synthetic one.
            if (_useUnifiedQueue && ingestion != null)
            {
                var queued = await EnqueueEmailForExtractionAsync(message, ingest, config, ingestion);
                if (queued > 0)
                {
                    ingest.ParseStatus = STATUS_QUEUED; // persister flips to Success/NeedsReview
                }
                else
                {
                    // Nothing enqueuable (empty body, no supported attachments) — same
                    // terminal state the legacy path produced for unextractable mail.
                    if (ingest.ParseStatus == STATUS_PENDING)
                        ingest.ParseStatus = STATUS_FAILED;
                    ingest.ParsedAt = DateTime.UtcNow;
                }
                await context.SaveChangesAsync();
                return true;
            }

            // Legacy direct-extraction path (Ingestion:UseUnifiedQueue=false).
            var (leadId, extractedText) = await SaveLeadFromEmailAndAttachments(
                message, ingest, config, context, llmService);
            ingest.ParsedAt = DateTime.UtcNow;
            if (leadId > 0)
            {
                ingest.ParseStatus = "NeedsReview";
            }
            else if (ingest.ParseStatus == STATUS_PENDING)
            {
                // No lead created and no more specific status (e.g. scanned-PDF review) was set.
                ingest.ParseStatus = STATUS_FAILED;
            }
            await context.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// ING-05: fans one RFQ email out to the durable extraction queue — the body text
        /// as its own content-addressed .txt document plus one job per supported
        /// attachment, all sharing one batch id and one provenance sidecar (real
        /// EmailIngest id + from/subject). Per-document failures are isolated: one bad
        /// attachment never blocks the body or the other attachments. Re-delivered
        /// content dedups naturally on the queue's (BusinessUnitId, ContentHash) index.
        /// Returns the number of jobs enqueued (duplicates count — they are handled work).
        /// </summary>
        private async Task<int> EnqueueEmailForExtractionAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration config,
            ERP_RFQ_Automation.Extraction.IDocumentIngestion ingestion)
        {
            var batchId = Guid.NewGuid();
            var queued = 0;
            var metadata = new ERP_RFQ_Automation.Extraction.ExtractionJobMetadata
            {
                SourceOccurrenceId = $"email:{message.MessageId ?? ingest.Id.ToString()}:body",
                EmailIngestId = ingest.Id,
                FromEmail = message.From.Mailboxes.FirstOrDefault()?.Address,
                Subject = message.Subject ?? "",
                SourceReceivedAtUtc = message.Date,
                ClientEmail = config.EmailAddress,
                LeadSource = "Email",
                EmailSource = "Text Only" // per-attachment jobs override with their own label
            };

            // 1) Email body as its own document (Subject/From header lines give the
            //    extractor the same context the legacy prompt had).
            try
            {
                var body = GetEmailBody(message);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var bodyDoc = $"Subject: {message.Subject}\nFrom: {message.From}\nDate: {message.Date:yyyy-MM-dd}\n\n{body}";
                    var bodyName = $"{SanitizeFileName(message.Subject ?? "email")}_body.txt";
                    var result = await ingestion.IngestAsync(
                        Encoding.UTF8.GetBytes(bodyDoc), bodyName, config.BusinessUnitId,
                        ERP_RFQ_Automation.Extraction.ExtractionSourceType.Email,
                        batchId, priority: 0, metadata);
                    queued++;
                    _logger.LogInformation("Enqueued email body as job {JobId} ({Outcome}) for ingest {IngestId}.",
                        result.JobId, result.Outcome, ingest.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue email body for ingest {IngestId}.", ingest.Id);
            }

            // 2) Each supported attachment is its own job (same batch, same provenance).
            var attachmentOrdinal = 0;
            foreach (var att in message.Attachments)
            {
                attachmentOrdinal++;
                if (att is not MimePart part || part.FileName == null) continue;
                var ext = Path.GetExtension(part.FileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext)) continue;

                try
                {
                    using var ms = new MemoryStream();
                    await part.Content.DecodeToAsync(ms);
                    if (ms.Length == 0) continue;
                    if (ms.Length > MAX_ATTACHMENT_SIZE)
                    {
                        _logger.LogWarning("Skipping large attachment {FileName} ({Size} bytes).", part.FileName, ms.Length);
                        continue;
                    }

                    var attMetadata = new ERP_RFQ_Automation.Extraction.ExtractionJobMetadata
                    {
                        SourceOccurrenceId = $"email:{message.MessageId ?? ingest.Id.ToString()}:attachment:{attachmentOrdinal}",
                        EmailIngestId = ingest.Id,
                        FromEmail = metadata.FromEmail,
                        Subject = metadata.Subject,
                        SourceReceivedAtUtc = metadata.SourceReceivedAtUtc,
                        ClientEmail = metadata.ClientEmail,
                        LeadSource = "Email",
                        EmailSource = GetFileTypeLabel(ext)
                    };
                    var result = await ingestion.IngestAsync(
                        ms.ToArray(), part.FileName, config.BusinessUnitId,
                        ERP_RFQ_Automation.Extraction.ExtractionSourceType.Email,
                        batchId, priority: 0, attMetadata);
                    queued++;
                    _logger.LogInformation("Enqueued attachment {FileName} as job {JobId} ({Outcome}) for ingest {IngestId}.",
                        part.FileName, result.JobId, result.Outcome, ingest.Id);
                }
                catch (Exception ex)
                {
                    // Poison-attachment isolation: continue with the remaining documents.
                    _logger.LogError(ex, "Failed to enqueue attachment {FileName} for ingest {IngestId}.",
                        part.FileName, ingest.Id);
                }
            }

            return queued;
        }
        private async Task<bool> IsLikelyRFQEmailAsync(MimeMessage message)
        {
            var subject = message.Subject?.ToLowerInvariant() ?? "";
            var body = GetEmailBody(message).ToLowerInvariant();
            // Strong RFQ indicators
            var strongIndicators = new[]
            {
                "rfq", "request for quotation", "request for quote",
                "tender", "bid invitation", "procurement notice", "rfp",
                "invitation to bid", "itb", "price inquiry", "commercial inquiry",
                "purchasing inquiry", "bid request", "quote", "quotation", "fresh quote"
            };
            // Weak indicators (need multiple matches)
            var weakIndicators = new[]
            {
                "bid", "proposal",
                "purchase", "pricing", "delivery", "material list",
                "boq", "bill of quantities", "specification", "compliance",
                "datasheet", "sow", "scope of work", "technical requirement",
                "procurement supervisor", "procurement manager", "supply chain",
                "inquiry", "request", "project", "requirement", "urgent"
            };
            // Check strong indicators in subject (high priority)
            if (strongIndicators.Any(ind => subject.Contains(ind)))
                return true;
            // Check for combination of weak indicators
            var weakMatches = weakIndicators.Count(ind => subject.Contains(ind) || body.Contains(ind));
            if (weakMatches >= 2)
                return true;

            // NEW: If there are attachments, we only need 1 weak indicator (like "Project" or "Inquiry")
            if (message.Attachments.Any() && weakMatches >= 1)
                return true;
            // Check for RFQ numbers or reference patterns
            if (Regex.IsMatch(subject + " " + body, @"RFQ[#:\s-]*[A-Z0-9-]+", RegexOptions.IgnoreCase))
                return true;
            // Check for attachments with RFQ-related names
            if (message.Attachments.Any(att => att is MimePart part &&
                HasRFQKeywordsInFilename(part.FileName)))
                return true;
            // NEW: Quick scan of attachment CONTENT for RFQ keywords
            if (await QuickScanAttachmentContentAsync(message, strongIndicators))
                return true;
            _logger.LogDebug("Email does not appear to be RFQ: {Subject}", message.Subject);
            return false;
        }
        private bool IsDuplicateKeyException(DbUpdateException ex)
        {
            // SQL Server unique/PK violation error numbers.
            if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                return sqlEx.Number == 2627 || sqlEx.Number == 2601;
            }
            // PostgreSQL unique_violation (SQLSTATE 23505) — after the Npgsql
            // migration duplicate-key errors surface as PostgresException.
            if (ex.InnerException is Npgsql.PostgresException pgEx)
            {
                return pgEx.SqlState == "23505";
            }
            return false;
        }
        private async Task<(long leadId, string extractedText)> SaveLeadFromEmailAndAttachments(
            MimeMessage message, EmailIngest ingest, EmailConfiguration config,
            ErpRfqAutomationContext context, ILLMService llmService)
        {
            // Extract text from email body and attachments
            string extracted = GetEmailBody(message);
            string attachmentsText = "";
            var fileTypes = new HashSet<string>();
            // Process attachments
            var attachmentStreams = new List<(string FileName, MemoryStream Stream, string Extension)>();
            foreach (var att in message.Attachments)
            {
                if (att is not MimePart part || part.FileName == null) continue;
                var ext = Path.GetExtension(part.FileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext)) continue;
                fileTypes.Add(GetFileTypeLabel(ext));
                try
                {
                    var ms = new MemoryStream();
                    await part.Content.DecodeToAsync(ms);
                    ms.Position = 0;
                    attachmentStreams.Add((part.FileName, ms, ext));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read attachment: {FileName}", part.FileName);
                }
            }
            // Extract text from attachments in parallel for efficiency
            var attachmentTasks = attachmentStreams.Select(async (item) =>
            {
                try
                {
                    var text = await ExtractTextFromAttachment(item.Stream, item.Extension);
                    return !string.IsNullOrWhiteSpace(text) ? $"\n\n[Attachment: {item.FileName}]\n{text}" : "";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract text from: {FileName}", item.FileName);
                    return "";
                }
                finally
                {
            item.Stream.Dispose();
                }
            });
            var attachmentResults = await Task.WhenAll(attachmentTasks);
            // ING-04: separate genuine text from the scanned-PDF marker. The marker never reaches
            // the LLM; it only signals that a scanned/image-only PDF could not be OCR'd so the
            // ingest is routed to review instead of silently producing an empty lead.
            bool scannedPdfNeedsReview = false;
            var usableResults = new List<string>();
            foreach (var r in attachmentResults)
            {
                if (r.Contains(SCANNED_PDF_SENTINEL))
                {
                    scannedPdfNeedsReview = true;
                    continue;
                }
                usableResults.Add(r);
            }
            attachmentsText = string.Join("", usableResults);
            extracted += attachmentsText;
            if (scannedPdfNeedsReview)
            {
                // Tentative status: kept if no lead is created; overridden to Success by the caller
                // if a lead is still extracted from the remaining (e.g. email body) content.
                ingest.ParseStatus = STATUS_SCANNED_PDF;
                _logger.LogWarning("Scanned/image-only PDF detected that could not be OCR'd for: {Subject}", message.Subject);
            }
            string emailSource = fileTypes.Count > 0
                ? string.Join(", ", fileTypes.OrderBy(x => x))
                : "Text Only";
            
            // Apply smart token limiting to prevent LLM context length errors
            string emailBodyText = GetEmailBody(message);
            string limitedText = LimitTextForLLM(emailBodyText, attachmentsText, message.Subject ?? "");
            
            // Log if truncation occurred for visibility
            if (limitedText.Length < extracted.Length)
            {
                _logger.LogWarning(
                    "Text truncated for LLM: Original {Original} chars -> Limited {Limited} chars. Subject: {Subject}",
                    extracted.Length, limitedText.Length, message.Subject);
            }
            
            // AI Extraction with validation
            try
            {
                var ai = await llmService.ExtractLeadDataAsync(limitedText,
                    new AiCallContext(config.BusinessUnitId, AiPurposes.RfqExtraction,
                        $"email-ingest:{ingest.Id}", "rfq-extraction-v1"));
                // Smart validation: Lower threshold if email clearly looks like RFQ
                var minConfidence = HasStrongRFQIndicators(message, ai)
                    ? MIN_CONFIDENCE_WITH_VALIDATION
                    : MIN_CONFIDENCE_THRESHOLD;
                // Validate AI extraction
                if (ai == null || ai.OverallConfidence < minConfidence)
                {
                    _logger.LogWarning(
                        "AI extraction failed validation. Confidence: {Confidence:F2}, Required: {Required:F2}, Subject: {Subject}",
                        ai?.OverallConfidence ?? 0, minConfidence, message.Subject);
                    return (0, extracted);
                }
                // Additional validation: Must have at least RFQ number or buyer name
                if (string.IsNullOrWhiteSpace(ai.Rfqno) && string.IsNullOrWhiteSpace(ai.BuyersName))
                {
                    _logger.LogWarning(
                        "AI extraction missing critical fields but will save due to strong RFQ indicators: {Subject}",
                        message.Subject);
                    // If the email has strong RFQ indicators, try to extract basic info from subject
                    if (HasStrongRFQIndicators(message, ai))
                    {
                        ai = EnrichWithSubjectData(ai, message.Subject);
                    }
                    else
                    {
                        return (0, extracted);
                    }
                }
                // Check for existing lead with same RFQno to avoid duplicate leads
                bool isDuplicate = false;

                if (!string.IsNullOrWhiteSpace(ai.Rfqno))
                {
                    isDuplicate = await context.Leads.AnyAsync(l =>
                        l.Rfqno == ai.Rfqno &&
                        l.BuyersName == ai.BuyersName);
                }
                else if (!string.IsNullOrWhiteSpace(ai.BuyersName))
                {
                    // For no-RFQ# cases, check buyer + first item's main description + quantity
                    var firstItem = ai.Items.FirstOrDefault();
                    if (firstItem != null && firstItem.Quantity > 0)
                    {
                        isDuplicate = await context.Leads.AnyAsync(l =>
                            l.BuyersName == ai.BuyersName &&
                            l.NoOfLineItems == ai.Items.Count &&
                            l.LeadItems.Any(li =>
                                li.CommodityProduct == firstItem.CommodityProduct &&
                                li.Quantity == firstItem.Quantity));
                    }
                }

                if (isDuplicate)
                {
                    _logger.LogWarning("Skipping duplicate lead for RFQ: {Rfqno}", ai.Rfqno);
                    return (0, extracted);
                }
                
                // Use transaction to ensure atomic Lead + Items + Attachments creation
                using var transaction = await context.Database.BeginTransactionAsync();
                try
                {
                    // Parse dates
                    DateTime recDate = ParseDate(ai.RecDate) ?? message.Date.DateTime;
                    DateTime? bidClosingDate = ParseDate(ai.BidClosingDate);
                    DateTime? acknowledgmentDate = ParseDate(ai.AcknowledgmentDate);
                    DateTime? subDate = ParseDate(ai.SubDate);
                    var items = ai.Items.Where(x => x.Quantity > 0).ToList();
                    // Create lead record
                    var lead = new Lead
                    {
                        Rfqno = Truncate(ai.Rfqno, 100),
                        BuyersName = Truncate(ai.BuyersName, 255),
                        RecDate = recDate,
                        BidClosingDate = bidClosingDate,
                        BiddingDecision = Truncate(ai.BiddingDecision, 100),
                        AcknowledgmentDate = acknowledgmentDate,
                        SubDate = subDate,
                        HeaderRemarks = Truncate(BuildHeaderRemarks(message, ai, extracted), 8000),
                        OpportunityNo = Truncate(ai.OpportunityNo, 100),
                        NoOfLineItems = items.Count,
                        Rfqtype = Truncate(ai.Rfqtype, 50),
                        DurationAgreement = Truncate(ai.DurationAgreement, 100),
                        LeadSource = "Email",
                        EmailSource = emailSource,
                        Clientemail = message.From.Mailboxes.FirstOrDefault()?.Address,
                        Aiconfidence = (decimal?)ai.OverallConfidence,
                        ReviewVersion = 1,
                        RequiresCommercialReview = true,
                        CommercialFactsVerified = false,
                        CreatedBy = "System",
                        CreatedDate = DateTime.UtcNow,
                        BusinessUnitId = config.BusinessUnitId,
                        EmailIngestsId = ingest.Id
                    };
                    context.Leads.Add(lead);
                    await context.SaveChangesAsync();
                    
                    // Add line items
                    foreach (var aiItem in items)
                    {
                        context.LeadItems.Add(CreateLeadItem(lead.Id, aiItem));
                    }
                    if (items.Count > 0)
                        await context.SaveChangesAsync();
                    
                    // Save attachments
                    await SaveAttachmentsAsync(message, lead.Id, context);
                    
                    // Commit transaction - all or nothing
                    await transaction.CommitAsync();
                    
                    _logger.LogInformation(
                        "Successfully created lead {LeadId} from email: {Subject}",
                        lead.Id, message.Subject);
                    return (lead.Id, extracted);
                }
                catch (Exception txEx)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(txEx, "Transaction rolled back for email: {Subject}", message.Subject);
                    throw; // Re-throw to outer catch
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save RFQ from email: {Subject}", message.Subject);
                return (0, extracted);
            }
        }
        private string BuildHeaderRemarks(MimeMessage message, LeadExtractionResult ai, string extracted)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Email: From {message.From}, Subject: {message.Subject}, Date: {message.Date:yyyy-MM-dd}");
            
            if (!string.IsNullOrWhiteSpace(ai.HeaderRemarks))
            {
                sb.AppendLine();
                sb.AppendLine("Summary:");
                sb.AppendLine(ai.HeaderRemarks);
            }
            
            return sb.ToString();
        }
        private bool HasStrongRFQIndicators(MimeMessage message, LeadExtractionResult? ai)
        {
            var subject = message.Subject?.ToLowerInvariant() ?? "";
            var body = GetEmailBody(message).ToLowerInvariant();
            // Check for RFQ number pattern in subject (very strong indicator)
            if (Regex.IsMatch(subject, @"rfq\s*#?\s*([A-Z0-9-]+)", RegexOptions.IgnoreCase))
                return true;
            // Check if AI found an RFQ number even with low confidence
            if (!string.IsNullOrWhiteSpace(ai?.Rfqno))
                return true;
            // Check for tender/procurement reference numbers
            if (Regex.IsMatch(subject + " " + body,
                @"(tender|procurement|quotation)\s*#?\s*[A-Z0-9-]+", RegexOptions.IgnoreCase))
                return true;
            // Check for explicit RFQ keywords in subject
            var strongKeywords = new[] { "request for quotation", "rfq", "tender invitation" };
            if (strongKeywords.Any(k => subject.Contains(k)))
                return true;
            return false;
        }
        private LeadExtractionResult EnrichWithSubjectData(LeadExtractionResult ai, string subject)
        {
            // Try to extract RFQ number from subject if missing
            if (string.IsNullOrWhiteSpace(ai.Rfqno))
            {
                var match = Regex.Match(subject, @"rfq\s*#?\s*([A-Z0-9-]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    ai = ai with { Rfqno = match.Groups[1].Value.Trim() };
                    _logger.LogInformation("Extracted RFQ number from subject: {RfqNo}", ai.Rfqno);
                }
            }
            // Try to extract buyer/company name from subject
            if (string.IsNullOrWhiteSpace(ai.BuyersName))
            {
                // Look for company name patterns in subject
                var parts = subject.Split(new[] { '-', '|', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    // Often company name is before or after RFQ number
                    foreach (var part in parts)
                    {
                        if (!part.ToLowerInvariant().Contains("rfq") && part.Trim().Length > 3)
                        {
                            ai = ai with { BuyersName = part.Trim() };
                            _logger.LogInformation("Extracted buyer name from subject: {BuyerName}", ai.BuyersName);
                            break;
                        }
                    }
                }
            }
            return ai;
        }
        private LeadItem CreateLeadItem(long leadId, LeadItemData aiItem)
        {
            int? leadTime = int.TryParse(aiItem.LeadTime ?? "", out int lt) ? lt : null;
            DateTime? receivedDate = ParseDate(aiItem.ReceivedDate);
            DateTime? bidClosingDateLine = ParseDate(aiItem.BidClosingDateLine);
            return new LeadItem
            {
                LeadId = leadId,
                CompanyRef = Truncate(aiItem.CompanyRef, 100),
                CustomerAccountPortalId = Truncate(aiItem.CustomerAccountPortalId, 100),
                CustomerRfqno = Truncate(aiItem.CustomerRfqno, 100),
                ItemMaterialCode = Truncate(aiItem.ItemMaterialCode, 100),
                CommodityProduct = Truncate(aiItem.CommodityProduct, 200),
                BuyerName = Truncate(aiItem.BuyerName, 200),
                LineItemNo = Truncate(aiItem.LineItemNo, 50),
                ProductShortName = Truncate(aiItem.ProductShortName, 1000),
                Alternative = Truncate(aiItem.Alternative, 100),
                ProductShortDescription = Truncate(aiItem.ProductShortDescription, 1000),
                Currency = Truncate(aiItem.Currency, 10),
                UnitOfMeasure = Truncate(aiItem.UnitOfMeasure, 100),
                UnitPrice = aiItem.UnitPrice,
                Quantity = aiItem.Quantity ?? 0,
                StorageLocation = Truncate(aiItem.StorageLocation, 100),
                ManufacturerName = Truncate(aiItem.ManufacturerName, 200),
                ManufacturerPartNumber = Truncate(aiItem.ManufacturerPartNumber, 100),
                AlternateProductName = Truncate(aiItem.AlternateProductName, 200),
                AlternatePartNumber = Truncate(aiItem.AlternatePartNumber, 100),
                ItemText = Truncate(aiItem.ItemText, 2000),
                MaterialPotext = Truncate(aiItem.MaterialPotext, 2000),
                LeadTime = leadTime,
                ReceivedDate = receivedDate,
                BidClosingDateLine = bidClosingDateLine,
                Aiconfidence = (decimal?)(aiItem.ItemConfidence ?? 0.0)
            };
        }
        private async Task<string> ExtractTextFromAttachment(MemoryStream ms, string ext)
        {
            return ext switch
            {
                ".pdf" => ExtractTextFromPdf(ms.ToArray()),
                ".docx" => ExtractTextFromDocx(ms),
                ".xlsx" => ExtractTextFromExcel(ms),
                ".pptx" => ExtractTextFromPptx(ms),
                ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff" => ExtractTextFromImage(ms.ToArray()),
                _ => ""
            };
        }
        private string GetFileTypeLabel(string ext) => ext switch
        {
            ".pdf" => "PDF",
            ".docx" => "Word",
            ".xlsx" => "Excel",
            ".pptx" => "PowerPoint",
            ".jpg" or ".jpeg" => "JPEG",
            ".png" => "PNG",
            ".bmp" => "BMP",
            ".tiff" => "TIFF",
            _ => "Unknown"
        };
        // Text extraction methods
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

            // ING-04: image-only / scanned PDF yields (near) zero embedded text. Rasterize the
            // pages with Docnet and OCR them with Tesseract as a fallback.
            _logger.LogInformation(
                "PDF has little/no embedded text ({Chars} non-whitespace chars); attempting OCR fallback.",
                CountNonWhitespace(pdfPigText));
            var ocrText = TryOcrScannedPdf(bytes);
            if (!IsNearEmptyText(ocrText))
            {
                // OCR text is inherently lower-confidence: label it so the LLM and human reviewers
                // treat it with appropriate caution (the model naturally lowers confidence on noisy input).
                return "[OCR-EXTRACTED TEXT FROM SCANNED PDF - lower confidence, may contain recognition errors]\n" + ocrText;
            }

            // Could not obtain text from a scanned PDF (OCR unavailable/failed or blank page).
            // Emit a marker so the caller routes the ingest to review instead of a silent empty lead.
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
                // Serialize native pdfium + Tesseract access (neither is thread-safe) because
                // attachment extraction runs in parallel.
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
                            // Composite any transparency over white for cleaner OCR; returns BGRA.
                            var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover());
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
                // Docnet native lib unavailable (unsupported RID), malformed/encrypted PDF, etc.
                _logger.LogWarning(ex, "Scanned-PDF OCR fallback unavailable or failed.");
                return "";
            }
        }

        /// <summary>Encodes a top-down BGRA buffer as a 24-bit (BGR) bottom-up BMP that Leptonica/Tesseract can read.</summary>
        private static byte[] BgraToBmp24(byte[] bgra, int width, int height)
        {
            int rowSize = ((24 * width + 31) / 32) * 4; // rows padded to a 4-byte boundary
            int pixelDataSize = rowSize * height;
            const int headerSize = 54;
            var bmp = new byte[headerSize + pixelDataSize];

            // BITMAPFILEHEADER
            bmp[0] = 0x42; // 'B'
            bmp[1] = 0x4D; // 'M'
            WriteInt32LE(bmp, 2, bmp.Length);
            WriteInt32LE(bmp, 10, headerSize);
            // BITMAPINFOHEADER
            WriteInt32LE(bmp, 14, 40);
            WriteInt32LE(bmp, 18, width);
            WriteInt32LE(bmp, 22, height); // positive height -> bottom-up
            WriteInt16LE(bmp, 26, 1);      // planes
            WriteInt16LE(bmp, 28, 24);     // bits per pixel
            WriteInt32LE(bmp, 30, 0);      // BI_RGB (uncompressed)
            WriteInt32LE(bmp, 34, pixelDataSize);
            WriteInt32LE(bmp, 38, 2835);   // ~72 DPI (x)
            WriteInt32LE(bmp, 42, 2835);   // ~72 DPI (y)

            int srcStride = width * 4;
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * srcStride;                               // source is top-down
                int dst = headerSize + (height - 1 - y) * rowSize;        // dest is bottom-up
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
                        sb.Append(text.Text);
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
        // Utility methods
        private string LimitTextForLLM(string emailBody, string attachmentsText, string subject)
        {
            var totalChars = emailBody.Length + attachmentsText.Length + subject.Length;

            // If under limit, return as-is
            if (totalChars <= MAX_CHARS_FOR_LLM)
            {
                return $"Subject: {subject}\n\nBody:\n{emailBody}\n\nAttachments:\n{attachmentsText}";
            }

            _logger.LogWarning(
                "Text exceeds LLM limit. Total: {Total} chars, Limit: {Limit} chars. Truncating intelligently.",
                totalChars, MAX_CHARS_FOR_LLM);

            var sb = new StringBuilder();
            sb.AppendLine($"Subject: {subject}");
            sb.AppendLine();

            // Priority 1: Email body (always include, truncate if needed)
            sb.AppendLine("Body:");
            if (emailBody.Length > PRIORITY_EMAIL_BODY_CHARS)
            {
                sb.AppendLine(emailBody.Substring(0, PRIORITY_EMAIL_BODY_CHARS));
                sb.AppendLine($"[... EMAIL BODY TRUNCATED - {emailBody.Length - PRIORITY_EMAIL_BODY_CHARS} chars omitted ...]");
            }
            else
            {
                sb.AppendLine(emailBody);
            }
            sb.AppendLine();

            // Priority 2: Attachments (distribute remaining space)
            var remainingChars = MAX_CHARS_FOR_LLM - sb.Length;
            sb.AppendLine("Attachments:");

            if (attachmentsText.Length > remainingChars)
            {
                // Extract first N chars of attachments (likely has most important info)
                sb.AppendLine(attachmentsText.Substring(0, Math.Min(remainingChars, attachmentsText.Length)));
                sb.AppendLine($"[... ATTACHMENTS TRUNCATED - {attachmentsText.Length - remainingChars} chars omitted ...]");
                sb.AppendLine("NOTE: Large document detected. Manual review may be needed for complete details.");
            }
            else
            {
                sb.AppendLine(attachmentsText);
            }

            return sb.ToString();
        }
        // Helper methods for RFQ detection in attachments
        private bool HasRFQKeywordsInFilename(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var lowerFileName = fileName.ToLowerInvariant();
            var keywords = new[] { 
                "rfq", "rfp", "tender", "quotation", "procurement", 
                "quote", "pricing", "boq", "bill_of_quantities", 
                "material_list", "spec", "drawing", "itb", "request" 
            };

            if (keywords.Any(k => lowerFileName.Contains(k)))
                return true;

            return Regex.IsMatch(fileName, @"(RFQ|RFP|ITB|Tender)[#:\s-]*[A-Z0-9-]+", RegexOptions.IgnoreCase);
        }
        private async Task<bool> QuickScanAttachmentContentAsync(MimeMessage message, string[] keywords)
        {
            // Only scan first 1000 chars of each attachment for performance
            const int QUICK_SCAN_LENGTH = 1000;

            foreach (var att in message.Attachments)
            {
                if (att is not MimePart part || part.FileName == null)
                    continue;

                var ext = Path.GetExtension(part.FileName).ToLowerInvariant();
                if (!IsSupportedExtension(ext))
                    continue;

                try
                {
                    var ms = new MemoryStream();
                    await part.Content.DecodeToAsync(ms);
                    ms.Position = 0;

                    // Quick extraction (first chunk only)
                    string snippet = await ExtractTextSnippetAsync(ms, ext, QUICK_SCAN_LENGTH);
                    ms.Dispose();

                    // Check for RFQ keywords in snippet
                    var lowerSnippet = snippet.ToLowerInvariant();
                    if (keywords.Any(kw => lowerSnippet.Contains(kw)))
                    {
                        _logger.LogInformation("RFQ keyword found in attachment content: {FileName}", part.FileName);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during quick scan of attachment: {FileName}", part.FileName);
                    // Continue checking other attachments
                }
            }

            return false;
        }
        private async Task<string> ExtractTextSnippetAsync(MemoryStream ms, string ext, int maxLength)
        {
            try
            {
                string fullText = await ExtractTextFromAttachment(ms, ext);
                return fullText.Length > maxLength
                    ? fullText.Substring(0, maxLength)
                    : fullText;
            }
            catch
            {
                return "";
            }
        }
        private string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }
        private DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var formats = new[]
            {
                "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy",
                "dd-MM-yyyy", "d/M/yyyy", "yyyy/MM/dd"
            };
            return DateTime.TryParseExact(s.Trim(), formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
        private string GetEmailBody(MimeMessage message)
        {
            var body = message.GetTextBody(TextFormat.Plain);
            if (!string.IsNullOrWhiteSpace(body)) return body;
            var html = message.GetTextBody(TextFormat.Html);
            return html != null
                ? Regex.Replace(html, "<.*?>", " ")
                    .Replace("&nbsp;", " ")
                    .Replace("\r\n", "\n")
                : "";
        }
        private string SanitizeFileName(string fileName)
        {
            return string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(),
                StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");
        }
        private bool IsSupportedExtension(string ext) => ext switch
        {
            ".pdf" or ".doc" or ".docx" or ".xlsx" or ".pptx" or
            ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff" => true,
            _ => false
        };
        private async Task SaveAttachmentsAsync(MimeMessage message, long leadId,
            ErpRfqAutomationContext context)
        {
            // FIXED: Removed parallel Task.Run to fix DbContext thread-safety issue
            // DbContext is NOT thread-safe - process sequentially instead
            foreach (var entity in message.Attachments)
            {
                if (entity is not MimePart part || part.FileName == null) continue;
                if (part.FileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)) continue;
                
                var safeName = SanitizeFileName(part.FileName);
                var fileName = $"{leadId}_{Guid.NewGuid()}_{safeName}";
                var relativePath = Path.Combine("Uploads", "RFQ_Attachments", fileName);
                var physicalPath = Path.Combine(_attachmentPath, fileName);

                try
                {
                    // Check file size BEFORE writing to disk (security fix)
                    long size = 0;
                    using (var tempStream = new MemoryStream())
                    {
                        await part.Content.DecodeToAsync(tempStream);
                        size = tempStream.Length;
                        
                        if (size > MAX_ATTACHMENT_SIZE)
                        {
                            _logger.LogWarning("Skipping large attachment: {File} ({Size} bytes)",
                                safeName, size);
                            continue; // Skip this attachment
                        }
                        
                        // Write to disk only if size is acceptable
                        tempStream.Position = 0;
                        await using var fileStream = File.Create(physicalPath);
                        await tempStream.CopyToAsync(fileStream);
                        await fileStream.FlushAsync();
                    }

                    // Add to database (thread-safe since we're sequential now)
                    context.Attachments.Add(new Attachment
                    {
                        ParentType = "Lead",
                        ParentId = leadId,
                        FileName = safeName,
                        FilePath = relativePath,
                        MimeType = part.ContentType?.MimeType,
                        FileSize = size,
                        ContentType = part.ContentType?.MediaType,
                        CreatedOn = DateTime.UtcNow,
                        UploadedDate = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save attachment: {File}", safeName);
                    // Clean up file if it was created
                    if (File.Exists(physicalPath))
                    {
                        try { File.Delete(physicalPath); } catch { /* Ignore cleanup errors */ }
                    }
                }
            }
            
            // Save all attachments at once
            if (message.Attachments.Any())
                await context.SaveChangesAsync();
        }
        public async Task SendEmailAsync(string to, string subject, string body, List<(string FileName, byte[] FileContent, string ContentType)> attachments = null, string fromEmail = null, long? businessUnitId = null)
        {
            var query = _context.EmailConfigurations.Where(e => e.IsActive && e.Protocol.ToUpper() == "SMTP");

            if (!string.IsNullOrEmpty(fromEmail))
            {
                query = query.Where(e => e.EmailAddress == fromEmail);
            }

            if (businessUnitId.HasValue)
            {
                query = query.Where(e => e.BusinessUnitId == businessUnitId.Value);
            }

            var config = await query.FirstOrDefaultAsync();

            // Fallback within the SAME business unit only: if a specific fromEmail was requested but
            // not found, fall back to any active SMTP config belonging to that business unit.
            // We must never fall back to another tenant's SMTP account — doing so would send this
            // tenant's quote (customer names, pricing, terms) through a different tenant's mail server.
            if (config == null && !string.IsNullOrEmpty(fromEmail) && businessUnitId.HasValue)
            {
                config = await _context.EmailConfigurations
                    .FirstOrDefaultAsync(e => e.IsActive && e.Protocol.ToUpper() == "SMTP" && e.BusinessUnitId == businessUnitId);
            }

            if (config == null)
            {
                throw new InvalidOperationException(
                    businessUnitId.HasValue
                        ? $"No active SMTP configuration found for business unit {businessUnitId.Value}. Configure an email account for this business unit before sending."
                        : "No active SMTP configuration found for sending emails.");
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(config.ConfigurationName, config.EmailAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = body };

            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    builder.Attachments.Add(attachment.FileName, attachment.FileContent, ContentType.Parse(attachment.ContentType));
                }
            }

            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(config.Host, config.Port, config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(config.EmailAddress, config.Password);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {To} using {From} (Host: {Host}, Port: {Port}, SSL: {SSL})", to, config.EmailAddress, config.Host, config.Port, config.UseSsl);
                throw;
            }
        }
    }
}
