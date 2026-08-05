using DocumentFormat.OpenXml.InkML;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ProductController : ControllerBase
    {
        private readonly IProductRepository _repository;
        private readonly ErpRfqAutomationContext _context;

        public ProductController(IProductRepository repository, ErpRfqAutomationContext context)
        {
            _repository = repository;
            _context = context;
        }

        private bool TryGetTenantId(out long businessUnitId) =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId) && businessUnitId > 0;

        private string Actor() => User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirst("email")?.Value
            ?? throw new UnauthorizedAccessException("Authenticated actor identity is required.");

        [HttpGet]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<PaginatedProductResponseDTO>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? search = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                _ = businessUnitId; // Retained for client compatibility; authenticated tenant is authoritative.
                if (!TryGetTenantId(out var targetBUId)) return Forbid();
                if (pageNumber < 1) return BadRequest("Page number must be ≥ 1.");
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000) return BadRequest("Page size must be between 1 and 1000.");

                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search, isActive);
                var materialized = items.ToList();
                var ids = materialized.Select(x => x.Id).ToArray();
                var stock = await _context.Set<Models.Inventory>().AsNoTracking().Where(x => x.Buid == targetBUId &&
                        x.ProductId.HasValue && ids.Contains(x.ProductId.Value))
                    .GroupBy(x => x.ProductId!.Value).Select(x => new { x.Key, Quantity = x.Sum(y => y.QtyOnHand) })
                    .ToDictionaryAsync(x => x.Key, x => x.Quantity);
                materialized.ForEach(x => x.QtyOnHand = stock.GetValueOrDefault(x.Id));

                return Ok(new PaginatedProductResponseDTO
                {
                    Items = materialized,
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                });
            }
            catch (Exception)
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "The product list could not be loaded.");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<ProductResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                _ = businessUnitId;
                if (!TryGetTenantId(out var targetBUId)) return Forbid();

                var product = await _repository.GetByIdAsync(id, targetBUId);
                if (product == null) return NotFound();

                var images = product.ProductAttachments?
                    .Where(a => IsImage(a.FileName))
                    .Select(a => new ProductAttachmentDTO
                    {
                        AttachmentId = a.AttachmentId,
                        FileName = a.FileName,
                        Location = a.Locations,
                        Description = a.Description
                    }).ToList() ?? new List<ProductAttachmentDTO>();

                var attachments = product.ProductAttachments?
                    .Where(a => !IsImage(a.FileName))
                    .Select(a => new ProductAttachmentDTO
                    {
                        AttachmentId = a.AttachmentId,
                        FileName = a.FileName,
                        Location = a.Locations,
                        Description = a.Description
                    }).ToList() ?? new List<ProductAttachmentDTO>();

                var dto = new ProductResponseDTO
                {
                    Id = product.Id,
                    DocId = product.DocId,
                    ProductName = product.ProductName,
                    PartNo = product.PartNo,
                    ModelNo = product.ModelNo,
                    Description = product.Description,
                    CategoryId = product.CategoryId,
                    CategoryName = product.Category?.CategoryName,
                    QtyOnHand = await _context.Set<Models.Inventory>().AsNoTracking().Where(x => x.Buid == targetBUId &&
                        x.ProductId == product.Id).SumAsync(x => (decimal?)x.QtyOnHand) ?? 0m,
                    ReorderPoint = product.ReorderPoint,
                    UomId = product.UomId,
                    UomName = product.Uom?.UomName,
                    UnitCost = product.UnitCost,
                    SellingPrice = product.SellingPrice,
                    FinalLandedCost = product.FinalLandedCost,
                    FinalSalesPrice = product.FinalSalesPrice,
                    WarehouseId = product.WarehouseId,
                    WarehouseName = product.Warehouse?.WarehouseName,
                    PreferredSupplierId = product.PreferredSupplierId,
                    PreferredSupplierName = product.PreferredSupplier?.Name,
                    PreferredSupplierEmail = product.PreferredSupplier?.ContactEmail,
                    BatchTracking = product.BatchTracking,
                    SerialTracking = product.SerialTracking,
                    ExpirationDate = product.ExpirationDate,
                    Height = product.Height,
                    Width = product.Width,
                    Depth = product.Depth,
                    Weight = product.Weight,
                    Dimensions = product.Dimensions,
                    Barcode = product.Barcode,
                    Qrcode = product.Qrcode,
                    LeadTime = product.LeadTime,
                    Hscode = product.Hscode,
                    CountryOfOrigin = product.CountryOfOrigin,
                    Buid = product.Buid,
                    BusinessUnitName = product.Bu?.BusinessUnitName,
                    IsActive = product.IsActive ?? true,
                    IsCatalogItem = product.IsCatalogItem,
                    SubCategoryId = product.SubCategoryId,
                    SubCategoryName = product.SubCategory?.SubCategoryName,
                    CreatedBy = product.CreatedBy,
                    CreatedOn = product.CreatedOn,
                    ModifiedBy = product.ModifiedBy,
                    ModifiedOn = product.ModifiedOn,
                    Images = images,
                    Attachments = attachments
                };

                return Ok(dto);
            }
            catch (Exception)
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "The product could not be loaded.");
            }
        }

        private bool IsImage(string fileName)
        {
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            return extensions.Contains(System.IO.Path.GetExtension(fileName).ToLower());
        }

        [HttpPost]
        [RequireModulePermission("Products", PermissionAction.Create)]
        public async Task<ActionResult<ProductResponseDTO>> Create([FromForm] ProductCreateRequestDTO request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            
            if (!TryGetTenantId(out var claimBUId)) return Forbid();
            request.Buid = claimBUId;

            var product = new Product
            {
                ProductName = request.ProductName,
                PartNo = request.PartNo,
                ModelNo = request.ModelNo,
                Description = request.Description,
                CategoryId = request.CategoryId,
                QtyOnHand = 0m,
                ReorderPoint = request.ReorderPoint,
                UomId = request.UomId,
                UnitCost = request.UnitCost,
                SellingPrice = request.SellingPrice,
                FinalLandedCost = request.FinalLandedCost,
                FinalSalesPrice = request.FinalSalesPrice,

                WarehouseId = request.WarehouseId,
                PreferredSupplierId = request.PreferredSupplierId,
                BatchTracking = request.BatchTracking,
                SerialTracking = request.SerialTracking,
                ExpirationDate = request.ExpirationDate,
                Height = request.Height,
                Width = request.Width,
                Depth = request.Depth,
                Weight = request.Weight,
                Dimensions = request.Dimensions,
                Barcode = request.Barcode,
                Qrcode = request.Qrcode,
                LeadTime = request.LeadTime,
                Hscode = request.Hscode,
                CountryOfOrigin = request.CountryOfOrigin,
                Buid = request.Buid,
                IsActive = request.IsActive,
                IsCatalogItem = request.IsCatalogItem,
                SubCategoryId = request.SubCategoryId,
                CreatedBy = Actor(),
                CreatedOn = DateTime.UtcNow
            };
            await _repository.AddAsync(product, request.Attachments);

            // Reload the product to include attachments
            var savedProduct = await _repository.GetByIdAsync(product.Id, request.Buid);

            var images = savedProduct.ProductAttachments.Where(a => IsImage(a.FileName)).Select(a => new ProductAttachmentDTO
            {
                AttachmentId = a.AttachmentId,
                FileName = a.FileName,
                Location = a.Locations,
                Description = a.Description
            }).ToList();

            var attachments = savedProduct.ProductAttachments.Where(a => !IsImage(a.FileName)).Select(a => new ProductAttachmentDTO
            {
                AttachmentId = a.AttachmentId,
                FileName = a.FileName,
                Location = a.Locations,
                Description = a.Description
            }).ToList();

            var response = new ProductResponseDTO
            {
                Id = savedProduct.Id,
                DocId = savedProduct.DocId,
                ProductName = savedProduct.ProductName,
                PartNo = savedProduct.PartNo,
                ModelNo = savedProduct.ModelNo,
                Description = savedProduct.Description,
                CategoryId = savedProduct.CategoryId,
                CategoryName = savedProduct.Category?.CategoryName,
                QtyOnHand = await _context.Set<Models.Inventory>().AsNoTracking().Where(x => x.Buid == request.Buid &&
                    x.ProductId == savedProduct.Id).SumAsync(x => (decimal?)x.QtyOnHand) ?? 0m,
                ReorderPoint = savedProduct.ReorderPoint,
                UomId = savedProduct.UomId,
                UomName = savedProduct.Uom?.UomName,
                UnitCost = savedProduct.UnitCost,
                SellingPrice = savedProduct.SellingPrice,
                FinalLandedCost = savedProduct.FinalLandedCost,
                FinalSalesPrice = savedProduct.FinalSalesPrice,

                WarehouseId = savedProduct.WarehouseId,
                WarehouseName = savedProduct.Warehouse?.WarehouseName,
                PreferredSupplierId = savedProduct.PreferredSupplierId,
                PreferredSupplierName = savedProduct.PreferredSupplier?.Name,
                PreferredSupplierEmail = savedProduct.PreferredSupplier?.ContactEmail,
                BatchTracking = savedProduct.BatchTracking,
                SerialTracking = savedProduct.SerialTracking,
                ExpirationDate = savedProduct.ExpirationDate,
                Height = savedProduct.Height,
                Width = savedProduct.Width,
                Depth = savedProduct.Depth,
                Weight = savedProduct.Weight,
                Dimensions = savedProduct.Dimensions,
                Barcode = savedProduct.Barcode,
                Qrcode = savedProduct.Qrcode,
                LeadTime = savedProduct.LeadTime,
                Hscode = savedProduct.Hscode,
                CountryOfOrigin = savedProduct.CountryOfOrigin,
                Buid = savedProduct.Buid,
                BusinessUnitName = savedProduct.Bu?.BusinessUnitName,
                IsActive = savedProduct.IsActive ?? true,
                IsCatalogItem = savedProduct.IsCatalogItem,
                SubCategoryId = savedProduct.SubCategoryId,
                SubCategoryName = savedProduct.SubCategory?.SubCategoryName,
                CreatedBy = savedProduct.CreatedBy,
                CreatedOn = savedProduct.CreatedOn,
                ModifiedBy = savedProduct.ModifiedBy,
                ModifiedOn = savedProduct.ModifiedOn,
                Images = images,
                Attachments = attachments
            };
            return CreatedAtAction(nameof(GetById), new { id = savedProduct.Id, businessUnitId = savedProduct.Buid }, response);
        }


        [HttpPut("{id}")]
        [RequireModulePermission("Products", PermissionAction.Edit)]
        public async Task<ActionResult<ProductResponseDTO>> Update(long id, [FromForm] ProductUpdateRequestDTO request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            
            if (!TryGetTenantId(out var claimBUId)) return Forbid();
            request.Buid = claimBUId;

            var product = await _repository.GetByIdAsync(id, request.Buid);
            product.ProductName = request.ProductName;
            product.PartNo = request.PartNo;
            product.ModelNo = request.ModelNo;
            product.Description = request.Description;
            product.CategoryId = request.CategoryId;
            product.ReorderPoint = request.ReorderPoint;
            product.UomId = request.UomId;
            product.UnitCost = request.UnitCost;
            product.SellingPrice = request.SellingPrice;
            product.FinalLandedCost = request.FinalLandedCost;
            product.FinalSalesPrice = request.FinalSalesPrice;

            product.WarehouseId = request.WarehouseId;
            product.PreferredSupplierId = request.PreferredSupplierId;
            product.BatchTracking = request.BatchTracking;
            product.SerialTracking = request.SerialTracking;
            product.ExpirationDate = request.ExpirationDate;
            product.Height = request.Height;
            product.Width = request.Width;
            product.Depth = request.Depth;
            product.Weight = request.Weight;
            product.Dimensions = request.Dimensions;
            product.Barcode = request.Barcode;
            product.Qrcode = request.Qrcode;
            product.LeadTime = request.LeadTime;
            product.Hscode = request.Hscode;
            product.CountryOfOrigin = request.CountryOfOrigin;
            product.IsActive = request.IsActive;
            product.IsCatalogItem = request.IsCatalogItem;
            product.SubCategoryId = request.SubCategoryId;
            product.ModifiedBy = Actor();
            product.ModifiedOn = DateTime.UtcNow;

            await _repository.UpdateAsync(product, request.Buid, request.Attachments);

            // Reload the product to include attachments
            var savedProduct = await _repository.GetByIdAsync(id, request.Buid);

            var images = savedProduct.ProductAttachments.Where(a => IsImage(a.FileName)).Select(a => new ProductAttachmentDTO
            {
                AttachmentId = a.AttachmentId,
                FileName = a.FileName,
                Location = a.Locations,
                Description = a.Description
            }).ToList();

            var attachments = savedProduct.ProductAttachments.Where(a => !IsImage(a.FileName)).Select(a => new ProductAttachmentDTO
            {
                AttachmentId = a.AttachmentId,
                FileName = a.FileName,
                Location = a.Locations,
                Description = a.Description
            }).ToList();

            var response = new ProductResponseDTO
            {
                Id = savedProduct.Id,
                DocId = savedProduct.DocId,
                ProductName = savedProduct.ProductName,
                PartNo = savedProduct.PartNo,
                ModelNo = savedProduct.ModelNo,
                Description = savedProduct.Description,
                CategoryId = savedProduct.CategoryId,
                CategoryName = savedProduct.Category?.CategoryName,
                QtyOnHand = await _context.Set<Models.Inventory>().AsNoTracking().Where(x => x.Buid == request.Buid &&
                    x.ProductId == savedProduct.Id).SumAsync(x => (decimal?)x.QtyOnHand) ?? 0m,
                ReorderPoint = savedProduct.ReorderPoint,
                UomId = savedProduct.UomId,
                UomName = savedProduct.Uom?.UomName,
                UnitCost = savedProduct.UnitCost,
                SellingPrice = savedProduct.SellingPrice,
                FinalLandedCost = savedProduct.FinalLandedCost,
                FinalSalesPrice = savedProduct.FinalSalesPrice,

                WarehouseId = savedProduct.WarehouseId,
                WarehouseName = savedProduct.Warehouse?.WarehouseName,
                PreferredSupplierId = savedProduct.PreferredSupplierId,
                PreferredSupplierName = savedProduct.PreferredSupplier?.Name,
                PreferredSupplierEmail = savedProduct.PreferredSupplier?.ContactEmail,
                BatchTracking = savedProduct.BatchTracking,
                SerialTracking = savedProduct.SerialTracking,
                ExpirationDate = savedProduct.ExpirationDate,
                Height = savedProduct.Height,
                Width = savedProduct.Width,
                Depth = savedProduct.Depth,
                Weight = savedProduct.Weight,
                Dimensions = savedProduct.Dimensions,
                Barcode = savedProduct.Barcode,
                Qrcode = savedProduct.Qrcode,
                LeadTime = savedProduct.LeadTime,
                Hscode = savedProduct.Hscode,
                CountryOfOrigin = savedProduct.CountryOfOrigin,
                Buid = savedProduct.Buid,
                BusinessUnitName = savedProduct.Bu?.BusinessUnitName,
                IsActive = savedProduct.IsActive ?? true,
                IsCatalogItem = savedProduct.IsCatalogItem,
                SubCategoryId = savedProduct.SubCategoryId,
                SubCategoryName = savedProduct.SubCategory?.SubCategoryName,
                CreatedBy = savedProduct.CreatedBy,
                CreatedOn = savedProduct.CreatedOn,
                ModifiedBy = savedProduct.ModifiedBy,
                ModifiedOn = savedProduct.ModifiedOn,
                Images = images,
                Attachments = attachments
            };
            return Ok(response);
        }

        [HttpDelete("{id}")]
        [RequireModulePermission("Products", PermissionAction.Delete)]
        public async Task<IActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();

            try
            {
                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                // The repository blocks deletes that would orphan stock, movements or incoming
                // supply. That is a conflict the caller can act on, not a server fault — this
                // action had no handler at all, so those turned into bare 500s.
                return Conflict(new { error = ex.Message });
            }
        }

        // Dropdown endpoints
        [HttpGet("lookups/business-units")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<BusinessUnitLookupDTO>>> GetBusinessUnits()
        {
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            var businessUnit = await _context.BusinessUnits.AsNoTracking()
                .Where(x => x.Id == targetBUId && x.IsActive != false)
                .Select(x => new BusinessUnitLookupDTO
                {
                    Id = x.Id,
                    BusinessUnitName = x.BusinessUnitName,
                    BusinessUnitCode = x.BusinessUnitCode
                }).ToListAsync();
            return Ok(businessUnit);
        }

        [HttpGet("lookups/product-categories")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<ProductCategoryLookupDTO>>> GetProductCategories([FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            return Ok(await _repository.GetProductCategoriesAsync(targetBUId));
        }

        [HttpGet("lookups/item-statuses")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<LookupItemDTO>>> GetItemStatuses()
        {
            return Ok(await _repository.GetItemStatusesAsync());
        }

        [HttpGet("lookups/suppliers")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<SupplierLookupDTO>>> GetSuppliers([FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            return Ok(await _repository.GetSuppliersAsync(targetBUId));
        }

        [HttpGet("lookups/product-subcategories")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<ProductSubCategoryLookupDTO>>> GetProductSubCategories([FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            return Ok(await _repository.GetProductSubCategoriesAsync(targetBUId));
        }

        [HttpGet("lookups/warehouses")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<WarehouseLookupDTO>>> GetWarehouses([FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            return Ok(await _repository.GetWarehousesAsync(targetBUId));
        }

        [HttpGet("lookups/uoms")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<List<LookupItemDTO>>> GetUoms([FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            return Ok(await _repository.GetUomsAsync(targetBUId));
        }

        // Product Matching Endpoints
        [HttpPost("match-product")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<ProductMatchResponseDTO>> MatchProduct([FromBody] ProductMatchRequestDTO request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            
            if (!TryGetTenantId(out var claimBUId)) return Forbid();
            request.BusinessUnitId = claimBUId;

            try
            {
                var result = await _repository.MatchProductAsync(request);
                return Ok(result);
            }
            catch (Exception)
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "The product match could not be completed.");
            }
        }

        [HttpGet("{id}/stock-details")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<StockDetailsDTO>> GetStockDetails(long id, [FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();

            try
            {
                var result = await _repository.GetStockDetailsAsync(id, targetBUId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception)
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "Stock details could not be loaded.");
            }
        }

        [HttpGet("{id}/purchase-history")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<PurchaseHistoryDTO>> GetPurchaseHistory(long id, [FromQuery] long? businessUnitId = null)
        {
            _ = businessUnitId;
            if (!TryGetTenantId(out var targetBUId)) return Forbid();

            try
            {
                var result = await _repository.GetPurchaseHistoryAsync(id, targetBUId);
                return Ok(result);
            }
            catch (Exception)
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "Purchase history could not be loaded.");
            }
        }
    }
}
