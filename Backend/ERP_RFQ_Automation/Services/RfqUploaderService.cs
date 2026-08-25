using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services
{
    public class RfqUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<RfqUploaderService> _logger;
        private readonly ICanonicalRfqNormalizer _canonicalNormalizer;

        public RfqUploaderService(
            ErpRfqAutomationContext context,
            ILogger<RfqUploaderService> logger,
            ICanonicalRfqNormalizer canonicalNormalizer)
        {
            _context = context;
            _logger = logger;
            _canonicalNormalizer = canonicalNormalizer;
        }

        public async Task<byte[]> GenerateTemplateAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("RFQTemplate");

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
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
            }

            // Sample Data
            ws.Cells[2, 1].Value = "RFQ-ABC-001";
            ws.Cells[2, 2].Value = "Global Industries";
            ws.Cells[2, 3].Value = DateTime.Now.ToString("yyyy-MM-dd");
            ws.Cells[2, 4].Value = DateTime.Now.AddDays(14).ToString("yyyy-MM-dd");
            ws.Cells[2, 5].Value = "Contactor 220V 40A";
            ws.Cells[2, 6].Value = 5;
            ws.Cells[2, 7].Value = 120.50;
            ws.Cells[2, 8].Value = "USD";
            ws.Cells[2, 9].Value = "Schneider Electric";
            ws.Cells[2, 10].Value = "LC1D40AM7";
            ws.Cells[2, 11].Value = 10;

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
            Task.FromResult(ServiceResult<string>.CreateFailure(
                "Direct spreadsheet-to-RFQ creation is retired. Use governed Lead intake and RFQ Promotion."));

        private async Task<ServiceResult<string>> UploadTemplateCoreAsync(Stream fileStream, long businessUnitId, string createdBy)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];

            int rowCount = ws.Dimension?.Rows ?? 0;
            if (rowCount < 2) return ServiceResult<string>.CreateFailure("The uploaded file is empty or missing data.");

            var rows = new List<RfqSpreadsheetRow>();
            for (int row = 2; row <= rowCount; row++)
            {
                rows.Add(new RfqSpreadsheetRow
                {
                    RowNumber = row,
                    SourceDocumentName = ws.Name,
                    RfqNo = ws.Cells[row, 1].Text,
                    BuyerName = ws.Cells[row, 2].Text,
                    ReceivedDate = ws.Cells[row, 3].Text,
                    BidClosingDate = ws.Cells[row, 4].Text,
                    ProductName = ws.Cells[row, 5].Text,
                    Quantity = ws.Cells[row, 6].Text,
                    UnitPrice = ws.Cells[row, 7].Text,
                    Currency = ws.Cells[row, 8].Text,
                    ManufacturerName = ws.Cells[row, 9].Text,
                    ManufacturerPartNumber = ws.Cells[row, 10].Text,
                    LeadTimeDays = ws.Cells[row, 11].Text
                });
            }

            var canonicalImport = _canonicalNormalizer.NormalizeSpreadsheetRows(rows, businessUnitId);
            var blockingIssues = canonicalImport.Issues
                .Where(i => i.Severity == ValidationSeverity.Error)
                .Take(10)
                .Select(i => $"{i.Code}: {i.Message} ({i.Evidence?.Location ?? "unknown location"})")
                .ToList();

            if (blockingIssues.Any())
            {
                return ServiceResult<string>.CreateFailure(
                    "RFQ upload failed canonical validation: " + string.Join("; ", blockingIssues));
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                foreach (var document in canonicalImport.Documents)
                {
                    if (await _context.Rfqs.AnyAsync(r => r.Rfqno == document.RfqNo.Value && r.BusinessUnitId == businessUnitId))
                    {
                        return ServiceResult<string>.CreateFailure(
                            $"RFQ number {document.RfqNo.Value} already exists in this Business Unit.");
                    }
                }

                int rfqCount = 0;
                int itemViewCount = 0;

                foreach (var document in canonicalImport.Documents)
                {
                    var rfq = new Rfq
                    {
                        Rfqno = document.RfqNo.Value!,
                        BuyersName = document.BuyerName.Value,
                        RecDate = document.ReceivedDate.Value == default ? DateTime.UtcNow : document.ReceivedDate.Value,
                        BidClosingDate = document.BidClosingDate.Value == default ? null : document.BidClosingDate.Value,
                        RfqstatusId = await ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecycleStatusCatalog.ResolveIdAsync(
                            _context, businessUnitId, "Rfq", "DRAFT"),
                        HeaderRemarks = BuildCanonicalRemarks(document),
                        CreatedBy = createdBy,
                        CreatedDate = DateTime.UtcNow,
                        BusinessUnitId = businessUnitId,
                        NoOfLineItems = document.LineItems.Count
                    };

                    throw new InvalidOperationException(
                        "Retired direct RFQ persistence path reached. Use governed Lead intake and RFQ Promotion.");

                    foreach (var canonicalItem in document.LineItems)
                    {
                        var item = new Rfqitem
                        {
                            ProductShortName = canonicalItem.ProductName.Value,
                            Quantity = canonicalItem.Quantity.Value == default ? 1 : canonicalItem.Quantity.Value,
                            UnitPrice = canonicalItem.UnitPrice.Value == default ? null : canonicalItem.UnitPrice.Value,
                            Currency = canonicalItem.Currency.Value,
                            ManufacturerName = canonicalItem.ManufacturerName.Value,
                            ManufacturerPartNumber = canonicalItem.ManufacturerPartNumber.Value,
                            LeadTime = canonicalItem.LeadTimeDays.Value == default ? null : canonicalItem.LeadTimeDays.Value,
                            CreatedBy = createdBy,
                            CreatedDate = DateTime.UtcNow,
                            Aiconfidence = CalculateLineConfidence(canonicalItem),
                            BidClosingDateLine = rfq.BidClosingDate
                        };

                        item.Rfqid = rfq.Id;
                        _context.Rfqitems.Add(item);
                        itemViewCount++;
                    }
                    rfqCount++;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return ServiceResult<string>.CreateSuccess($"{rfqCount} RFQs and {itemViewCount} items imported successfully.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "RFQ Excel upload failed.");
                return ServiceResult<string>.CreateFailure($"Import failed: {ex.Message}");
            }
        }

        private static decimal CalculateLineConfidence(CanonicalRfqLineItem item)
        {
            var values = new[]
            {
                item.ProductName.Confidence,
                item.Quantity.Confidence,
                item.UnitPrice.Confidence,
                item.Currency.Confidence,
                item.ManufacturerName.Confidence,
                item.ManufacturerPartNumber.Confidence,
                item.LeadTimeDays.Confidence
            };

            return Math.Round(values.Average(), 2);
        }

        private static string BuildCanonicalRemarks(CanonicalRfqDocument document)
        {
            var issueSummary = document.Issues.Any()
                ? string.Join("; ", document.Issues.Take(5).Select(i => $"{i.Code}:{i.Severity}"))
                : "none";

            return $"CanonicalSchema={document.SchemaVersion}; ValidationStatus={document.ValidationStatus}; Issues={issueSummary}";
        }
    }
}
