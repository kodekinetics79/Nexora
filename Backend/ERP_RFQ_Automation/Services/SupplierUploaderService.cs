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
    public class SupplierUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<SupplierUploaderService> _logger;

        public SupplierUploaderService(ErpRfqAutomationContext context, ILogger<SupplierUploaderService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<byte[]> GenerateTemplateAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("SupplierTemplate");

            // APPEND-ONLY. Existing columns keep their positions because the reader below indexes
            // by column number and tenants keep filled-in copies of this template; a new field goes
            // on the end. Column 11 = Tax Registration Number (added with the input-VAT evidence
            // work — a supplier with none cannot have its input tax treated as recoverable).
            string[] headers = {
                "Supplier Name*", "Contact Email", "Payment Terms",
                "Address Line 1", "Address Line 2", "Postal Code",
                "Success Rate (%)", "Avg Response Time (Days)", "Tags", "Comments",
                // The KSA format is stated in the header rather than demonstrated in the sample
                // row: a plausible-looking VAT number in a shipped template is the kind of value
                // that ends up copied into a real supplier record.
                "Tax Registration Number (KSA VAT: 15 digits, first and last digit 3)"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightSalmon);
            }

            // Sample Data
            ws.Cells[2, 1].Value = "Global Tech Supplies";
            ws.Cells[2, 2].Value = "sales@globaltech.com";
            ws.Cells[2, 3].Value = "Net 30";
            ws.Cells[2, 4].Value = "123 Tech Park";
            ws.Cells[2, 7].Value = 95;
            ws.Cells[2, 8].Value = 2;
            ws.Cells[2, 9].Value = "electronics, wholesale";

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

                /// <summary>
        /// Bulk import entry point. The whole import is run as one retriable unit so that the
        /// transaction it opens is owned by the configured execution strategy — see
        /// <see cref="ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction"/> for the
        /// defect this closes (every upload returned 500 against PostgreSQL).
        /// </summary>
        public Task<ERP_RFQ_Automation.Models.ServiceResult<string>> UploadTemplateAsync(Stream fileStream, long businessUnitId, string createdBy) =>
            ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction.ExecuteAsync(
                _context, fileStream, () => UploadTemplateCoreAsync(fileStream, businessUnitId, createdBy));

        private async Task<ERP_RFQ_Automation.Models.ServiceResult<string>> UploadTemplateCoreAsync(Stream fileStream, long businessUnitId, string createdBy)
        {
            // FR-MDM-05 / E44 — see ProductUploaderService for the full note. The audit is captured
            // at ErpRfqAutomationContext.SaveChanges and cannot be evaded from here; this line only
            // marks the SOURCE so a bulk supplier import is separable from a screen edit.
            using var auditSource = ERP_RFQ_Automation.MasterData.MasterDataAuditScope.PushSource(
                ERP_RFQ_Automation.MasterData.MasterDataChangeSources.Import);

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];

            int rowCount = ws.Dimension?.Rows ?? 0;
            if (rowCount < 2) return ERP_RFQ_Automation.Models.ServiceResult<string>.CreateFailure("The uploaded file is empty or missing data.");

            var suppliersToAdd = new List<Supplier>();
            var rejectedRows = new List<string>();
            int successCount = 0;
            int skipCount = 0;

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var existingSuppliers = await _context.Suppliers
                    .Where(s => s.Buid == businessUnitId)
                    .Select(s => new { s.Name, s.ContactEmail })
                    .ToListAsync();

                // DocId Generation setup
                var maxDoc = await _context.Suppliers
                    .Where(s => s.DocId != null && s.DocId.StartsWith("SU"))
                    .Select(s => s.DocId)
                    .MaxAsync();

                long nextNum = 1;
                if (maxDoc != null)
                {
                    nextNum = long.Parse(maxDoc.Substring(2)) + 1;
                }

                for (int row = 2; row <= rowCount; row++)
                {
                    var name = ws.Cells[row, 1].Text?.Trim();
                    var email = ws.Cells[row, 2].Text?.Trim();

                    if (string.IsNullOrEmpty(name)) continue;

                    // Check for duplicates in DB
                    if (existingSuppliers.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && 
                                                 (string.IsNullOrEmpty(email) || e.ContactEmail == email)))
                    {
                        skipCount++;
                        continue;
                    }

                    // Check for duplicates in current list
                    if (suppliersToAdd.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                              (string.IsNullOrEmpty(email) || s.ContactEmail == email)))
                    {
                        skipCount++;
                        continue;
                    }

                    // Column 11, appended. A malformed registration number is REJECTED rather than
                    // dropped: importing the supplier without it would silently create a record
                    // whose input tax can never be treated as recoverable, and the importer would
                    // have reported success.
                    if (!ERP_RFQ_Automation.Tax.TaxRegistrationNumbers.TryCanonicalize(
                            ws.Cells[row, 11].Text, "Tax Registration Number",
                            out var taxRegistrationNumber, out var taxRegistrationError))
                    {
                        rejectedRows.Add($"Row {row} ({name}): {taxRegistrationError}");
                        continue;
                    }

                    var supplier = new Supplier
                    {
                        DocId = "SU" + (nextNum++).ToString("D8"),
                        Name = name,
                        ContactEmail = email,
                        PaymentTerms = ws.Cells[row, 3].Text?.Trim(),
                        AddressLine1 = ws.Cells[row, 4].Text?.Trim(),
                        AddressLine2 = ws.Cells[row, 5].Text?.Trim(),
                        PostalCode = ws.Cells[row, 6].Text?.Trim(),
                        SuccessRate = decimal.TryParse(ws.Cells[row, 7].Text, out var sr) ? sr : null,
                        AvgResponseTime = int.TryParse(ws.Cells[row, 8].Text, out var art) ? art : null,
                        Tags = ws.Cells[row, 9].Text?.Trim(),
                        Comments = ws.Cells[row, 10].Text?.Trim(),
                        TaxRegistrationNumber = taxRegistrationNumber,
                        ImageUrl = string.Empty, // Required in model
                        Buid = businessUnitId,
                        IsActive = true,
                        CreatedBy = createdBy,
                        CreatedOn = DateTime.UtcNow
                    };

                    suppliersToAdd.Add(supplier);
                    successCount++;
                }

                if (suppliersToAdd.Any())
                {
                    _context.Suppliers.AddRange(suppliersToAdd);
                    await _context.SaveChangesAsync();
                }

                await transaction.CommitAsync();
                
                string msg = $"{successCount} suppliers imported successfully.";
                if (skipCount > 0) msg += $" {skipCount} duplicates skipped.";
                if (rejectedRows.Count > 0)
                    msg += $" {rejectedRows.Count} row(s) rejected: " + string.Join(" ", rejectedRows.Take(10));

                return ERP_RFQ_Automation.Models.ServiceResult<string>.CreateSuccess(msg, msg);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Supplier upload failed.");
                return ERP_RFQ_Automation.Models.ServiceResult<string>.CreateFailure($"Transaction failed: {ex.Message}");
            }
        }

        public async Task<byte[]> ExportSuppliersAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var suppliers = await _context.Suppliers
                .Where(s => s.Buid == businessUnitId)
                .OrderBy(s => s.Name)
                .ToListAsync();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Suppliers");

            // Append-only, same as the import template: column 9 = Tax Registration Number.
            string[] headers = {
                "ID", "Supplier Name", "Contact Email", "Payment Terms", "Address", "Success Rate (%)", "Status", "Created On",
                "Tax Registration Number"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightSalmon);
            }

            int row = 2;
            foreach (var s in suppliers)
            {
                ws.Cells[row, 1].Value = s.DocId;
                ws.Cells[row, 2].Value = s.Name;
                ws.Cells[row, 3].Value = s.ContactEmail;
                ws.Cells[row, 4].Value = s.PaymentTerms;
                ws.Cells[row, 5].Value = s.AddressLine1;
                ws.Cells[row, 6].Value = s.SuccessRate;
                ws.Cells[row, 7].Value = (s.IsActive ?? true) ? "Active" : "Inactive";
                ws.Cells[row, 8].Value = s.CreatedOn.ToString("yyyy-MM-dd HH:mm");
                ws.Cells[row, 9].Value = s.TaxRegistrationNumber;
                row++;
            }

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }
    }
}
