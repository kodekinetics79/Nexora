using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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

        public LeadUploaderService(ErpRfqAutomationContext context, ILogger<LeadUploaderService> logger,
            ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService identity)
        {
            _context = context;
            _logger = logger;
            _identity = identity;
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

        public async Task<ServiceResult<string>> UploadTemplateAsync(Stream fileStream, long businessUnitId, string createdBy)
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
                            Clientemail = "excel@upload.com",
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
                    // Unreadable now stores 0, which every downstream guard already rejects
                    // (QuoteService line validation, the reviewer's approve check, and the
                    // new CHECK constraints), AND raises RequiresCommercialReview on the
                    // parent lead so a human is actually asked. Fractional values are treated
                    // as unreadable rather than truncated: Quantity is an int column, and
                    // silently turning 2.5 into 2 is a 20% under-quote.
                    var quantityReading = QuantityParser.Parse(ws.Cells[row, 6].Text, allowFractional: false);
                    if (quantityReading.RequiresReview)
                    {
                        quantityNeedsReview.Add(leadKey);
                        _logger.LogWarning(
                            "Row {Row}: quantity {Raw} could not be read ({Origin}); the line is held for review rather than defaulted.",
                            row, ws.Cells[row, 6].Text, quantityReading.Origin);
                    }

                    var item = new LeadItem
                    {
                        ProductShortName = productName,
                        Quantity = quantityReading.HasValue ? (int)quantityReading.Value!.Value : 0,
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

                int leadCount = 0;
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
                    _context.Leads.Add(lead);
                    await _context.SaveChangesAsync(); // Save to get Lead.Id
                    importedLeadIds.Add(lead.Id);

                    foreach (var item in entry.Items)
                    {
                        item.LeadId = lead.Id;
                        _context.LeadItems.Add(item);
                        itemViewCount++;
                    }
                    leadCount++;
                }

                await _context.SaveChangesAsync();

                // Canonical identity for every imported lead, in the SAME transaction.
                //
                // This door used to add Leads directly and never call the identity service, so
                // every bulk-imported lead was born with line items and NO revision — and was
                // therefore permanently unconvertible to an RFQ. Run after the final
                // SaveChangesAsync so revision 1 records the lines.
                foreach (var importedLeadId in importedLeadIds)
                    await _identity.EstablishBaselineRevisionAsync(businessUnitId, importedLeadId,
                        new ERP_RFQ_Automation.LeadIdentity.LeadIdentityBaselineRequest(
                            "BulkUpload",
                            "Bulk lead import: commercial facts were supplied in the uploaded workbook. "
                            + "Canonical identity established at creation.",
                            "User", "System", $"bulk-upload:{businessUnitId}:{importedLeadId}"));

                await transaction.CommitAsync();

                return ServiceResult<string>.CreateSuccess($"{leadCount} leads and {itemViewCount} items imported successfully.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Lead Excel upload failed.");
                return ServiceResult<string>.CreateFailure($"Import failed: {ex.Message}");
            }
        }

        private DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy", "d/M/yyyy", "yyyy/MM/dd" };
            return DateTime.TryParseExact(s.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
    }
}
