using OfficeOpenXml;
using OfficeOpenXml.DataValidation;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Drawing;

namespace ERP_RFQ_Automation.Services
{
    public class ProductUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<ProductUploaderService> _logger;
        private readonly IStockLedgerService _ledger;

        public ProductUploaderService(ErpRfqAutomationContext context, ILogger<ProductUploaderService> logger,
            IStockLedgerService? ledger = null)
        {
            _context = context;
            _logger = logger;
            // Defaulted so the existing call sites keep compiling, but never null: writing the
            // opening stock anywhere other than the ledger is exactly the defect being closed.
            _ledger = ledger ?? new StockLedgerService(context);
        }

        public async Task<byte[]> GenerateTemplateAsync(long businessUnitId)
        {
            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Products");
            var lookupSheet = package.Workbook.Worksheets.Add("Lookups");
            lookupSheet.Hidden = eWorkSheetHidden.VeryHidden;

            // Headers
            string[] headers = { 
                "Product Name*", "Part No*", "Model No", "Description", 
                "Category*", "SubCategory*", "UOM*", "Warehouse*", "Preferred Supplier",
                "Qty On Hand", "Reorder Point", "Unit Cost", "Selling Price",
                "Batch Tracking(Yes/No)", "Serial Tracking(Yes/No)", "Expiration Date(YYYY-MM-DD)",
                "Height", "Width", "Depth", "Weight", "Dimensions",
                "Barcode", "QRCode", "Lead Time", "HSCode", "Country of Origin",
                "Is Catalog Item(Yes/No)", "Final Landed Cost", "Final Sales Price"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
                worksheet.Cells[1, i + 1].Style.Font.Bold = true;
                worksheet.Cells[1, i + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                worksheet.Cells[1, i + 1].Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                worksheet.Column(i + 1).Width = 20;
            }

            // Add Dummy Data
            var dummyProducts = new List<object[]>
            {
                new object[] { "Sample Laptop", "CLP-001", "Mod-X", "High-end gaming laptop", "Electronics", "IT", "Pcs", "Main WH", "Dell", 10, 2, 800, 1200, "No", "No", "2026-12-31", 2, 35, 25, 2.5, "35x25x2", "123456", "QR123", 5, "847130", "China", "Yes", 850, 1300 },
                new object[] { "Office Chair", "FUR-001", "Erg-01", "Ergonomic office chair", "Furniture", "Office", "Pcs", "Showroom", "IKEA", 5, 1, 150, 250, "No", "No", "", 100, 60, 60, 15, "100x60x60", "223456", "QR223", 10, "940330", "Vietnam", "Yes", 160, 280 },
                new object[] { "USB-C Cable", "ACC-001", "CBL-3FT", "3ft USB-C to USB-C cable", "Accessories", "Cables", "Box", "Store Room", "Belkin", 50, 10, 5, 15, "No", "No", "", 1, 1, 1, 0.1, "100cm", "323456", "QR323", 2, "854442", "China", "Yes", 6, 20 },
                new object[] { "Monitor 27\"", "MON-001", "UHD-27", "27-inch 4K Monitor", "Electronics", "Monitors", "Unit", "Main WH", "LG", 15, 3, 200, 350, "No", "Yes", "2027-01-01", 45, 60, 20, 5, "60x45x20", "423456", "QR423", 7, "852852", "Korea", "Yes", 220, 380 },
                new object[] { "Toner Cart.", "PRN-001", "HP-85A", "Black Toner Cartridge 85A", "Stationery", "Printer", "Pcs", "Annex", "HP", 20, 5, 40, 80, "Yes", "No", "2025-06-30", 15, 15, 35, 1, "35x15x15", "523456", "QR523", 3, "844399", "USA", "Yes", 45, 90 }
            };

            for (int row = 0; row < dummyProducts.Count; row++)
            {
                for (int col = 0; col < dummyProducts[row].Length; col++)
                {
                    worksheet.Cells[row + 2, col + 1].Value = dummyProducts[row][col];
                }
            }

            // Fetch Lookups
            var categories = await _context.ProductCategories.Where(c => c.BusinessUnitId == businessUnitId && c.IsActive == true).Select(c => c.CategoryName).ToListAsync();
            var subCategories = await _context.ProductSubCategories.Where(s => s.BusinessUnitId == businessUnitId && s.IsActive == true).Select(s => s.SubCategoryName).ToListAsync();
            var uoms = await _context.SetUoms.Where(u => u.BusinessUnitId == businessUnitId && u.IsActive == true).Select(u => u.UomName).ToListAsync();
            var warehouses = await _context.Warehouses.Where(w => w.BusinessUnitId == businessUnitId && w.IsActive == true).Select(w => w.WarehouseName).ToListAsync();
            var suppliers = await _context.Suppliers.Where(s => (s.Buid == businessUnitId || s.Buid == null) && s.IsActive == true).Select(s => s.Name).ToListAsync();

            // Populate Lookup sheet for validation
            for (int i = 0; i < categories.Count; i++) lookupSheet.Cells[i + 1, 1].Value = categories[i];
            for (int i = 0; i < subCategories.Count; i++) lookupSheet.Cells[i + 1, 2].Value = subCategories[i];
            for (int i = 0; i < uoms.Count; i++) lookupSheet.Cells[i + 1, 3].Value = uoms[i];
            for (int i = 0; i < warehouses.Count; i++) lookupSheet.Cells[i + 1, 4].Value = warehouses[i];
            for (int i = 0; i < suppliers.Count; i++) lookupSheet.Cells[i + 1, 5].Value = suppliers[i];
            
            // Add Yes/No lookup
            lookupSheet.Cells[1, 6].Value = "Yes";
            lookupSheet.Cells[2, 6].Value = "No";

            // Add Data Validations (Dropdowns)
            // Category (Col 5)
            if (categories.Any())
            {
                var val = worksheet.DataValidations.AddListValidation("E2:E1000");
                val.Formula.ExcelFormula = $"Lookups!$A$1:$A${categories.Count}";
            }
            // SubCategory (Col 6)
            if (subCategories.Any())
            {
                var val = worksheet.DataValidations.AddListValidation("F2:F1000");
                val.Formula.ExcelFormula = $"Lookups!$B$1:$B${subCategories.Count}";
            }
            // UOM (Col 7)
            if (uoms.Any())
            {
                var val = worksheet.DataValidations.AddListValidation("G2:G1000");
                val.Formula.ExcelFormula = $"Lookups!$C$1:$C${uoms.Count}";
            }
            // Warehouse (Col 8)
            if (warehouses.Any())
            {
                var val = worksheet.DataValidations.AddListValidation("H2:H1000");
                val.Formula.ExcelFormula = $"Lookups!$D$1:$D${warehouses.Count}";
            }
            // Supplier (Col 9)
            if (suppliers.Any())
            {
                var val = worksheet.DataValidations.AddListValidation("I2:I1000");
                val.Formula.ExcelFormula = $"Lookups!$E$1:$E${suppliers.Count}";
            }

            // Yes/No Validations
            var yesNoValRange = "N2:O1000 AA2:AA1000"; // Batch Tracking, Serial Tracking, Is Catalog Item
            var yesNoVal = worksheet.DataValidations.AddListValidation(yesNoValRange);
            yesNoVal.Formula.ExcelFormula = "Lookups!$F$1:$F$2";

            worksheet.Cells.AutoFitColumns();
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
            // FR-MDM-05 / E44. The audit itself is NOT wired here — it is captured at
            // ErpRfqAutomationContext.SaveChanges, so this method could not evade it even if this
            // line were deleted. What this line does is re-label the ambient actor's SOURCE, so a
            // thousand-row bulk edit of FinalLandedCost is distinguishable in the trail from a
            // single edit on the product screen. Bulk import is where unaudited mass changes
            // happen, and "who changed every landed cost in the catalogue last Tuesday" is a
            // different review question from "who edited this one product".
            using var auditSource = MasterDataAuditScope.PushSource(MasterDataChangeSources.Import);

            using var package = new ExcelPackage(fileStream);
            var worksheet = package.Workbook.Worksheets[0];
            int rowCount = worksheet.Dimension?.Rows ?? 0;

            if (rowCount <= 1) return ServiceResult<string>.CreateFailure("Excel file is empty.");

            int successCount = 0;
            int errorCount = 0;
            var errors = new List<string>();

            // Opening stock declared on the sheet, captured per row and posted to the stock ledger
            // after the products exist. RecordCountAsync opens its own transaction (it has to: the
            // balance and its balancing movement must commit together), so it cannot run inside the
            // product-insert transaction below.
            var openingStock = new List<(int Row, Product Product, long WarehouseId, decimal Quantity)>();
            var importBatchId = Guid.NewGuid().ToString("N");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Auto generate starting DocId
                var maxDoc = await _context.Products
                    .Where(p => p.DocId != null && p.DocId.StartsWith("PR"))
                    .OrderByDescending(p => p.DocId)
                    .Select(p => p.DocId)
                    .FirstOrDefaultAsync();

                long nextNum = 1;
                if (maxDoc != null && maxDoc.Length > 2)
                {
                    if (long.TryParse(maxDoc.Substring(2), out var lastNum))
                    {
                        nextNum = lastNum + 1;
                    }
                }

                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var productName = worksheet.Cells[row, 1].Text;
                        var partNo = worksheet.Cells[row, 2].Text;
                        if (string.IsNullOrWhiteSpace(productName) || string.IsNullOrWhiteSpace(partNo)) continue;

                        var categoryName = worksheet.Cells[row, 5].Text;
                        var subCategoryName = worksheet.Cells[row, 6].Text;
                        var uomName = worksheet.Cells[row, 7].Text;
                        var warehouseName = worksheet.Cells[row, 8].Text;
                        var supplierName = worksheet.Cells[row, 9].Text;

                        // Auto-Setup Logic
                        long? categoryId = await GetOrCreateCategory(categoryName, businessUnitId, createdBy);
                        int? subCategoryId = await GetOrCreateSubCategory(subCategoryName, businessUnitId, createdBy);
                        int? uomId = await GetOrCreateUom(uomName, businessUnitId, createdBy);
                        long? warehouseId = await GetOrCreateWarehouse(warehouseName, businessUnitId, createdBy);
                        long? supplierId = await GetSupplierIdByName(supplierName, businessUnitId);

                        var countedQuantity = decimal.TryParse(worksheet.Cells[row, 10].Text, out var qty) ? qty : 0m;
                        if (countedQuantity < 0m)
                            throw new InvalidOperationException(
                                $"Qty On Hand ({countedQuantity}) cannot be negative.");
                        // The sheet's Warehouse column is the only place a physical location can
                        // come from; stock with no warehouse cannot be represented in the ledger,
                        // and silently dropping it is how an opening balance disappears.
                        if (countedQuantity > 0m && warehouseId is null)
                            throw new InvalidOperationException(
                                "Qty On Hand was supplied without a Warehouse. Stock must be located in a warehouse.");

                        var product = new Product
                        {
                            DocId = "PR" + (nextNum++).ToString("D8"),
                            ProductName = productName,
                            PartNo = partNo,
                            ModelNo = worksheet.Cells[row, 3].Text,
                            Description = worksheet.Cells[row, 4].Text,
                            CategoryId = categoryId,
                            SubCategoryId = subCategoryId,
                            UomId = uomId,
                            WarehouseId = warehouseId,
                            PreferredSupplierId = supplierId,
                            // LEDGER AUTHORITY: this used to be the import's stock write. Product
                            // .QtyOnHand is a legacy denormalised column that posts no inventory
                            // movement and is invisible to the availability engine, so a client's
                            // opening stock upload landed nowhere any warehouse or ATP screen could
                            // see it. The real quantity is posted to Inventory below.
                            QtyOnHand = 0m,
                            ReorderPoint = decimal.TryParse(worksheet.Cells[row, 11].Text, out var rp) ? rp : 0,
                            UnitCost = decimal.TryParse(worksheet.Cells[row, 12].Text, out var uc) ? uc : 0,
                            SellingPrice = decimal.TryParse(worksheet.Cells[row, 13].Text, out var sp) ? sp : 0,
                            BatchTracking = ParseBool(worksheet.Cells[row, 14].Text),
                            SerialTracking = ParseBool(worksheet.Cells[row, 15].Text),
                            ExpirationDate = ParseDateOnly(worksheet.Cells[row, 16].Text),
                            Height = ParseDecimal(worksheet.Cells[row, 17].Text),
                            Width = ParseDecimal(worksheet.Cells[row, 18].Text),
                            Depth = ParseDecimal(worksheet.Cells[row, 19].Text),
                            Weight = ParseDecimal(worksheet.Cells[row, 20].Text),
                            Dimensions = worksheet.Cells[row, 21].Text,
                            Barcode = worksheet.Cells[row, 22].Text,
                            Qrcode = worksheet.Cells[row, 23].Text,
                            LeadTime = int.TryParse(worksheet.Cells[row, 24].Text, out var lt) ? lt : null,
                            Hscode = worksheet.Cells[row, 25].Text,
                            CountryOfOrigin = worksheet.Cells[row, 26].Text,
                            IsCatalogItem = ParseBool(worksheet.Cells[row, 27].Text) ?? true,
                            FinalLandedCost = ParseDecimal(worksheet.Cells[row, 28].Text),
                            FinalSalesPrice = ParseDecimal(worksheet.Cells[row, 29].Text),
                            Buid = businessUnitId,
                            CreatedBy = createdBy,
                            CreatedOn = DateTime.UtcNow,
                            IsActive = true
                        };

                        _context.Products.Add(product);
                        if (countedQuantity > 0m)
                            openingStock.Add((row, product, warehouseId!.Value, countedQuantity));
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        errors.Add($"Row {row}: {ex.Message}");
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Product upload failed.");
                return ServiceResult<string>.CreateFailure($"Transaction failed: {ex.Message}");
            }

            var stockedRows = await PostOpeningStockAsync(businessUnitId, createdBy, importBatchId, openingStock, errors);
            errorCount += openingStock.Count - stockedRows;

            return ServiceResult<string>.CreateSuccess(
                $"{successCount} products imported successfully. {stockedRows} opening stock balances posted to inventory. {errorCount} errors.",
                "Import Complete");
        }

