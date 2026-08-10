using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialRouting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Services
{
    public class CustomerUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<CustomerUploaderService> _logger;

        public CustomerUploaderService(ErpRfqAutomationContext context, ILogger<CustomerUploaderService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<byte[]> GenerateTemplateAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("CustomerTemplate");

            string[] headers = {
                "Customer Name*", "Contact Email", 
                "Billing Address Line 1", "Billing Address Line 2", "Billing City", "Billing State", "Billing Country", "Billing Postal Code",
                "Shipping Address Line 1", "Shipping Address Line 2", "Shipping City", "Shipping State", "Shipping Country", "Shipping Postal Code"
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
            ws.Cells[2, 1].Value = "Tech Solutions Inc";
            ws.Cells[2, 2].Value = "info@techsolutions.com";
            ws.Cells[2, 3].Value = "123 Business Bay";
            ws.Cells[2, 5].Value = "New York";
            ws.Cells[2, 6].Value = "NY";
            ws.Cells[2, 7].Value = "USA";
            ws.Cells[2, 8].Value = "10001";

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

        public async Task<ServiceResult<string>> UploadTemplateAsync(Stream fileStream, long businessUnitId, string createdBy)
        {
            // FR-MDM-05 / E44 — see ProductUploaderService for the full note. The audit is captured
            // at ErpRfqAutomationContext.SaveChanges and cannot be evaded from here; this line only
            // marks the SOURCE so a bulk customer import is separable from a screen edit.
            using var auditSource = MasterDataAuditScope.PushSource(MasterDataChangeSources.Import);

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];

            int rowCount = ws.Dimension?.Rows ?? 0;
            if (rowCount < 2) return ServiceResult<string>.CreateFailure("The uploaded file is empty or missing data.");

            try
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                var counts = await strategy.ExecuteAsync(async () =>
                {
                    _context.ChangeTracker.Clear();
                    await using var transaction = await _context.Database.BeginTransactionAsync();
                    var customersToAdd = new List<Customer>();
                    var successCount = 0;
                    var skipCount = 0;
                    var existingCustomers = await _context.Customers
                        .Where(c => c.Buid == businessUnitId)
                        .Select(c => new { c.Name, c.ContactEmail })
                        .ToListAsync();

                    for (int row = 2; row <= rowCount; row++)
                    {
                        var name = ws.Cells[row, 1].Text?.Trim();
                        var email = ws.Cells[row, 2].Text?.Trim();
                        if (string.IsNullOrEmpty(email)) email = null;
                        if (string.IsNullOrEmpty(name)) continue;

                        var isDuplicateInDb = existingCustomers.Any(e =>
                            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            (email != null && e.ContactEmail != null &&
                             e.ContactEmail.Equals(email, StringComparison.OrdinalIgnoreCase)));
                        var isDuplicateInList = customersToAdd.Any(c =>
                            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            (email != null && c.ContactEmail != null &&
                             c.ContactEmail.Equals(email, StringComparison.OrdinalIgnoreCase)));
                        if (isDuplicateInDb || isDuplicateInList)
                        {
                            skipCount++;
                            continue;
                        }

                        customersToAdd.Add(new Customer
                        {
                            DocId = null,
                            Name = name,
                            ContactEmail = email,
                            ImageUrl = "default-customer.png",
                            BillingAddressLine1 = ws.Cells[row, 3].Text?.Trim(),
                            BillingAddressLine2 = ws.Cells[row, 4].Text?.Trim(),
                            BillingCity = ws.Cells[row, 5].Text?.Trim(),
                            BillingState = ws.Cells[row, 6].Text?.Trim(),
                            BillingCountry = ws.Cells[row, 7].Text?.Trim(),
                            BillingPostalCode = ws.Cells[row, 8].Text?.Trim(),
                            ShippingAddressLine1 = ws.Cells[row, 9].Text?.Trim(),
                            ShippingAddressLine2 = ws.Cells[row, 10].Text?.Trim(),
                            ShippingCity = ws.Cells[row, 11].Text?.Trim(),
                            ShippingState = ws.Cells[row, 12].Text?.Trim(),
                            ShippingCountry = ws.Cells[row, 13].Text?.Trim(),
                            ShippingPostalCode = ws.Cells[row, 14].Text?.Trim(),
                            Buid = businessUnitId,
                            IsActive = true,
                            CreatedBy = createdBy,
                            CreatedOn = DateTime.UtcNow,
                            ConcurrencyToken = Guid.NewGuid()
                        });
                        successCount++;
                    }

                    if (customersToAdd.Count > 0)
                    {
                        _context.Customers.AddRange(customersToAdd);
                        await _context.SaveChangesAsync();
                        foreach (var customer in customersToAdd)
                            customer.DocId = $"CU{customer.Id:D8}";
                        await _context.SaveChangesAsync();
                        foreach (var customer in customersToAdd)
                            await CustomerIdentityMaintenance.SynchronizeAsync(
                                _context, businessUnitId, customer.Id, "CustomerImport");
                        await _context.SaveChangesAsync();
                    }

                    await transaction.CommitAsync();
                    return (successCount, skipCount);
                });

                string msg = $"{counts.successCount} customers imported successfully.";
                if (counts.skipCount > 0) msg += $" {counts.skipCount} duplicates skipped.";
                return ServiceResult<string>.CreateSuccess(msg, msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Customer upload failed.");
                
                return ServiceResult<string>.CreateFailure(
                    "Customer import failed. No records were committed.");
            }
        }

        public async Task<byte[]> ExportCustomersAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var customers = await _context.Customers
                .Where(c => c.Buid == businessUnitId)
                .OrderBy(c => c.Name)
                .ToListAsync();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Customers");

            string[] headers = {
                "ID", "Customer Name", "Contact Email", "Address", "City", "Country", "Status", "Created On"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
            }

            int row = 2;
            foreach (var c in customers)
            {
                ws.Cells[row, 1].Value = c.DocId;
                ws.Cells[row, 2].Value = c.Name;
                ws.Cells[row, 3].Value = c.ContactEmail;
                ws.Cells[row, 4].Value = c.BillingAddressLine1;
                ws.Cells[row, 5].Value = c.BillingCity;
                ws.Cells[row, 6].Value = c.BillingCountry;
                ws.Cells[row, 7].Value = (c.IsActive ?? true) ? "Active" : "Inactive";
                ws.Cells[row, 8].Value = c.CreatedOn.ToString("yyyy-MM-dd HH:mm");
                row++;
            }

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }
    }
}
