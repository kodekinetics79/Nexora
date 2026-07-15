using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class ProductRepository : IProductRepository
    {
        private readonly ErpRfqAutomationContext _context;
        private readonly IWebHostEnvironment _environment;

        public ProductRepository(ErpRfqAutomationContext context, IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        private async Task<string?> SaveAttachmentAsync(IFormFile file, string subFolder)
        {
            if (file == null || file.Length == 0) return null;

            var uploadsFolder = Path.Combine(_environment.WebRootPath, subFolder);
            if (!Directory.Exists(uploadsFolder))
                Directory.CreateDirectory(uploadsFolder);

            var uniqueFileName = $"{Guid.NewGuid()}_{file.FileName}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            return $"/{subFolder}/{uniqueFileName}";
        }

        private bool IsImage(string fileName)
        {
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            return extensions.Contains(Path.GetExtension(fileName).ToLower());
        }

        public async Task<(IEnumerable<ProductResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null, bool? isActive = null)
        {
            IQueryable<Product> query = _context.Products
                .AsNoTracking()
                .Where(p => p.Buid == businessUnitId)
                .Include(p => p.Category)
                .Include(p => p.SubCategory)
                .Include(p => p.Warehouse)
                .Include(p => p.PreferredSupplier)
                .Include(p => p.Bu);

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                query = query.Where(p =>
                    p.PartNo.ToLower().Contains(search) ||
                    (p.ProductName != null && p.ProductName.ToLower().Contains(search)) ||
                    (p.ModelNo != null && p.ModelNo.ToLower().Contains(search)) ||
                    (p.Description != null && p.Description.ToLower().Contains(search)));
            }

            if (isActive.HasValue)
                query = query.Where(p => p.IsActive == isActive.Value);

            int totalItems = await query.CountAsync();

            var products = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var productIds = products.Select(p => p.Id).ToList();

            var attachments = await _context.ProductAttachments
                .Where(a => productIds.Contains(a.InventoryId))
                .ToListAsync();

            var dtos = products.Select(p =>
            {
                var productAttachments = attachments.Where(a => a.InventoryId == p.Id).ToList();
                var imageList = productAttachments.Where(a => IsImage(a.FileName)).Select(a => new ProductAttachmentDTO
                {
                    AttachmentId = a.AttachmentId,
                    FileName = a.FileName,
                    Location = a.Locations,
                    Description = a.Description
                }).ToList();

                var attachmentList = productAttachments.Where(a => !IsImage(a.FileName)).Select(a => new ProductAttachmentDTO
                {
                    AttachmentId = a.AttachmentId,
                    FileName = a.FileName,
                    Location = a.Locations,
                    Description = a.Description
                }).ToList();

                return new ProductResponseDTO
                {
                    Id = p.Id,
                    DocId = p.DocId,
                    ProductName = p.ProductName,
                    PartNo = p.PartNo,
                    ModelNo = p.ModelNo,
                    Description = p.Description,
                    CategoryId = p.CategoryId,
                    CategoryName = p.Category?.CategoryName,
                    QtyOnHand = p.QtyOnHand,
                    ReorderPoint = p.ReorderPoint,
                    UomId = p.UomId,
                    UomName = p.Uom?.UomName,
                    UnitCost = p.UnitCost,
                    SellingPrice = p.SellingPrice,
                    FinalLandedCost = p.FinalLandedCost,
                    FinalSalesPrice = p.FinalSalesPrice,

                    WarehouseId = p.WarehouseId,
                    WarehouseName = p.Warehouse?.WarehouseName,
                    PreferredSupplierId = p.PreferredSupplierId,
                    PreferredSupplierName = p.PreferredSupplier?.Name,
                    PreferredSupplierEmail = p.PreferredSupplier?.ContactEmail,
                    BatchTracking = p.BatchTracking,
                    SerialTracking = p.SerialTracking,
                    ExpirationDate = p.ExpirationDate,
                    Height = p.Height,
                    Width = p.Width,
                    Depth = p.Depth,
                    Weight = p.Weight,
                    Dimensions = p.Dimensions,
                    Barcode = p.Barcode,
                    Qrcode = p.Qrcode,
                    LeadTime = p.LeadTime,
                    Hscode = p.Hscode,
                    CountryOfOrigin = p.CountryOfOrigin,
                    Buid = p.Buid,
                    BusinessUnitName = p.Bu?.BusinessUnitName,
                    IsActive = p.IsActive ?? true,
                    IsCatalogItem = p.IsCatalogItem,
                    SubCategoryId = p.SubCategoryId,
                    SubCategoryName = p.SubCategory?.SubCategoryName,
                    CreatedBy = p.CreatedBy,
                    CreatedOn = p.CreatedOn,
                    ModifiedBy = p.ModifiedBy,
                    ModifiedOn = p.ModifiedOn,
                    Images = imageList,
                    Attachments = attachmentList
                };
            }).ToList();

            return (dtos, totalItems);
        }

        public async Task<Product> GetByIdAsync(long id, long businessUnitId)
        {
            return await _context.Products
                .Include(p => p.Category)
                .Include(p => p.SubCategory)
                .Include(p => p.Warehouse)
                .Include(p => p.PreferredSupplier)
                .Include(p => p.Uom)
                .Include(p => p.Bu)
                .Include(p => p.ProductAttachments)
                .FirstOrDefaultAsync(p => p.Id == id && p.Buid == businessUnitId) ?? throw new KeyNotFoundException($"Product with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Product product, List<IFormFile>? attachments)
        {
            // Validate uniqueness of PartNo within BusinessUnit
            var partNoExists = await _context.Products.AnyAsync(p => p.PartNo == product.PartNo && p.Buid == product.Buid);
            if (partNoExists)
                throw new ArgumentException($"PartNo {product.PartNo} already exists in this Business Unit.");

            // Normalize and Validate foreign keys if provided
            if (product.CategoryId == 0) product.CategoryId = null;
            if (product.CategoryId.HasValue)
            {
                var categoryExists = await _context.ProductCategories.AnyAsync(c => c.Id == product.CategoryId && c.BusinessUnitId == product.Buid);
                if (!categoryExists)
                    throw new ArgumentException($"Category ID {product.CategoryId} does not exist in this Business Unit.");
            }

            if (product.UomId == 0) product.UomId = null;
            if (product.UomId.HasValue)
            {
                if (!await _context.SetUoms.AnyAsync(u => u.UomId == product.UomId && u.BusinessUnitId == product.Buid))
                    throw new ArgumentException($"UOM ID {product.UomId} does not exist in this Business Unit.");
            }

            if (product.SubCategoryId == 0) product.SubCategoryId = null;
            if (product.SubCategoryId.HasValue)
            {
                var subCategoryExists = await _context.ProductSubCategories.AnyAsync(s => s.Id == product.SubCategoryId && s.BusinessUnitId == product.Buid);
                if (!subCategoryExists)
                    throw new ArgumentException($"SubCategory ID {product.SubCategoryId} does not exist in this Business Unit.");
            }

            if (product.WarehouseId == 0) product.WarehouseId = null;
            if (product.WarehouseId.HasValue)
            {
                var warehouseExists = await _context.Warehouses.AnyAsync(w => w.Id == product.WarehouseId && w.BusinessUnitId == product.Buid);
                if (!warehouseExists)
                    throw new ArgumentException($"Warehouse ID {product.WarehouseId} does not exist in this Business Unit.");
            }

            if (product.PreferredSupplierId == 0) product.PreferredSupplierId = null;
            if (product.PreferredSupplierId.HasValue)
            {
                var supplierExists = await _context.Suppliers.AnyAsync(s => s.Id == product.PreferredSupplierId && s.Buid == product.Buid);
                if (!supplierExists)
                    throw new ArgumentException($"PreferredSupplier ID {product.PreferredSupplierId} does not exist in this Business Unit.");
            }


            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == product.Buid);
            if (!buExists)
                throw new ArgumentException($"Business Unit ID {product.Buid} does not exist.");

            // Auto generate DocId
            var maxDoc = await _context.Products
                .Where(p => p.DocId != null && p.DocId.StartsWith("PR"))
                .Select(p => p.DocId)
                .MaxAsync();

            long nextNum = 1;
            if (maxDoc != null)
            {
                nextNum = long.Parse(maxDoc.Substring(2)) + 1;
            }
            product.DocId = "PR" + nextNum.ToString("D8");

            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            // Save attachments to ProductAttachment table
            if (attachments != null && attachments.Any())
            {
                foreach (var file in attachments)
                {
                    var path = await SaveAttachmentAsync(file, "ProductAttachments");
                    if (path != null)
                    {
                        var attachment = new ProductAttachment
                        {
                            InventoryId = product.Id,
                            FileName = file.FileName,
                            Locations = path,
                            Description = null,  // can add from request if needed
                            UploadDate = DateTime.UtcNow,
                            UploadedBy = null,  // set if user ID available
                            CreatedBy = product.CreatedBy,
                            CreatedOn = DateTime.UtcNow
                        };
                        _context.ProductAttachments.Add(attachment);
                    }
                }
                await _context.SaveChangesAsync();
            }
        }

        public async Task UpdateAsync(Product product, long businessUnitId, List<IFormFile>? attachments)
        {
            var existing = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == product.Id && p.Buid == businessUnitId);
            if (existing == null)
                throw new KeyNotFoundException($"Product with ID {product.Id} not found in Business Unit {businessUnitId}.");

            if (product.Buid != businessUnitId)
                throw new ArgumentException("Provided Product Business Unit does not match context.");

            // Validate uniqueness of ProductName (exclude current product)
            var prodExists = await _context.Products.AnyAsync(p => p.ProductName == product.ProductName && p.Buid == businessUnitId && p.Id != product.Id);
            if (prodExists)
                throw new ArgumentException($"Product name {product.ProductName} already exists in this Business Unit.");

            // Preserve DocId
            product.DocId = existing.DocId;

            // Uniqueness check for PartNo (exclude self)
            var partNoExists = await _context.Products.AnyAsync(p => p.PartNo == product.PartNo && p.Buid == businessUnitId && p.Id != product.Id);
            if (partNoExists)
                throw new ArgumentException($"PartNo {product.PartNo} already exists in this Business Unit.");

            // Normalize and Validate foreign keys if provided
            if (product.CategoryId == 0) product.CategoryId = null;
            if (product.CategoryId.HasValue)
            {
                if (!await _context.ProductCategories.AnyAsync(c => c.Id == product.CategoryId && c.BusinessUnitId == businessUnitId))
                    throw new ArgumentException($"Category ID {product.CategoryId} does not exist in this Business Unit.");
            }

            if (product.UomId == 0) product.UomId = null;
            if (product.UomId.HasValue)
            {
                if (!await _context.SetUoms.AnyAsync(u => u.UomId == product.UomId && u.BusinessUnitId == businessUnitId))
                    throw new ArgumentException($"UOM ID {product.UomId} does not exist in this Business Unit.");
            }

            if (product.SubCategoryId == 0) product.SubCategoryId = null;
            if (product.SubCategoryId.HasValue)
            {
                if (!await _context.ProductSubCategories.AnyAsync(s => s.Id == product.SubCategoryId && s.BusinessUnitId == product.Buid))
                    throw new ArgumentException($"SubCategory ID {product.SubCategoryId} does not exist in this Business Unit.");
            }

            if (product.WarehouseId == 0) product.WarehouseId = null;
            if (product.WarehouseId.HasValue)
            {
                if (!await _context.Warehouses.AnyAsync(w => w.Id == product.WarehouseId && w.BusinessUnitId == product.Buid))
                    throw new ArgumentException($"Warehouse ID {product.WarehouseId} does not exist in this Business Unit.");
            }

            if (product.PreferredSupplierId == 0) product.PreferredSupplierId = null;
            if (product.PreferredSupplierId.HasValue)
            {
                if (!await _context.Suppliers.AnyAsync(s => s.Id == product.PreferredSupplierId && s.Buid == product.Buid))
                    throw new ArgumentException($"PreferredSupplier ID {product.PreferredSupplierId} does not exist in this Business Unit.");
            }

            product.ModifiedOn = DateTime.UtcNow;

            _context.Products.Update(product);
            await _context.SaveChangesAsync();

            // Add new attachments
            if (attachments != null && attachments.Any())
            {
                foreach (var file in attachments)
                {
                    var path = await SaveAttachmentAsync(file, "ProductAttachments");
                    if (path != null)
                    {
                        var attachment = new ProductAttachment
                        {
                            InventoryId = product.Id,
                            FileName = file.FileName,
                            Locations = path,
                            Description = null,
                            UploadDate = DateTime.UtcNow,
                            UploadedBy = null,
                            CreatedBy = product.ModifiedBy ?? product.CreatedBy,
                            CreatedOn = DateTime.UtcNow
                        };
                        _context.ProductAttachments.Add(attachment);
                    }
                }
                await _context.SaveChangesAsync();
            }
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var product = await GetByIdAsync(id, businessUnitId);

            // Check for dependencies (e.g., attachments)
            if (await _context.ProductAttachments.AnyAsync(a => a.InventoryId == id))
                throw new InvalidOperationException($"Cannot delete Product with ID {id} because it has associated attachments. Delete attachments first.");

            _context.Products.Remove(product);
            await _context.SaveChangesAsync();
        }

        // Dropdown implementations
        public async Task<List<BusinessUnitLookupDTO>> GetActiveBusinessUnitsAsync()
        {
            return await _context.BusinessUnits
                .Where(b => b.IsActive == true)
                .Select(b => new BusinessUnitLookupDTO
                {
                    Id = b.Id,
                    BusinessUnitName = b.BusinessUnitName,
                    BusinessUnitCode = b.BusinessUnitCode
                })
                .OrderBy(b => b.BusinessUnitName)
                .ToListAsync();
        }

        public async Task<List<ProductCategoryLookupDTO>> GetProductCategoriesAsync(long businessUnitId)
        {
            return await _context.ProductCategories
                .Where(c => c.BusinessUnitId == businessUnitId && c.IsActive == true)
                .Select(c => new ProductCategoryLookupDTO
                {
                    Id = c.Id,
                    CategoryName = c.CategoryName
                })
                .OrderBy(c => c.CategoryName)
                .ToListAsync();
        }

        public async Task<List<LookupItemDTO>> GetItemStatusesAsync()
        {
            return await _context.SetupMasters
                .Where(sm => sm.SetupType.Contains("Status") && sm.IsActive == true)
                .Select(sm => new LookupItemDTO
                {
                    Id = sm.SetupId,
                    Value = sm.SetupValue
                })
                .OrderBy(sm => sm.Value)
                .ToListAsync();
        }

        public async Task<List<SupplierLookupDTO>> GetSuppliersAsync(long businessUnitId)
        {
            return await _context.Suppliers
                .Where(s => s.Buid == businessUnitId && s.IsActive == true)
                .Select(s => new SupplierLookupDTO
                {
                    Id = s.Id,
                    Name = s.Name
                })
                .OrderBy(s => s.Name)
                .ToListAsync();
        }

        public async Task<List<ProductSubCategoryLookupDTO>> GetProductSubCategoriesAsync(long businessUnitId)
        {
            return await _context.ProductSubCategories
                .Where(sc => sc.BusinessUnitId == businessUnitId && sc.IsActive == true)
                .Select(sc => new ProductSubCategoryLookupDTO
                {
                    Id = sc.Id,
                    SubCategoryName = sc.SubCategoryName
                })
                .OrderBy(sc => sc.SubCategoryName)
                .ToListAsync();
        }

        public async Task<List<WarehouseLookupDTO>> GetWarehousesAsync(long businessUnitId)
        {
            return await _context.Warehouses
                .Where(w => w.BusinessUnitId == businessUnitId && w.IsActive == true)
                .Select(w => new WarehouseLookupDTO
                {
                    Id = w.Id,
                    WarehouseName = w.WarehouseName
                })
                .OrderBy(w => w.WarehouseName)
                .ToListAsync();
        }

        public async Task<List<LookupItemDTO>> GetUomsAsync(long businessUnitId)
        {
            return await _context.SetUoms
                .Where(u => u.BusinessUnitId == businessUnitId && u.IsActive == true)
                .Select(u => new LookupItemDTO
                {
                    Id = (long)u.UomId,
                    Value = u.UomName ?? ""
                })
                .OrderBy(u => u.Value)
                .ToListAsync();
        }

        // Product Matching Implementation
        public async Task<ProductMatchResponseDTO> MatchProductAsync(ProductMatchRequestDTO request)
        {
            var response = new ProductMatchResponseDTO();
            if (request == null) return response;

            var partNo = request.PartNo?.Trim().ToLowerInvariant();
            var mfr = request.Manufacturer?.Trim().ToLowerInvariant();
            var desc = request.Description?.Trim().ToLowerInvariant();

            // Step 1: Try exact match on Part Number or Model Number (Normalized)
            if (!string.IsNullOrWhiteSpace(partNo))
            {
                var exactMatch = await _context.Products
                    .AsNoTracking()
                    .Where(p => p.Buid == request.BusinessUnitId && (p.IsActive == null || p.IsActive == true))
                    .Where(p => p.PartNo.ToLower() == partNo || p.ModelNo.ToLower() == partNo || p.ProductName.ToLower() == partNo)
                    .Include(p => p.PreferredSupplier)
                    .FirstOrDefaultAsync();

                if (exactMatch != null)
                {
                    bool mfrMatches = !string.IsNullOrWhiteSpace(mfr) && 
                                     !string.IsNullOrWhiteSpace(exactMatch.PreferredSupplier?.Name) &&
                                     exactMatch.PreferredSupplier.Name.Trim().ToLowerInvariant() == mfr;

                    response.HasExactMatch = true;
                    response.ExactMatch = new ProductMatchSuggestionDTO
                    {
                        ProductId = exactMatch.Id,
                        ProductName = exactMatch.ProductName ?? "",
                        PartNo = exactMatch.PartNo,
                        Manufacturer = exactMatch.PreferredSupplier?.Name,
                        Description = exactMatch.Description,
                        QtyOnHand = exactMatch.QtyOnHand,
                        UnitCost = exactMatch.UnitCost,
                        SellingPrice = exactMatch.SellingPrice,
                        FinalLandedCost = exactMatch.FinalLandedCost,
                        FinalSalesPrice = exactMatch.FinalSalesPrice,
                        LeadTime = exactMatch.LeadTime,
                        PreferredSupplierId = exactMatch.PreferredSupplierId,
                        PreferredSupplierName = exactMatch.PreferredSupplier?.Name,
                        PreferredSupplierEmail = exactMatch.PreferredSupplier?.ContactEmail,
                        MatchConfidence = mfrMatches ? 100 : 95,
                        MatchReason = mfrMatches ? "Exact match on Part Number and Manufacturer" : "Exact match on Part Number"
                    };
                    return response;
                }
            }

            // Step 2: Try matching in Supplier Quoted Items
            if (!string.IsNullOrWhiteSpace(partNo))
            {
                var quotedMatch = await _context.SupplierQuotedItems
                    .AsNoTracking()
                    .Where(q => q.BusinessUnitId == request.BusinessUnitId && q.IsActive)
                    .Where(q => q.ItemName.ToLower() == partNo || (q.Description != null && q.Description.ToLower().Contains(partNo)))
                    .Include(q => q.Supplier)
                    .OrderByDescending(q => q.QuoteDate)
                    .FirstOrDefaultAsync();

                if (quotedMatch != null)
                {
                    response.HasExactMatch = true;
                    response.ExactMatch = new ProductMatchSuggestionDTO
                    {
                        ProductId = 0, // Not in inventory
                        ProductName = quotedMatch.ItemName ?? "",
                        PartNo = partNo.ToUpperInvariant(),
                        Manufacturer = quotedMatch.Supplier?.Name,
                        Description = quotedMatch.Description,
                        QtyOnHand = 0,
                        UnitCost = quotedMatch.UnitPrice,
                        SellingPrice = quotedMatch.UnitPrice,
                        MatchConfidence = 90,
                        MatchReason = "Found in historical Supplier Quotes",
                        PreferredSupplierId = quotedMatch.SupplierId,
                        PreferredSupplierName = quotedMatch.Supplier?.Name,
                        PreferredSupplierEmail = quotedMatch.Supplier?.ContactEmail
                    };
                    return response;
                }
            }

            // Step 3: Load larger product set for fuzzy matching
            var productsForMatching = await _context.Products
                .AsNoTracking()
                .Where(p => p.Buid == request.BusinessUnitId && (p.IsActive == null || p.IsActive == true))
                .OrderByDescending(p => p.ModifiedOn ?? p.CreatedOn)
                .Take(500) // Increased sample size
                .Select(p => new
                {
                    p.Id,
                    p.ProductName,
                    p.PartNo,
                    p.ModelNo,
                    p.Description,
                    p.QtyOnHand,
                    p.UnitCost,
                    p.SellingPrice,
                    p.FinalLandedCost,
                    p.FinalSalesPrice,
                    p.LeadTime,
                    p.PreferredSupplierId,
                    SupplierName = p.PreferredSupplier != null ? p.PreferredSupplier.Name : "",
                    SupplierEmail = p.PreferredSupplier != null ? p.PreferredSupplier.ContactEmail : ""
                })
                .ToListAsync();

            if (!productsForMatching.Any())
                return response;

            // Step 4: Fuzzy matching
            var fuzzyMatches = new List<ProductMatchSuggestionDTO>();

            foreach (var product in productsForMatching)
            {
                int score = 0;
                int nameSim = CalculateSimilarity(request.Description ?? "", product.ProductName ?? "");
                int partSim = CalculateSimilarity(partNo ?? "", product.PartNo ?? "");
                int mfrSim = CalculateSimilarity(mfr ?? "", product.SupplierName ?? "");

                score = (int)((partSim * 0.6) + (mfrSim * 0.2) + (nameSim * 0.2));

                if (score >= 60)
                {
                    fuzzyMatches.Add(new ProductMatchSuggestionDTO
                    {
                        ProductId = product.Id,
                        ProductName = product.ProductName ?? "",
                        PartNo = product.PartNo,
                        Manufacturer = product.SupplierName,
                        Description = product.Description,
                        QtyOnHand = product.QtyOnHand,
                        UnitCost = product.UnitCost,
                        SellingPrice = product.SellingPrice,
                        FinalLandedCost = product.FinalLandedCost,
                        FinalSalesPrice = product.FinalSalesPrice,
                        LeadTime = product.LeadTime,
                        PreferredSupplierId = product.PreferredSupplierId,
                        PreferredSupplierName = product.SupplierName,
                        PreferredSupplierEmail = product.SupplierEmail,
                        MatchConfidence = score,
                        MatchReason = $"Fuzzy match ({score}%)"
                    });
                }
            }

            response.FuzzyMatches = fuzzyMatches.OrderByDescending(m => m.MatchConfidence).Take(5).ToList();
            return response;
        }

        private ProductMatchSuggestionDTO MapToProductMatchSuggestion(Product product, int confidence, string reason)
        {
            return new ProductMatchSuggestionDTO
            {
                ProductId = product.Id,
                ProductName = product.ProductName ?? "",
                PartNo = product.PartNo,
                Manufacturer = product.PreferredSupplier?.Name,
                Description = product.Description,
                QtyOnHand = product.QtyOnHand,
                UnitCost = product.UnitCost,
                SellingPrice = product.SellingPrice,
                FinalLandedCost = product.FinalLandedCost,
                FinalSalesPrice = product.FinalSalesPrice,

                LeadTime = product.LeadTime,
                PreferredSupplierId = product.PreferredSupplierId,
                PreferredSupplierName = product.PreferredSupplier?.Name,
                PreferredSupplierEmail = product.PreferredSupplier?.ContactEmail,
                MatchConfidence = confidence,
                MatchReason = reason
            };
        }

        // Levenshtein Distance Algorithm for fuzzy matching
        private static int CalculateSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return 0;

            source = source.ToLowerInvariant();
            target = target.ToLowerInvariant();

            int distance = ComputeLevenshteinDistance(source, target);
            int maxLength = Math.Max(source.Length, target.Length);
            
            if (maxLength == 0)
                return 100;

            return (int)((1.0 - (double)distance / maxLength) * 100);
        }

        private static int ComputeLevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        public async Task<StockDetailsDTO> GetStockDetailsAsync(long productId, long businessUnitId)
        {
            var product = await _context.Products
                .AsNoTracking()
                .Where(p => p.Id == productId && p.Buid == businessUnitId)
                .Include(p => p.Warehouse)
                .FirstOrDefaultAsync();

            if (product == null)
                throw new KeyNotFoundException($"Product with ID {productId} not found.");

            // Check if product has purchase history (check RFQ items for now)
            bool hasPurchaseHistory = await _context.Rfqitems
                .AnyAsync(ri => ri.ProductId == productId);

            return new StockDetailsDTO
            {
                ProductId = product.Id,
                ProductName = product.ProductName ?? "",
                PartNo = product.PartNo,
                QtyOnHand = product.QtyOnHand,
                ReorderPoint = product.ReorderPoint,
                WarehouseName = product.Warehouse?.WarehouseName,
                StockPartNumber = product.PartNo,
                UnitCost = product.UnitCost,
                SellingPrice = product.SellingPrice,
                FinalLandedCost = product.FinalLandedCost,
                FinalSalesPrice = product.FinalSalesPrice,
                ReplacementCost = product.UnitCost, // Using UnitCost as replacement cost

                Currency = "USD", // Default currency, should be from product or warehouse
                LeadTime = product.LeadTime,
                HasPurchaseHistory = hasPurchaseHistory
            };
        }

        public async Task<PurchaseHistoryDTO> GetPurchaseHistoryAsync(long productId, long businessUnitId)
        {
            // Get RFQ items for this product (as purchase history)
            var rfqItems = await _context.Rfqitems
                .AsNoTracking()
                .Where(ri => ri.ProductId == productId)
                .Include(ri => ri.Rfq)
                .Include(ri => ri.Supplier)
                .OrderByDescending(ri => ri.CreatedDate)
                .Take(10)
                .ToListAsync();

            var history = rfqItems.Select(ri => new PurchaseHistoryItemDTO
            {
                OrderId = ri.Rfqid,
                OrderNumber = ri.Rfq.Rfqno,
                OrderDate = ri.CreatedDate,
                SupplierName = ri.Supplier?.Name,
                Quantity = ri.Quantity,
                UnitPrice = ri.UnitPrice ?? 0,
                Currency = ri.Currency
            }).ToList();

            return new PurchaseHistoryDTO
            {
                ProductId = productId,
                PurchaseHistory = history
            };
        }
    }
}