        /// <summary>
        /// Posts each row's declared quantity to the authoritative per-warehouse stock ledger.
        ///
        /// <para><c>RecordCountAsync</c> creates the <c>Inventory</c> row on first use and posts the
        /// balancing <c>InventoryMovement</c> in the same transaction, so an imported balance is
        /// immediately visible to available-to-promise and reconciles against the movement ledger.
        /// The idempotency key is derived from the import batch, so a retried batch replays rather
        /// than double-counting.</para>
        ///
        /// <para>This runs after the product transaction has committed because the ledger owns its
        /// own transaction. A product whose stock posting fails is therefore imported with zero
        /// stock and reported as an error row, rather than the whole import being lost.</para>
        /// </summary>
        private async Task<int> PostOpeningStockAsync(long businessUnitId, string createdBy, string importBatchId,
            IReadOnlyList<(int Row, Product Product, long WarehouseId, decimal Quantity)> openingStock,
            List<string> errors)
        {
            var posted = 0;
            foreach (var (row, product, warehouseId, quantity) in openingStock)
            {
                try
                {
                    await _ledger.RecordCountAsync(businessUnitId, product.Id, warehouseId, quantity,
                        $"product-import:{importBatchId}:{product.Id}:{warehouseId}", createdBy,
                        "Opening stock import");
                    posted++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Opening stock could not be posted for product {ProductId}.", product.Id);
                    errors.Add($"Row {row}: product imported but opening stock was not posted: {ex.Message}");
                }
            }
            return posted;
        }

