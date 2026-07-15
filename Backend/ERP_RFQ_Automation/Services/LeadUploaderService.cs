using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        public LeadUploaderService(ErpRfqAutomationContext context, ILogger<LeadUploaderService> logger)
        {
            _context = context;
            _logger = logger;
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
                            Aiconfidence = 1.0m, // Manual upload is 100% confident
                            CreatedBy = createdBy,
                            CreatedDate = DateTime.UtcNow,
                            BusinessUnitId = businessUnitId,
                            EmailIngestsId = dummyIngest.Id
                        };
                        groupedLeads[leadKey] = (lead, new List<LeadItem>());
                    }

                    var item = new LeadItem
                    {
                        ProductShortName = productName,
                        Quantity = int.TryParse(ws.Cells[row, 6].Text, out var qty) ? qty : 1,
                        UnitPrice = decimal.TryParse(ws.Cells[row, 7].Text, out var price) ? price : null,
                        Currency = ws.Cells[row, 8].Text?.Trim(),
                        ManufacturerName = ws.Cells[row, 9].Text?.Trim(),
                        ManufacturerPartNumber = ws.Cells[row, 10].Text?.Trim(),
                        LeadTime = int.TryParse(ws.Cells[row, 11].Text, out var lt) ? lt : null,
                        Aiconfidence = 1.0m
                    };

                    groupedLeads[leadKey].Items.Add(item);
                }

                int leadCount = 0;
                int itemViewCount = 0;

                foreach (var entry in groupedLeads.Values)
                {
                    var lead = entry.Lead;
                    lead.NoOfLineItems = entry.Items.Count;
                    _context.Leads.Add(lead);
                    await _context.SaveChangesAsync(); // Save to get Lead.Id

                    foreach (var item in entry.Items)
                    {
                        item.LeadId = lead.Id;
                        _context.LeadItems.Add(item);
                        itemViewCount++;
                    }
                    leadCount++;
                }

                await _context.SaveChangesAsync();
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
