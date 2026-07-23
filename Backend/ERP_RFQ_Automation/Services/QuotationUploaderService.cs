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
    public class QuotationUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<QuotationUploaderService> _logger;

        public QuotationUploaderService(ErpRfqAutomationContext context, ILogger<QuotationUploaderService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<byte[]> GenerateTemplateAsync(long businessUnitId)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();

            // 1. Fetch lookup data
            var customers = await _context.Customers
                .Where(c => (c.Buid == businessUnitId || c.Buid == null) && (c.IsActive ?? true))
                .OrderBy(c => c.Name)
                .Select(c => c.Name)
                .ToListAsync();

            var currencies = await _context.Currencies
                .Where(c => c.BusinessUnitId == businessUnitId && (c.IsActive ?? true))
                .OrderBy(c => c.Code)
                .Select(c => c.Code)
                .ToListAsync();

            var products = await _context.Products
                .Where(p => p.Buid == businessUnitId && (p.IsActive ?? true))
                .OrderBy(p => p.ProductName)
                .Select(p => p.ProductName)
                .ToListAsync();

            // 2. Add Data Sheet (Hidden)
            var dataWs = package.Workbook.Worksheets.Add("Data");
            dataWs.Hidden = eWorkSheetHidden.VeryHidden;

            // Populate Customer Names in Data Sheet Column A
            for (int i = 0; i < customers.Count; i++) dataWs.Cells[i + 1, 1].Value = customers[i];
            // Populate Currencies in Data Sheet Column B
            for (int i = 0; i < currencies.Count; i++) dataWs.Cells[i + 1, 2].Value = currencies[i];
            // Populate Products in Data Sheet Column C
            for (int i = 0; i < products.Count; i++) dataWs.Cells[i + 1, 3].Value = products[i];

            // 3. Main Template Sheet
            var ws = package.Workbook.Worksheets.Add("QuotationTemplate");

            string[] headers = {
                "Quote No", "Customer Name*", "Quote Date (YYYY-MM-DD)*", "Valid Until (YYYY-MM-DD)",
                "Currency Code*", "Product Name*", "Quantity*", "Unit Price*",
                "Tax Amount", "Discount Amount", "Header Remarks"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
            }

            // 4. Add Data Validations (Dropdowns)

            // Customer Dropdown (Column B: 2)
            if (customers.Any())
            {
                var customerListRange = $"Data!$A$1:$A${customers.Count}";
                var val = ws.DataValidations.AddListValidation("B2:B1000");
                val.Formula.ExcelFormula = customerListRange;
                val.ShowErrorMessage = true;
                val.ErrorTitle = "Invalid Customer";
                val.Error = "Please select a customer from the dropdown list.";
            }

            // Currency Dropdown (Column E: 5)
            if (currencies.Any())
            {
                var currencyListRange = $"Data!$B$1:$B${currencies.Count}";
                var val = ws.DataValidations.AddListValidation("E2:E1000");
                val.Formula.ExcelFormula = currencyListRange;
                val.ShowErrorMessage = true;
                val.ErrorTitle = "Invalid Currency";
                val.Error = "Please select a currency from the dropdown list.";
            }

            // Product Dropdown (Column F: 6)
            if (products.Any())
            {
                var productListRange = $"Data!$C$1:$C${products.Count}";
                var val = ws.DataValidations.AddListValidation("F2:F1000");
                val.Formula.ExcelFormula = productListRange;
                val.ShowErrorMessage = true;
                val.ErrorTitle = "Invalid Product";
                val.Error = "Please select a product from the dropdown list.";
            }

            // Sample Data
            ws.Cells[2, 1].Value = "QT-2024-001";
            ws.Cells[2, 2].Value = customers.FirstOrDefault() ?? "Acme Corp";
            ws.Cells[2, 3].Value = DateTime.Now.ToString("yyyy-MM-dd");
            ws.Cells[2, 4].Value = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");
            ws.Cells[2, 5].Value = currencies.FirstOrDefault() ?? "USD";
            ws.Cells[2, 6].Value = products.FirstOrDefault() ?? "Solar Panel 400W";
            ws.Cells[2, 7].Value = 10;
            ws.Cells[2, 8].Value = 150.00;
            ws.Cells[2, 9].Value = 15.00;
            ws.Cells[2, 10].Value = 5.00;
            ws.Cells[2, 11].Value = "Initial quotation for Q2 project.";

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
                var groupedQuotes = new Dictionary<string, (Quote Quote, List<QuoteItem> Items)>();

                for (int row = 2; row <= rowCount; row++)
                {
                    var rawQuoteNo = ws.Cells[row, 1].Text?.Trim();
                    var customerName = ws.Cells[row, 2].Text?.Trim();
                    var productName = ws.Cells[row, 6].Text?.Trim();

                    if (string.IsNullOrEmpty(customerName) || string.IsNullOrEmpty(productName))
                        continue;

                    // Grouping logic: If QuoteNo is empty, use row as unique ID for this row only.
                    // If QuoteNo is provided, use it to group multiple items.
                    string quoteNo = rawQuoteNo;
                    bool isAutoGenerated = string.IsNullOrEmpty(quoteNo) || (!quoteNo.StartsWith("QT-", StringComparison.OrdinalIgnoreCase));

                    string quoteKey = string.IsNullOrEmpty(quoteNo)
                        ? $"AUTO_{row}_{customerName}".ToLowerInvariant()
                        : $"{quoteNo}_{customerName}".ToLowerInvariant();

                    if (!groupedQuotes.ContainsKey(quoteKey))
                    {
                        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Name.ToLower().Trim() == customerName.ToLower().Trim() && c.Buid == businessUnitId);
                        if (customer == null)
                        {
                            var globalCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.Name.ToLower().Trim() == customerName.ToLower().Trim());
                            if (globalCustomer != null)
                            {
                                return ServiceResult<string>.CreateFailure($"Customer '{customerName}' found but belongs to a different Business Unit (ID: {globalCustomer.Buid}). Your current session BU is {businessUnitId}.");
                            }
                            return ServiceResult<string>.CreateFailure($"Customer '{customerName}' not found in the system.");
                        }

                        var currencyCode = ws.Cells[row, 5].Text?.Trim();
                        var currencyCodeLower = currencyCode?.ToLower()?.Trim() ?? "";
                        var currency = await _context.Currencies.FirstOrDefaultAsync(c => (c.Code.ToLower().Trim() == currencyCodeLower || c.CurrencyName.ToLower().Trim() == currencyCodeLower) && c.BusinessUnitId == businessUnitId);
                        if (currency == null)
                        {
                            var globalCurrency = await _context.Currencies.FirstOrDefaultAsync(c => c.Code.ToLower().Trim() == currencyCodeLower || c.CurrencyName.ToLower().Trim() == currencyCodeLower);
                            if (globalCurrency != null)
                            {
                                return ServiceResult<string>.CreateFailure($"Currency '{currencyCode}' found but belongs to a different Business Unit (ID: {globalCurrency.BusinessUnitId}). Your current session BU is {businessUnitId}.");
                            }
                            return ServiceResult<string>.CreateFailure($"Currency '{currencyCode}' not found.");
                        }

                        var quoteDateStr = ws.Cells[row, 3].Text?.Trim();
                        var validUntilStr = ws.Cells[row, 4].Text?.Trim();
                        var quoteDate = ParseDate(quoteDateStr) ?? DateTime.UtcNow;
                        var validUntil = ParseDate(validUntilStr);

                        var quote = new Quote
                        {
                            QuoteNo = quoteNo,
                            CustomerId = customer.Id,
                            BusinessUnitId = businessUnitId,
                            QuoteDate = quoteDate,
                            ValidUntil = validUntil,
                            StatusId = 42, // Draft (matches QuoteService logic)
                            CurrencyId = currency.Id,
                            HeaderRemarks = ws.Cells[row, 11].Text?.Trim(),
                            FinancialCalculationVersion = 2,
                            CreatedBy = createdBy,
                            CreatedDate = DateTime.UtcNow,
                            TotalAmount = 0 // Will be calculated
                        };
                        groupedQuotes[quoteKey] = (quote, new List<QuoteItem>());
                    }

                    var qty = decimal.TryParse(ws.Cells[row, 7].Text, out var q) ? q : 0;
                    var price = decimal.TryParse(ws.Cells[row, 8].Text, out var p) ? p : 0;
                    var tax = decimal.TryParse(ws.Cells[row, 9].Text, out var t) ? t : 0;
                    var disc = decimal.TryParse(ws.Cells[row, 10].Text, out var d) ? d : 0;
                    var lineTotal = (qty * price) + tax - disc;

                    var product = await _context.Products.FirstOrDefaultAsync(p => p.ProductName != null && p.ProductName.ToLower().Trim() == productName.ToLower().Trim() && p.Buid == businessUnitId);
                    // Non-blocking product lookup: matching the flexible "RFQ method" logic
                    if (product == null)
                    {
                        var globalProduct = await _context.Products.FirstOrDefaultAsync(p => p.ProductName != null && p.ProductName.ToLower().Trim() == productName.ToLower().Trim());
                        if (globalProduct != null)
                        {
                            _logger.LogInformation($"Product '{productName}' used in Quote but found in a different BU (ID: {globalProduct.Buid}). Current session BU is {businessUnitId}. Product ID will be null in database.");
                        }
                    }

                    var item = new QuoteItem
                    {
                        ProductId = product?.Id,
                        ItemDescription = productName,
                        Quantity = qty,
                        UnitPrice = price,
                        TaxAmount = tax,
                        Discount = disc,
                        TotalAmount = lineTotal,
                        CreatedBy = createdBy,
                        CreatedDate = DateTime.UtcNow
                    };

                    groupedQuotes[quoteKey].Quote.TotalAmount += lineTotal;
                    groupedQuotes[quoteKey].Items.Add(item);
                }

                int quoteCount = 0;
                int itemCount = 0;

                foreach (var entry in groupedQuotes.Values)
                {
                    var quote = entry.Quote;

                    // Auto-generate number if placeholder or empty
                    if (string.IsNullOrEmpty(quote.QuoteNo) || !quote.QuoteNo.StartsWith("QT-", StringComparison.OrdinalIgnoreCase))
                    {
                        quote.QuoteNo = await GenerateNextQuoteNumberAsync(businessUnitId);
                    }

                    _context.Quotes.Add(quote);
                    await _context.SaveChangesAsync();

                    foreach (var item in entry.Items)
                    {
                        item.QuoteId = quote.Id;
                        _context.QuoteItems.Add(item);
                        itemCount++;
                    }
                    quoteCount++;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return ServiceResult<string>.CreateSuccess($"{quoteCount} Quotations and {itemCount} items imported successfully.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Quotation Excel upload failed.");
                return ServiceResult<string>.CreateFailure($"Import failed: {ex.Message}");
            }
        }

        private async Task<string> GenerateNextQuoteNumberAsync(long businessUnitId)
        {
            var now = DateTime.UtcNow;
            var prefix = $"QT-{now:MM}{now:yy}-";

            var lastQuote = await _context.Quotes
                .Where(q => q.BusinessUnitId == businessUnitId && q.QuoteNo.StartsWith(prefix))
                .OrderByDescending(q => q.QuoteNo)
                .FirstOrDefaultAsync();

            int nextSequence = 1;
            if (lastQuote != null)
            {
                var parts = lastQuote.QuoteNo.Split('-');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int lastSequence))
                {
                    nextSequence = lastSequence + 1;
                }
            }

            return $"{prefix}{nextSequence:D4}";
        }

        private DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy", "d/M/yyyy", "yyyy/MM/dd" };
            if (DateTime.TryParseExact(s.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                return d;

            // Try standard parse as fallback
            if (DateTime.TryParse(s, out var d2))
                return d2;

            return null;
        }
    }
}