        public async Task<byte[]> ExportProductsAsync(long businessUnitId)
        {
            var products = await _context.Products
                .Where(p => p.Buid == businessUnitId)
                .Include(p => p.Category)
                .Include(p => p.SubCategory)
                .Include(p => p.Uom)
                .Include(p => p.Warehouse)
                .OrderBy(p => p.ProductName)
                .ToListAsync();

            // Export the authoritative per-warehouse balance, not the legacy product column. The
            // export is the round-trip of the import; reading a column the import no longer writes
            // would report every imported product as having zero stock.
            var onHandByProduct = await _context.Set<Models.Inventory>().AsNoTracking()
                .Where(x => x.Buid == businessUnitId && x.ProductId != null)
                .GroupBy(x => x.ProductId!.Value)
                .Select(x => new { ProductId = x.Key, Quantity = x.Sum(y => y.QtyOnHand) })
                .ToDictionaryAsync(x => x.ProductId, x => x.Quantity);

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Products");

            string[] headers = { 
                "Product ID", "Product Name", "Part No", "Model No", "Description", 
                "Category", "SubCategory", "UOM", "Warehouse", "Qty On Hand", 
                "Unit Cost", "Selling Price", "Status", "Created On" 
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightGray);
            }

            int row = 2;
            foreach (var p in products)
            {
                ws.Cells[row, 1].Value = p.DocId;
                ws.Cells[row, 2].Value = p.ProductName;
                ws.Cells[row, 3].Value = p.PartNo;
                ws.Cells[row, 4].Value = p.ModelNo;
                ws.Cells[row, 5].Value = p.Description;
                ws.Cells[row, 6].Value = p.Category?.CategoryName;
                ws.Cells[row, 7].Value = p.SubCategory?.SubCategoryName;
                ws.Cells[row, 8].Value = p.Uom?.UomName;
                ws.Cells[row, 9].Value = p.Warehouse?.WarehouseName;
                ws.Cells[row, 10].Value = onHandByProduct.GetValueOrDefault(p.Id);
                ws.Cells[row, 11].Value = p.UnitCost;
                ws.Cells[row, 12].Value = p.SellingPrice;
                ws.Cells[row, 13].Value = (p.IsActive ?? true) ? "Active" : "Inactive";
                ws.Cells[row, 14].Value = p.CreatedOn.ToString("yyyy-MM-dd HH:mm");
                row++;
            }

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

