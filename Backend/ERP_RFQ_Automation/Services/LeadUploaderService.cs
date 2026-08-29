using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Extraction.Quantities;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services
{
    public class LeadUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<LeadUploaderService> _logger;

        private readonly ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService _identity;

        // Required, not optional-with-null — the rule ManualUploadService states beside its own
        // identity collaborator, and for the same reason: an optional dependency is always supplied
        // in production and always absent in tests, so the step that must always run becomes the
        // step nothing exercises. That is exactly how client resolution came to be missing from
        // every upload door while the extraction door had it.
        private readonly ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService _customerResolution;

        public LeadUploaderService(ErpRfqAutomationContext context, ILogger<LeadUploaderService> logger,
            ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService identity,
            ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService customerResolution)
        {
            _context = context;
            _logger = logger;
            _identity = identity;
            _customerResolution = customerResolution;
        }

        public async Task<byte[]> GenerateTemplateAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("LeadTemplate");

            string[] headers = {
                "RFQ No*", "Buyer Name*", "Rec Date (YYYY-MM-DD)*", "Bid Closing Date (YYYY-MM-DD)",
                "Product Name*", "Quantity*", "Unit Price", "Currency",
                "Manufacturer", "Part Number", "Lead Time (Days)"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
            }

            // Sample Data
            ws.Cells[2, 1].Value = "RFQ-2024-001";
            ws.Cells[2, 2].Value = "Tech Corp";
            ws.Cells[2, 3].Value = DateTime.Now.ToString("yyyy-MM-dd");
            ws.Cells[2, 4].Value = DateTime.Now.AddDays(7).ToString("yyyy-MM-dd");
            ws.Cells[2, 5].Value = "Industrial Sensor A1";
            ws.Cells[2, 6].Value = 10;
            ws.Cells[2, 7].Value = 150.00;
            ws.Cells[2, 8].Value = "USD";
            ws.Cells[2, 9].Value = "SensorTech";
            ws.Cells[2, 10].Value = "ST-A1-X";
            ws.Cells[2, 11].Value = 15;

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

                /// <summary>
        /// Bulk import entry point. The whole import is run as one retriable unit so that the
        /// transaction it opens is owned by the configured execution strategy — see
        /// <see cref="ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction"/> for the
        /// defect this closes (every upload returned 500 against PostgreSQL).
        /// </summary>
        public Task<ServiceResult<string>> UploadTemplateAsync(Stream fileStream, long businessUnitId, string createdBy) =>
            ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction.ExecuteAsync(
                _context, fileStream, () => UploadTemplateCoreAsync(fileStream, businessUnitId, createdBy));

        private async Task<ServiceResult<string>> UploadTemplateCoreAsync(Stream fileStream, long businessUnitId, string createdBy)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];

            int rowCount = ws.Dimension?.Rows ?? 0;
            if (rowCount < 2) return ServiceResult<string>.CreateFailure("The uploaded file is empty or missing data.");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Create a dummy EmailIngest for this upload batch
                var dummyIngest = new EmailIngest
                {
                    MessageId = $"Upload_{Guid.NewGuid()}",
                    EmailSubject = "Excel Lead Upload",
                    FromEmail = "system@excel.upload",
                    ToEmail = "system@rfq.com",
                    EmailConfigurationId = (await _context.EmailConfigurations.FirstOrDefaultAsync(e => e.BusinessUnitId == businessUnitId && e.IsActive))?.Id ?? 1,
                    CreatedOn = DateTime.UtcNow,
                    ParseStatus = "Success",
                    ParsedAt = DateTime.UtcNow
                };
                _context.EmailIngests.Add(dummyIngest);
                await _context.SaveChangesAsync();

                var groupedLeads = new Dictionary<string, (Lead Lead, List<LeadItem> Items)>();

                // Leads with at least one row whose quantity could not be read. These get
                // RequiresCommercialReview so the extraction-review gate actually engages —
                // it is keyed on that flag, and this upload door never set it, which is why
                // a fabricated quantity could reach a customer quote unseen.
                var quantityNeedsReview = new HashSet<string>(StringComparer.Ordinal);

                for (int row = 2; row <= rowCount; row++)
                {
                    var rfqNo = ws.Cells[row, 1].Text?.Trim();
                    var buyerName = ws.Cells[row, 2].Text?.Trim();
                    var productName = ws.Cells[row, 5].Text?.Trim();

                    if (string.IsNullOrEmpty(rfqNo) || string.IsNullOrEmpty(buyerName) || string.IsNullOrEmpty(productName))
                        continue;

                    string leadKey = $"{rfqNo}_{buyerName}".ToLowerInvariant();

                    if (!groupedLeads.ContainsKey(leadKey))
                    {
                        var recDateStr = ws.Cells[row, 3].Text?.Trim();
                        var bidClosingStr = ws.Cells[row, 4].Text?.Trim();

                        var lead = new Lead
                        {
                            Rfqno = rfqNo,
                            BuyersName = buyerName,
                            RecDate = ParseDate(recDateStr) ?? DateTime.UtcNow,
                            BidClosingDate = ParseDate(bidClosingStr),
                            LeadSource = "Excel Upload",
                            EmailSource = "Excel",
                            // No fabricated client email. "excel@upload.com" used to be written
                            // here, and reconciliation derives the customer scope from
                            // CustomerId ?? Clientemail ?? BuyersName — a shared fake address
                            // would put every bulk-imported lead in ONE customer scope, letting
                            // two different buyers who reuse a reference string merge as one
                            // inquiry. Null means the buyer name (a real, typed fact) is the
                            // scope, which is the truth this template actually captured.
                            Clientemail = null,
                            // Aiconfidence is deliberately NOT set. Nothing was extracted
                            // here — a human typed these cells into Nexora's own template,
                            // so there is no prediction to be confident about. Writing 1.0
                            // put a fabricated "100%" into the same column the review screen
                            // and the dashboard read as a model score, and those rows then
                            // pulled the tenant's average confidence upward. Null means
                            // "not applicable", which is the truth.
                            CreatedBy = createdBy,
                            CreatedDate = DateTime.UtcNow,
                            BusinessUnitId = businessUnitId,
                            EmailIngestsId = dummyIngest.Id
                        };
                        groupedLeads[leadKey] = (lead, new List<LeadItem>());
                    }

                    // QUANTITY — never invent a number. This previously read
                    //     Quantity = int.TryParse(cell.Text, out var qty) ? qty : 1
                    // and int.TryParse's default NumberStyles rejects thousands separators,
                    // any decimal point, and any trailing unit. So "1,000" became 1, as did
                    // "2,500 PCS", "12.00" and "2.5". The customer asked for a thousand and
                    // the quote said one — plausible enough to pass review, and the review
                    // gate was inert on this door anyway (RequiresCommercialReview was never
                    // set here). 875 of 2,966 production lines carried quantity 1.
                    //
                    // Unreadable now stores NULL — the column can finally say "unknown" — and
                    // raises RequiresCommercialReview on the parent lead so a human is actually
                    // asked. It stored 0 while the column was NOT NULL, which every downstream
                    // guard rejected but which still read, on a screen, as a demand for nothing.
                    // Fractional measured quantities are valid. Anything that cannot fit the
                    // numeric(20,6) contract without rounding is held for review.
                    var quantityReading = QuantityParser.Parse(ws.Cells[row, 6].Text, allowFractional: true);
                    if (quantityReading.RequiresReview
                        || quantityReading.Value is not { } parsedQuantity
                        || !QuantityParser.FitsPersistedQuantity(parsedQuantity))
                    {
                        quantityNeedsReview.Add(leadKey);
                        _logger.LogWarning(
                            "Row {Row}: quantity {Raw} could not be read ({Origin}); the line is held for review rather than defaulted.",
                            row, ws.Cells[row, 6].Text, quantityReading.Origin);
                    }

                    var item = new LeadItem
                    {
                        ProductShortName = productName,
                        Quantity = quantityReading.Value is { } quantity
                            && QuantityParser.FitsPersistedQuantity(quantity) ? quantity : null,
                        UnitPrice = decimal.TryParse(ws.Cells[row, 7].Text, out var price) ? price : null,
                        Currency = ws.Cells[row, 8].Text?.Trim(),
                        ManufacturerName = ws.Cells[row, 9].Text?.Trim(),
                        ManufacturerPartNumber = ws.Cells[row, 10].Text?.Trim(),
                        LeadTime = int.TryParse(ws.Cells[row, 11].Text, out var lt) ? lt : null
                        // Aiconfidence left null — see the lead above. A typed cell has no
                        // extraction confidence.
                    };

                    groupedLeads[leadKey].Items.Add(item);
                }

                int leadCount = 0, revisionCount = 0, duplicateCount = 0, reviewCount = 0;
                // Leads this upload created or amended, for the client resolution that runs
                // after the commit. Reconciliation now owns lead creation, so the id comes
                // from its outcome rather than from lead.Id.
                var importedLeadIds = new List<long>();
                int itemViewCount = 0;

                // The batch ingest was stamped "Success" at creation, BEFORE any row was
                // parsed. The review queue lists a lead with an EmailIngest only while that
                // ingest reads "NeedsReview", and review submit refuses otherwise — so a
                // held lead under a "Success" ingest was blocked from converting yet
                // invisible to every reviewer: governed into a queue nobody can see.
                // (Known edge: all leads in one upload share this ingest, so approving one
                // flips it to "Success" for the rest; acceptable for now because a reviewer
                // works a batch together, and the conversion gate on each lead still holds.)
                if (quantityNeedsReview.Count > 0)
                    dummyIngest.ParseStatus = "NeedsReview";

                // FULL reconciliation per imported inquiry, in the SAME transaction — not a bare
                // identity baseline. This door used to add Leads directly and then call
                // EstablishBaselineRevisionAsync, which by design does NO matching: a revised RFQ
                // re-imported through this template always became a second, unlinked lead. This
                // door is NOT production-fenced (it is deterministic and never touches the
                // unified extraction queue), so it was the one live amendment fork left after the
                // extraction-worker door started reconciling. ReconcileAsync now decides: a
                // genuinely new row creates a lead with revision 1 exactly as before, an amended
                // row versions its canonical lead, a byte-identical row is recorded as a
                // duplicate occurrence, and an ambiguous row is held for possible-match review.
                var reconciliationBatchId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(
                    $"bulk-upload-batch:{businessUnitId}:{dummyIngest.Id}")));
                foreach (var (leadKey, entry) in groupedLeads)
                {
                    var lead = entry.Lead;
                    lead.NoOfLineItems = entry.Items.Count;

                    // Engage the review gate for this lead when any quantity was unreadable.
                    // LeadRepository and LeadConversionIntelligence both refuse RFQ conversion
                    // while RequiresCommercialReview is set and CommercialFactsVerified is not,
                    // so this is what stops a held line from being quoted.
                    if (quantityNeedsReview.Contains(leadKey))
                        lead.RequiresCommercialReview = true;

                    // Items travel on the navigation: reconciliation reads candidate.LeadItems
                    // for the fingerprint and similarity, and it alone decides whether this
                    // candidate row is persisted (New) or projected onto an existing lead.
                    foreach (var item in entry.Items)
                        lead.LeadItems.Add(item);

                    var reconciliation = await _identity.ReconcileAsync(lead,
                        new ERP_RFQ_Automation.LeadIdentity.LeadIntakeDescriptor(
                            BatchId: reconciliationBatchId,
                            SourceChannel: "BulkUpload",
                            // Stable per (ingest row, workbook inquiry): a retry of this unit of
                            // work replays instead of double-writing. The key is hashed because
                            // leadKey is free text from workbook cells.
                            IdempotencyKey: $"bulk-upload:{businessUnitId}:ingest:{dummyIngest.Id}:"
                                + RowKeyHash(leadKey),
                            ExternalSourceId: null, EmailThreadId: null, SourceSystem: "BulkUpload",
                            Sender: null, Subject: "Excel Lead Upload", OriginalFileName: null,
                            MimeType: null, FileSize: null,
                            // The typed row content, hashed deterministically: what lets an
                            // identical re-import be recognised as the same document even when
                            // the recency window has long since moved past the original.
                            ContentHash: RowContentHash(lead),
                            SourceDocumentId: null, ExtractionJobId: null,
                            SourceReceivedAtUtc: DateTimeOffset.UtcNow, IngestedAtUtc: DateTimeOffset.UtcNow,
                            // A human typed these cells; nothing was extracted or predicted.
                            ProcessingPath: ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.Deterministic,
                            ExternalAiUsed: false, ExternalCost: null,
                            ActorType: "User", ActorId: createdBy,
                            CorrelationId: $"bulk-upload:{businessUnitId}:ingest:{dummyIngest.Id}"));

                    switch (reconciliation.Classification)
                    {
                        case ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.New:
                            leadCount++;
                            itemViewCount += entry.Items.Count;
                            importedLeadIds.Add(reconciliation.LeadId);
                            break;
                        case ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.Revision:
                            revisionCount++;
                            itemViewCount += entry.Items.Count;
                            importedLeadIds.Add(reconciliation.LeadId);
                            // The projection copies the lines (including any NULL quantity) onto
                            // the canonical lead but not the review flag; raise it there or the
                            // unreadable quantity sails past the conversion gate on the lead the
                            // buyer will actually be quoted from.
                            if (lead.RequiresCommercialReview)
                            {
                                var canonical = await _context.Leads.SingleAsync(x =>
                                    x.BusinessUnitId == businessUnitId && x.Id == reconciliation.LeadId);
                                canonical.RequiresCommercialReview = true;
                                canonical.CommercialFactsVerified = false;
                                await _context.SaveChangesAsync();
                            }
                            break;
                        case ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.ExactDuplicate:
                            duplicateCount++;
                            break;
                        default:
                            reviewCount++;
                            dummyIngest.ParseStatus = "NeedsReview";
                            break;
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Client resolution runs AFTER the commit, deliberately outside the upload
                // transaction. It writes its own rows, and a failure to work out who the buyer is
                // must never roll back an import the user has already done — the leads and their
                // lines are the work; the client link is a decision that can be re-run.
                //
                // Until now no upload door resolved at all: only the extraction worker did, so a
                // bulk-imported lead was born with a NULL CustomerMatchReasonCode — not "no match
                // found" but never evaluated — and could never be qualified or converted, because
                // both require a client. Exactly the shape of the identity gap fixed above it.
                await ERP_RFQ_Automation.CustomerResolution.UploadedLeadResolution.ResolveAsync(
                    _customerResolution, businessUnitId, importedLeadIds, _logger, "bulk-upload");

                var summary = new StringBuilder(
                    $"{leadCount} leads and {itemViewCount} items imported successfully.");
                if (revisionCount > 0) summary.Append(
                    $" {revisionCount} row group(s) matched existing leads and were applied as new revisions.");
                if (duplicateCount > 0) summary.Append(
                    $" {duplicateCount} row group(s) were exact duplicates of existing leads and were recorded, not re-imported.");
                if (reviewCount > 0) summary.Append(
                    $" {reviewCount} row group(s) closely match existing inquiries and were held for possible-match review.");
                return ServiceResult<string>.CreateSuccess(summary.ToString());
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Lead Excel upload failed.");
                return ServiceResult<string>.CreateFailure($"Import failed: {ex.Message}");
            }
        }

        // Shared with every other ingestion door — see RfqDateParser.
        private DateTime? ParseDate(string s) => Extraction.RfqDateParser.Parse(s);

        /// <summary>Stable, column-safe token for a workbook row group's free-text key.</summary>
        private static string RowKeyHash(string leadKey) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leadKey)))[..32].ToLowerInvariant();

        /// <summary>
        /// Deterministic content hash for one imported inquiry: every typed fact of the header
        /// and its lines, in row order, separated by an unambiguous delimiter. This template has
        /// no source document bytes to hash, but the typed cells ARE the document — hashing them
        /// gives an identical re-import the same content identity on every run.
        /// </summary>
        private static string RowContentHash(Lead lead)
        {
            var sb = new StringBuilder()
                .Append(lead.Rfqno).Append('\u001f')
                .Append(lead.BuyersName).Append('\u001f')
                .Append(lead.BidClosingDate?.ToString("O")).Append('\u001f');
            foreach (var item in lead.LeadItems)
                sb.Append(item.ProductShortName).Append('\u001f')
                  .Append(item.Quantity?.ToString() ?? "null").Append('\u001f')
                  .Append(item.UnitPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null").Append('\u001f')
                  .Append(item.Currency).Append('\u001f')
                  .Append(item.ManufacturerName).Append('\u001f')
                  .Append(item.ManufacturerPartNumber).Append('\u001f')
                  .Append(item.LeadTime?.ToString() ?? "null").Append('');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }
    }
}
