using OfficeOpenXml;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Drawing;

namespace ERP_RFQ_Automation.Services
{
    public class ProductCategoryUploaderService
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<ProductCategoryUploaderService> _logger;

        public ProductCategoryUploaderService(ErpRfqAutomationContext context, ILogger<ProductCategoryUploaderService> logger)
        {
            _context = context;
            _logger = logger;
        }

        // ─────────────────────────────────────────────────────────────
        //  PRODUCT CATEGORY
        // ─────────────────────────────────────────────────────────────

        public async Task<byte[]> GenerateCategoryTemplateAsync(long businessUnitId)
        {
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("ProductCategories");

            // Headers
            string[] headers = { "Category Name*", "Description", "Parent Category Name" };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightSteelBlue);
                ws.Column(i + 1).Width = 30;
            }

            // Sample rows
            var samples = new[]
            {
                new object[] { "Electronics", "Electronic products and gadgets", "" },
                new object[] { "Furniture", "Office and home furniture", ""},
                new object[] { "Laptops", "Portable computers", "Electronics" },
            };

            for (int r = 0; r < samples.Length; r++)
            {
                for (int c = 0; c < samples[r].Length; c++)
                    ws.Cells[r + 2, c + 1].Value = samples[r][c];
            }

            // Instructions sheet
            var info = package.Workbook.Worksheets.Add("Instructions");
            info.Cells[1, 1].Value = "Instructions";
            info.Cells[1, 1].Style.Font.Bold = true;
            info.Cells[2, 1].Value = "• Fields marked with * are required.";
            info.Cells[3, 1].Value = "• 'Parent Category Name' is optional. If provided, it must match an existing category or another row in this file.";
            info.Cells[4, 1].Value = "• Duplicate category names will be skipped.";
            info.Column(1).Width = 80;

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

                /// <summary>
        /// Bulk import entry point. The whole import is run as one retriable unit so that the
        /// transaction it opens is owned by the configured execution strategy — see
        /// <see cref="ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction"/> for the
        /// defect this closes (every upload returned 500 against PostgreSQL).
        /// </summary>
        public Task<ServiceResult<string>> UploadCategoryTemplateAsync(Stream fileStream, long businessUnitId, string createdBy) =>
            ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction.ExecuteAsync(
                _context, fileStream, () => UploadCategoryTemplateCoreAsync(fileStream, businessUnitId, createdBy));

        private async Task<ServiceResult<string>> UploadCategoryTemplateCoreAsync(Stream fileStream, long businessUnitId, string createdBy)
        {
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];
            int rowCount = ws.Dimension?.Rows ?? 0;

            if (rowCount <= 1)
                return ServiceResult<string>.CreateFailure("Excel file is empty or has only headers.");

            int successCount = 0, skipCount = 0, errorCount = 0;
            var errors = new List<string>();

            // First pass: collect all new category names from the file so parent references within file work
            var fileCategories = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                for (int row = 2; row <= rowCount; row++)
                {
                    var categoryName = ws.Cells[row, 1].Text?.Trim();
                    if (string.IsNullOrWhiteSpace(categoryName)) continue;

                    var description = ws.Cells[row, 2].Text?.Trim();
                    var parentName = ws.Cells[row, 3].Text?.Trim();

                    try
                    {
                        // Check for duplicate
                        var existing = await _context.ProductCategories
                            .FirstOrDefaultAsync(c => c.CategoryName == categoryName && c.BusinessUnitId == businessUnitId);

                        if (existing != null)
                        {
                            skipCount++;
                            fileCategories[categoryName] = existing.Id;
                            continue;
                        }

                        // Resolve parent
                        long? parentId = null;
                        if (!string.IsNullOrWhiteSpace(parentName))
                        {
                            // Try in-file first, then DB
                            if (fileCategories.TryGetValue(parentName, out var pid))
                            {
                                parentId = pid;
                            }
                            else
                            {
                                var parent = await _context.ProductCategories
                                    .FirstOrDefaultAsync(c => c.CategoryName == parentName && c.BusinessUnitId == businessUnitId);
                                parentId = parent?.Id;
                            }
                        }

                        var category = new ProductCategory
                        {
                            CategoryName = categoryName,
                            Description = string.IsNullOrWhiteSpace(description) ? null : description,
                            ParentCategoryId = parentId,
                            BusinessUnitId = businessUnitId,
                            IsActive = true,
                            CreatedBy = createdBy,
                            CreatedOn = DateTime.UtcNow
                        };

                        _context.ProductCategories.Add(category);
                        await _context.SaveChangesAsync();

                        fileCategories[categoryName] = category.Id;
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        errors.Add($"Row {row} ({categoryName}): {ex.Message}");
                    }
                }

                await transaction.CommitAsync();

                var msg = $"{successCount} categories imported successfully. {skipCount} skipped (already exist). {errorCount} errors.";
                if (errors.Any()) msg += " Errors: " + string.Join("; ", errors);

                return ServiceResult<string>.CreateSuccess(msg, msg);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Category upload failed.");
                return ServiceResult<string>.CreateFailure($"Transaction failed: {ex.Message}");
            }
        }

        public async Task<byte[]> ExportCategoriesAsync(long businessUnitId)
        {
            var categories = await _context.ProductCategories
                .Where(c => c.BusinessUnitId == businessUnitId)
                .Include(c => c.ParentCategory)
                .OrderBy(c => c.CategoryName)
                .ToListAsync();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("ProductCategories");

            string[] headers = { "ID", "Category Name", "Description", "Parent Category", "Status", "Created On" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightSteelBlue);
            }

            int row = 2;
            foreach (var cat in categories)
            {
                ws.Cells[row, 1].Value = cat.Id;
                ws.Cells[row, 2].Value = cat.CategoryName;
                ws.Cells[row, 3].Value = cat.Description;
                ws.Cells[row, 4].Value = cat.ParentCategory?.CategoryName;
                ws.Cells[row, 5].Value = (cat.IsActive ?? true) ? "Active" : "Inactive";
                ws.Cells[row, 6].Value = cat.CreatedOn.ToString("yyyy-MM-dd HH:mm");
                row++;
            }

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

        // ─────────────────────────────────────────────────────────────
        //  PRODUCT SUB-CATEGORY
        // ─────────────────────────────────────────────────────────────

        public async Task<byte[]> GenerateSubCategoryTemplateAsync(long businessUnitId)
        {
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("ProductSubCategories");

            string[] headers = { "Sub-Category Name*", "Description" };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightGoldenrodYellow);
                ws.Column(i + 1).Width = 35;
            }

            // Sample rows
            var samples = new[]
            {
                new object[] { "IT Equipment", "Computers, servers and accessories" },
                new object[] { "Office Supplies", "Stationery and office consumables" },
                new object[] { "Cables & Connectors", "All types of cables and connectors" },
            };

            for (int r = 0; r < samples.Length; r++)
            {
                for (int c = 0; c < samples[r].Length; c++)
                    ws.Cells[r + 2, c + 1].Value = samples[r][c];
            }

            // Instructions sheet
            var info = package.Workbook.Worksheets.Add("Instructions");
            info.Cells[1, 1].Value = "Instructions";
            info.Cells[1, 1].Style.Font.Bold = true;
            info.Cells[2, 1].Value = "• Fields marked with * are required.";
            info.Cells[3, 1].Value = "• Duplicate sub-category names will be skipped.";
            info.Column(1).Width = 80;

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

                /// <summary>
        /// Bulk import entry point. The whole import is run as one retriable unit so that the
        /// transaction it opens is owned by the configured execution strategy — see
        /// <see cref="ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction"/> for the
        /// defect this closes (every upload returned 500 against PostgreSQL).
        /// </summary>
        public Task<ServiceResult<string>> UploadSubCategoryTemplateAsync(Stream fileStream, long businessUnitId, string createdBy) =>
            ERP_RFQ_Automation.Infrastructure.RetriableUploadTransaction.ExecuteAsync(
                _context, fileStream, () => UploadSubCategoryTemplateCoreAsync(fileStream, businessUnitId, createdBy));

        private async Task<ServiceResult<string>> UploadSubCategoryTemplateCoreAsync(Stream fileStream, long businessUnitId, string createdBy)
        {
            using var package = new ExcelPackage(fileStream);
            var ws = package.Workbook.Worksheets[0];
            int rowCount = ws.Dimension?.Rows ?? 0;

            if (rowCount <= 1)
                return ServiceResult<string>.CreateFailure("Excel file is empty or has only headers.");

            int successCount = 0, skipCount = 0, errorCount = 0;
            var errors = new List<string>();

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                for (int row = 2; row <= rowCount; row++)
                {
                    var subCategoryName = ws.Cells[row, 1].Text?.Trim();
                    if (string.IsNullOrWhiteSpace(subCategoryName)) continue;

                    var description = ws.Cells[row, 2].Text?.Trim();

                    try
                    {
                        var existing = await _context.ProductSubCategories
                            .FirstOrDefaultAsync(s => s.SubCategoryName == subCategoryName && s.BusinessUnitId == businessUnitId);

                        if (existing != null)
                        {
                            skipCount++;
                            continue;
                        }

                        var subCategory = new ProductSubCategory
                        {
                            SubCategoryName = subCategoryName,
                            Description = string.IsNullOrWhiteSpace(description) ? null : description,
                            BusinessUnitId = businessUnitId,
                            IsActive = true,
                            CreatedBy = createdBy,
                            CreatedOn = DateTime.UtcNow
                        };

                        _context.ProductSubCategories.Add(subCategory);
                        await _context.SaveChangesAsync();
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        errors.Add($"Row {row} ({subCategoryName}): {ex.Message}");
                    }
                }

                await transaction.CommitAsync();

                var msg = $"{successCount} sub-categories imported successfully. {skipCount} skipped (already exist). {errorCount} errors.";
                if (errors.Any()) msg += " Errors: " + string.Join("; ", errors);

                return ServiceResult<string>.CreateSuccess(msg, msg);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Sub-category upload failed.");
                return ServiceResult<string>.CreateFailure($"Transaction failed: {ex.Message}");
            }
        }

        public async Task<byte[]> ExportSubCategoriesAsync(long businessUnitId)
        {
            var subCategories = await _context.ProductSubCategories
                .Where(s => s.BusinessUnitId == businessUnitId)
                .OrderBy(s => s.SubCategoryName)
                .ToListAsync();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("ProductSubCategories");

            string[] headers = { "ID", "Sub-Category Name", "Description", "Status", "Created On" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(Color.LightGoldenrodYellow);
            }

            int row = 2;
            foreach (var sub in subCategories)
            {
                ws.Cells[row, 1].Value = sub.Id;
                ws.Cells[row, 2].Value = sub.SubCategoryName;
                ws.Cells[row, 3].Value = sub.Description;
                ws.Cells[row, 4].Value = (sub.IsActive ?? true) ? "Active" : "Inactive";
                ws.Cells[row, 5].Value = sub.CreatedOn?.ToString("yyyy-MM-dd HH:mm");
                row++;
            }

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }
    }
}