        private async Task<long?> GetOrCreateCategory(string name, long buId, string createdBy)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var cat = await _context.ProductCategories.FirstOrDefaultAsync(c => c.CategoryName == name && c.BusinessUnitId == buId);
            if (cat != null) return cat.Id;

            cat = new ProductCategory
            {
                CategoryName = name,
                BusinessUnitId = buId,
                CreatedBy = createdBy,
                CreatedOn = DateTime.UtcNow,
                IsActive = true
            };
            _context.ProductCategories.Add(cat);
            await _context.SaveChangesAsync();
            return cat.Id;
        }

        private async Task<int?> GetOrCreateSubCategory(string name, long buId, string createdBy)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var sub = await _context.ProductSubCategories.FirstOrDefaultAsync(s => s.SubCategoryName == name && s.BusinessUnitId == buId);
            if (sub != null) return sub.Id;

            sub = new ProductSubCategory
            {
                SubCategoryName = name,
                BusinessUnitId = buId,
                CreatedBy = createdBy,
                CreatedOn = DateTime.UtcNow,
                IsActive = true
            };
            _context.ProductSubCategories.Add(sub);
            await _context.SaveChangesAsync();
            return sub.Id;
        }

        /// <summary>
        /// Resolves a spreadsheet UoM cell to a SetUom row, creating one only when the tenant
        /// genuinely has no such unit.
        ///
        /// The match is TRIMMED and CASE-INSENSITIVE, and it considers the UoM CODE as well as
        /// the name. It used to be `u.UomName == name` — ordinal, untrimmed, name-only — so
        /// "Pcs", "pcs" and "PCS " each minted a separate row, in a table that has no unique
        /// index on (BusinessUnitId, UomCode) to stop them. That table is the lookup the whole
        /// conversion path normalises against, so every spurious row made canonicalisation
        /// weaker exactly where it was supposed to be strongest.
        /// </summary>
        private async Task<int?> GetOrCreateUom(string name, long buId, string createdBy)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            var uom = await _context.SetUoms.FirstOrDefaultAsync(u =>
                u.BusinessUnitId == buId
                && (u.UomName.ToLower() == trimmed.ToLower() || u.UomCode.ToLower() == trimmed.ToLower()));
            if (uom != null) return uom.UomId;

            uom = new SetUom
            {
                UomName = trimmed,
                UomCode = trimmed.Length > 10 ? trimmed.Substring(0, 10) : trimmed,
                BusinessUnitId = buId,
                CreatedBy = createdBy,
                CreatedDate = DateTime.UtcNow,
                IsActive = true
            };
            _context.SetUoms.Add(uom);
            await _context.SaveChangesAsync();
            return uom.UomId;
        }

        private async Task<long?> GetOrCreateWarehouse(string name, long buId, string createdBy)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var wh = await _context.Warehouses.FirstOrDefaultAsync(w => w.WarehouseName == name && w.BusinessUnitId == buId);
            if (wh != null) return wh.Id;

            wh = new Warehouse
            {
                WarehouseName = name,
                WarehouseCode = name.Length > 10 ? name.Substring(0, 10) : name,
                BusinessUnitId = buId,
                CreatedBy = createdBy,
                CreatedOn = DateTime.UtcNow,
                IsActive = true
            };
            _context.Warehouses.Add(wh);
            await _context.SaveChangesAsync();
            return wh.Id;
        }

        private async Task<long?> GetSupplierIdByName(string name, long buId)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.Name == name && (s.Buid == buId || s.Buid == null));
            return supplier?.Id;
        }

        private bool? ParseBool(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            return val.Equals("Yes", StringComparison.OrdinalIgnoreCase) || val.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        private decimal? ParseDecimal(string val)
        {
            if (decimal.TryParse(val, out var d)) return d;
            return null;
        }

        private DateOnly? ParseDateOnly(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return null;
            if (DateTime.TryParse(val, out var dt)) return DateOnly.FromDateTime(dt);
            return null;
        }
    }
}
