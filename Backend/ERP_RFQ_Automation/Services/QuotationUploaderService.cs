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

        private readonly QuoteBackfillSpine? _spine;

        public QuotationUploaderService(
            ErpRfqAutomationContext context,
            ILogger<QuotationUploaderService> logger,
            QuoteBackfillSpine? spine = null)
        {
            _context = context;
            _logger = logger;
            _spine = spine;
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

            var sampleRfqNumber = await _context.Rfqs
                .Where(r => r.BusinessUnitId == businessUnitId && r.CommercialCaseId != null)
                .OrderByDescending(r => r.Id)
                .Select(r => r.Rfqno)
                .FirstOrDefaultAsync();

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

            // The two line-reference columns are appended (12, 13) so templates downloaded
            // before they existed still import: their cells simply read as empty.
            //
            // Column 14 is appended in the same position-stable way but is MANDATORY, and that is
            // deliberate: a quotation inherits its Nexora Serial from the RFQ it answers, so a
            // quote uploaded against no RFQ would carry no commercial case and could never be
            // traced from inquiry to delivery. An upload from a template downloaded before this
            // column existed is refused with a message that says to download the current one,
            // rather than silently importing quotations outside the spine.
            string[] headers = {
                "Quote No", "Customer Name*", "Quote Date (YYYY-MM-DD)*", "Valid Until (YYYY-MM-DD)",
                "Currency Code*", "Product Name*", "Quantity*", "Unit Price*",
                "Tax Amount", "Discount Amount", "Header Remarks",
                "Unit of Measure", "Customer Line Ref", "Customer RFQ No*"
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
            ws.Cells[2, 12].Value = "EA";
            ws.Cells[2, 13].Value = "00010";
            // The sample RFQ number is a real one from this tenant when it has any. Left blank
            // otherwise — an invented reference would be refused on upload anyway, and printing
            // one that does not exist would read as a working example.
            ws.Cells[2, 14].Value = sampleRfqNumber;

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

                /// <summary>
        /// Bulk import entry point. The whole import is run as one retriable unit so that the
        /// transaction it opens is owned by the configured execution strategy — see
        /// <see cref="ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction"/> for the
        /// defect this closes (every upload returned 500 against PostgreSQL).
        /// </summary>
        /// <param name="backfill">
        /// When true the sheet is a BACK-FILL of quotes issued before Nexora existed, and a row
        /// naming no RFQ originates its own commercial spine instead of being refused.
        ///
        /// This is an explicit mode, never a fallback for a blank cell. A missing 'Customer RFQ No'
        /// on an ordinary upload is overwhelmingly a mistake, and quietly inventing an inquiry to
        /// paper over it is exactly the corruption the guard below exists to prevent. The operator
        /// has to say which kind of file this is.
        /// </param>
        public Task<ServiceResult<string>> UploadTemplateAsync(
            Stream fileStream, long businessUnitId, string createdBy, bool backfill = false) =>
            ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction.ExecuteAsync(
                _context, fileStream, () => UploadTemplateCoreAsync(fileStream, businessUnitId, createdBy, backfill));

        private async Task<ServiceResult<string>> UploadTemplateCoreAsync(
            Stream fileStream, long businessUnitId, string createdBy, bool backfill)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];

            int rowCount = ws.Dimension?.Rows ?? 0;
            if (rowCount < 2) return ServiceResult<string>.CreateFailure("The uploaded file is empty or missing data.");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var groupedQuotes = new Dictionary<string, (Quote Quote, List<QuoteItem> Items, string CustomerRfqNo)>();

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

                    // FR-COM-07. The originating RFQ is mandatory and is resolved BEFORE anything
                    // is built, so a spreadsheet that cannot name its inquiry is refused outright
                    // rather than producing priced quotations outside the commercial case.
                    //
                    // There is deliberately no "allocate a case here" branch. A commercial case is
                    // the one-to-one principal of a Lead, so minting one for a spreadsheet row
                    // would manufacture a phantom inquiry and corrupt the spine to preserve a
                    // convenience.
                    var customerRfqNo = ws.Cells[row, 14].Text?.Trim();
                    if (!backfill && string.IsNullOrEmpty(customerRfqNo))
                        return ServiceResult<string>.CreateFailure(
                            $"Row {row}: 'Customer RFQ No' is required. A quotation takes its Nexora Serial " +
                            "from the RFQ it answers, and one uploaded against no RFQ could never be traced " +
                            "from inquiry to delivery. Download the current template, which has that column.");

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

                        // Re-read inside the caller's tenant: a spreadsheet must not be able to
                        // name another business unit's RFQ and borrow its commercial case.
                        Rfq rfq;
                        if (backfill && string.IsNullOrEmpty(customerRfqNo))
                        {
                            // A quote issued before Nexora existed answers an inquiry that really happened, it
                            // just happened elsewhere. The spine records that: a Lead marked BACKFILL carrying the
                            // quote's ORIGINAL issue date, which mints the commercial case exactly as a live
                            // inquiry would. That is why this is not the "allocate a case here" branch the guard
                            // above rightly refuses — no case is minted without the Lead that owns it.
                            if (_spine is null)
                                return ServiceResult<string>.CreateFailure(
                                    $"Row {row}: back-fill is unavailable because the commercial spine is not wired up.");
                            if (string.IsNullOrWhiteSpace(quoteNo))
                                return ServiceResult<string>.CreateFailure(
                                    $"Row {row}: a back-filled quote needs the number its CUSTOMER knows it by. " +
                                    "That number is how the quote is recognised and how a re-import is detected.");
                            var issuedOn = ParseDate(ws.Cells[row, 3].Text?.Trim()) ?? DateTime.UtcNow;
                            rfq = await _spine.OriginateAsync(
                                businessUnitId, customer.Id, null, issuedOn, createdBy, quoteNo);
                        }
                        else
                        {
                            var normalizedRfqNo = customerRfqNo.ToLower();
                            var rfqMatches = await _context.Rfqs
                                .Include(r => r.Lead)
                                .Where(r => r.BusinessUnitId == businessUnitId && r.Rfqno.ToLower().Trim() == normalizedRfqNo)
                                .OrderBy(r => r.Id)
                                .ToListAsync();
                            if (rfqMatches.Count == 0)
                                return ServiceResult<string>.CreateFailure(
                                    $"Row {row}: RFQ '{customerRfqNo}' was not found in this business unit.");
                            // Rfqno carries no uniqueness constraint, so an ambiguous reference is a
                            // question for a human, not a coin toss between two commercial cases.
                            if (rfqMatches.Count > 1)
                                return ServiceResult<string>.CreateFailure(
                                    $"Row {row}: RFQ '{customerRfqNo}' matches {rfqMatches.Count} RFQs " +
                                    $"(IDs {string.Join(", ", rfqMatches.Select(r => r.Id))}). Resolve the duplicate " +
                                    "before importing, so the quotation cannot be attached to the wrong case.");
                            rfq = rfqMatches[0];
                        }
                        if (!rfq.CommercialCaseId.HasValue)
                            return ServiceResult<string>.CreateFailure(
                                $"Row {row}: RFQ '{rfq.Rfqno}' carries no commercial case, so a quotation " +
                                "cannot inherit one from it.");
                        if (rfq.CustomerId.HasValue && rfq.CustomerId.Value != customer.Id)
                            return ServiceResult<string>.CreateFailure(
                                $"Row {row}: RFQ '{rfq.Rfqno}' belongs to a different customer than '{customerName}'. " +
                                "A quotation must answer its own customer's inquiry.");

                        var quoteDateStr = ws.Cells[row, 3].Text?.Trim();
                        var validUntilStr = ws.Cells[row, 4].Text?.Trim();
                        var quoteDate = ParseDate(quoteDateStr) ?? DateTime.UtcNow;
                        var validUntil = ParseDate(validUntilStr);

                        var quote = new Quote
                        {
                            QuoteNo = quoteNo,
                            ExternalQuoteReference = backfill ? quoteNo : null,
                            Origin = backfill ? QuoteOrigin.Backfill : QuoteOrigin.Pipeline,
                            Rfqid = rfq.Id,
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
                        quote.InheritCommercialIdentity(rfq);
                        groupedQuotes[quoteKey] = (quote, new List<QuoteItem>(), customerRfqNo);
                    }
                    else if (!string.Equals(groupedQuotes[quoteKey].CustomerRfqNo, customerRfqNo,
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        // One quotation answers one inquiry. Rows that group under the same quote
                        // number but name different RFQs would otherwise silently take the first
                        // row's commercial case.
                        return ServiceResult<string>.CreateFailure(
                            $"Row {row}: quote '{quoteNo}' is already being imported against RFQ " +
                            $"'{groupedQuotes[quoteKey].CustomerRfqNo}'. A quotation cannot answer two RFQs.");
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

                    // Optional line-reference columns (12, 13): blank on templates downloaded
                    // before the columns existed. Stored as null, never as an empty string.
                    var unitOfMeasure = ws.Cells[row, 12].Text?.Trim();
                    var customerLineRef = ws.Cells[row, 13].Text?.Trim();

                    var item = new QuoteItem
                    {
                        ProductId = product?.Id,
                        ItemDescription = productName,
                        Quantity = qty,
                        UnitOfMeasure = string.IsNullOrEmpty(unitOfMeasure) ? null : unitOfMeasure,
                        CustomerLineRef = string.IsNullOrEmpty(customerLineRef) ? null : customerLineRef,
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
