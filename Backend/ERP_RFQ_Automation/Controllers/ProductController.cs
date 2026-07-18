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
            _context = context;
        }

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
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
                if (pageNumber < 1) return BadRequest("Page number must be ≥ 1.");
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000) return BadRequest("Page size must be between 1 and 1000.");

                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search, isActive);

                return Ok(new PaginatedProductResponseDTO
                {
                    Items = items,
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<ProductResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

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
                    QtyOnHand = product.QtyOnHand,
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
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
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
            
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId > 0) request.Buid = claimBUId;

            if (request.Buid <= 0) return BadRequest("Business Unit ID is required.");

            var product = new Product
            {
                ProductName = request.ProductName,
                PartNo = request.PartNo,
                ModelNo = request.ModelNo,
                Description = request.Description,
                CategoryId = request.CategoryId,
                QtyOnHand = request.QtyOnHand,
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
                CreatedBy = request.CreatedBy,
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
                QtyOnHand = savedProduct.QtyOnHand,
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
            
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId > 0) request.Buid = claimBUId;

            if (request.Buid <= 0) return BadRequest("Business Unit ID is required.");

            var product = await _repository.GetByIdAsync(id, request.Buid);
            product.ProductName = request.ProductName;
            product.PartNo = request.PartNo;
            product.ModelNo = request.ModelNo;
            product.Description = request.Description;
            product.CategoryId = request.CategoryId;
            product.QtyOnHand = request.QtyOnHand;
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
            product.ModifiedBy = request.ModifiedBy;
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
                QtyOnHand = savedProduct.QtyOnHand,
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
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

            await _repository.DeleteAsync(id, targetBUId);

            return NoContent();
        }

        // Dropdown endpoints
        [HttpGet("lookups/business-units")]
        public async Task<ActionResult<List<BusinessUnitLookupDTO>>> GetBusinessUnits()
        {
            return Ok(await _repository.GetActiveBusinessUnitsAsync());
        }

        [HttpGet("lookups/product-categories")]
        public async Task<ActionResult<List<ProductCategoryLookupDTO>>> GetProductCategories([FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
            return Ok(await _repository.GetProductCategoriesAsync(targetBUId));
        }

        [HttpGet("lookups/item-statuses")]
        public async Task<ActionResult<List<LookupItemDTO>>> GetItemStatuses()
        {
            return Ok(await _repository.GetItemStatusesAsync());
        }

        [HttpGet("lookups/suppliers")]
        public async Task<ActionResult<List<SupplierLookupDTO>>> GetSuppliers([FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
            return Ok(await _repository.GetSuppliersAsync(targetBUId));
        }

        [HttpGet("lookups/product-subcategories")]
        public async Task<ActionResult<List<ProductSubCategoryLookupDTO>>> GetProductSubCategories([FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
            return Ok(await _repository.GetProductSubCategoriesAsync(targetBUId));
        }

        [HttpGet("lookups/warehouses")]
        public async Task<ActionResult<List<WarehouseLookupDTO>>> GetWarehouses([FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
            return Ok(await _repository.GetWarehousesAsync(targetBUId));
        }

        [HttpGet("lookups/uoms")]
        public async Task<ActionResult<List<LookupItemDTO>>> GetUoms([FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
            return Ok(await _repository.GetUomsAsync(targetBUId));
        }

        // Product Matching Endpoints
        [HttpPost("match-product")]
        public async Task<ActionResult<ProductMatchResponseDTO>> MatchProduct([FromBody] ProductMatchRequestDTO request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId > 0) request.BusinessUnitId = claimBUId;

            if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var result = await _repository.MatchProductAsync(request);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to match product: {ex.Message}");
            }
        }

        [HttpGet("{id}/stock-details")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<StockDetailsDTO>> GetStockDetails(long id, [FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var result = await _repository.GetStockDetailsAsync(id, targetBUId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to get stock details: {ex.Message}");
            }
        }

        [HttpGet("{id}/purchase-history")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<PurchaseHistoryDTO>> GetPurchaseHistory(long id, [FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var result = await _repository.GetPurchaseHistoryAsync(id, targetBUId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Failed to get purchase history: {ex.Message}");
            }
        }
    }
}