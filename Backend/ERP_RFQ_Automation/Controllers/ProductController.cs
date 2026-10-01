using DocumentFormat.OpenXml.InkML;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Traceability;
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
        private readonly IMasterDataChangeHistoryReader _changeHistory;

        public ProductController(
            IProductRepository repository,
            ErpRfqAutomationContext context,
            IMasterDataChangeHistoryReader changeHistory)
        {
            _repository = repository;
            _context = context;
            _changeHistory = changeHistory;
        }

        private bool TryGetTenantId(out long businessUnitId) =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId) && businessUnitId > 0;

        /// <summary>
        /// RFC 7807 body carrying the request's trace identifier, so a caller reporting a failure
        /// gives support an id that ties straight back to the server log entry. Mirrors the helper
        /// on the sibling master-data controller (SupplierController).
        ///
        /// <para>NOT named <c>Problem</c>. <see cref="ControllerBase"/> already declares
        /// <c>Problem(...)</c>, which this class calls six times to RETURN a 500
        /// (<c>ObjectResult</c>); this helper BUILDS a body (<c>ProblemDetails</c>) to be handed to
        /// <c>BadRequest</c>/<c>Conflict</c>. Overloaded on argument shape, the two bound correctly
        /// but read identically at every call site, and the compiler would have silently picked the
        /// other one the day an argument list drifted. The distinct name makes which one is meant
        /// visible in the call rather than inferable from the arguments.</para>
        /// </summary>
        private ProblemDetails TracedProblem(int status, string title, string detail)
        {
            var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            return problem;
        }

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
            [FromQuery] bool? isActive = null,
            [FromQuery] string stock = "all",
            [FromQuery] long? warehouseId = null,
            [FromQuery] string sortBy = "partNo",
            [FromQuery] string sortDirection = "asc")
        {
            try
            {
                _ = businessUnitId; // Retained for client compatibility; authenticated tenant is authoritative.
                if (!TryGetTenantId(out var targetBUId)) return Forbid();
                if (pageNumber < 1) return BadRequest("Page number must be ≥ 1.");
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000) return BadRequest("Page size must be between 1 and 1000.");

                var listQuery = new ProductListQuery(stock, warehouseId, sortBy, sortDirection);
                if (listQuery.ValidationError is { } queryError)
                    return BadRequest(TracedProblem(400, "Invalid product query", queryError));
                if (warehouseId.HasValue && !await _context.Warehouses.AsNoTracking()
                    .AnyAsync(w => w.Id == warehouseId && w.BusinessUnitId == targetBUId))
                    return BadRequest(TracedProblem(400, "Invalid product query", "Warehouse is not available."));

                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search, isActive, listQuery);
                var materialized = items.ToList();
                var ids = materialized.Select(x => x.Id).ToArray();
                var stockRows = await _context.Set<Models.Inventory>().AsNoTracking()
                    .Where(x => x.Buid == targetBUId && x.ProductId.HasValue && ids.Contains(x.ProductId.Value)
                        && (!warehouseId.HasValue || x.WarehouseId == warehouseId))
                    .Select(x => new
                    {
                        x.Id,
                        ProductId = x.ProductId!.Value,
                        x.WarehouseId,
                        WarehouseName = x.Warehouse == null ? null : x.Warehouse.WarehouseName,
                        x.QtyOnHand,
                        x.AllocatedQuantity,
                        x.QuarantineQuantity,
                        x.DamagedQuantity,
                        x.ExpiredQuantity,
                        x.SafetyStockQuantity,
                        x.ReorderPoint,
                        x.MinimumLevel,
                        x.MaximumLevel,
                    })
                    .ToListAsync();
                var stockIds = stockRows.Select(x => x.Id).ToArray();
                var reservations = !InventoryReleaseScope.ReservationsEnabled || stockIds.Length == 0
                    ? new Dictionary<long, decimal>()
                    : await _context.StockReservations.AsNoTracking()
                        .Where(x => x.BusinessUnitId == targetBUId && stockIds.Contains(x.InventoryId)
                            && x.Status == StockReservationStatus.Active)
                        .GroupBy(x => x.InventoryId)
                        .Select(x => new { InventoryId = x.Key, Quantity = x.Sum(y => y.Quantity) })
                        .ToDictionaryAsync(x => x.InventoryId, x => x.Quantity);
                List<IncomingInventory> incomingRows = ids.Length == 0
                    ? new List<IncomingInventory>()
                    : await _context.IncomingInventory.AsNoTracking()
                        .Where(x => x.BusinessUnitId == targetBUId && ids.Contains(x.ProductId)
                            && (!warehouseId.HasValue || x.WarehouseId == warehouseId)
                            && (x.Status == IncomingInventoryStatus.Ordered
                                || x.Status == IncomingInventoryStatus.Confirmed
                                || x.Status == IncomingInventoryStatus.InTransit
                                || x.Status == IncomingInventoryStatus.PartiallyReceived))
                        .ToListAsync();
                var lotSummaries = ids.Length == 0
                    ? new Dictionary<long, (int LotCount, int QuarantinedLotCount, decimal RemainingQuantity)>()
                    : (await _context.MaterialLots.AsNoTracking()
                        .Where(x => x.BusinessUnitId == targetBUId && ids.Contains(x.ProductId))
                        .GroupBy(x => x.ProductId)
                        .Select(group => new
                        {
                            ProductId = group.Key,
                            LotCount = group.Count(),
                            QuarantinedLotCount = group.Count(x => x.Status == MaterialLotStatuses.Quarantined),
                            RemainingQuantity = group.Sum(x => x.QuantityReceived - x.QuantityConsumed),
                        })
                        .ToListAsync())
                        .ToDictionary(x => x.ProductId, x => (x.LotCount, x.QuarantinedLotCount, x.RemainingQuantity));
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var expiredCertificates = ids.Length == 0
                    ? new Dictionary<long, int>()
                    : await (from certificate in _context.MaterialLotCertificates.AsNoTracking()
                             join lot in _context.MaterialLots.AsNoTracking()
                                 on certificate.MaterialLotId equals lot.Id
                             where certificate.BusinessUnitId == targetBUId
                                 && lot.BusinessUnitId == targetBUId
                                 && ids.Contains(lot.ProductId)
                                 && certificate.ExpiresOn.HasValue
                                 && certificate.ExpiresOn.Value < today
                             group certificate by lot.ProductId into certificateGroup
                             select new { ProductId = certificateGroup.Key, Count = certificateGroup.Count() })
                        .ToDictionaryAsync(x => x.ProductId, x => x.Count);

                foreach (var item in materialized)
                {
                    var itemStock = stockRows.Where(x => x.ProductId == item.Id).ToList();
                    item.QtyOnHand = itemStock.Sum(x => x.QtyOnHand);
                    item.ReservedQuantity = itemStock.Sum(x => reservations.GetValueOrDefault(x.Id));
                    item.AvailableQuantity = itemStock.Sum(x => InventoryQuantityMath.AvailableToPromise(
                        x.QtyOnHand,
                        reservations.GetValueOrDefault(x.Id),
                        x.AllocatedQuantity,
                        x.QuarantineQuantity,
                        x.DamagedQuantity,
                        x.ExpiredQuantity,
                        x.SafetyStockQuantity));
                    item.WarehouseCount = itemStock.Where(x => x.WarehouseId.HasValue)
                        .Select(x => x.WarehouseId!.Value).Distinct().Count();
                    item.StockLocationSummary = string.Join(", ", itemStock
                        .Select(x => x.WarehouseName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct()
                        .OrderBy(name => name));
                    if (itemStock.Count > 0 && string.IsNullOrWhiteSpace(item.StockLocationSummary))
                        item.StockLocationSummary = "Warehouse not assigned";
                    var reorderConditions = itemStock.Select(stockRow =>
                    {
                        var available = InventoryQuantityMath.AvailableToPromise(
                            stockRow.QtyOnHand,
                            reservations.GetValueOrDefault(stockRow.Id),
                            stockRow.AllocatedQuantity,
                            stockRow.QuarantineQuantity,
                            stockRow.DamagedQuantity,
                            stockRow.ExpiredQuantity,
                            stockRow.SafetyStockQuantity);
                        var inbound = incomingRows.Where(incoming => incoming.ProductId == item.Id
                                && incoming.WarehouseId == stockRow.WarehouseId)
                            .Sum(incoming => incoming.OpenQuantity);
                        var condition = ReorderAlertService.Classify(
                            available + inbound,
                            stockRow.QtyOnHand,
                            stockRow.MinimumLevel,
                            stockRow.MaximumLevel,
                            stockRow.ReorderPoint);
                        return new
                        {
                            condition.Kind,
                            condition.Threshold,
                            condition.Shortfall,
                            stockRow.WarehouseName,
                        };
                    }).ToList();
                    item.ReorderStatus = new[]
                    {
                        ReorderAlertKinds.OutOfStock,
                        ReorderAlertKinds.BelowMinimum,
                        ReorderAlertKinds.ReorderPoint,
                        ReorderAlertKinds.Overstock,
                    }.FirstOrDefault(kind => reorderConditions.Any(condition => condition.Kind == kind));
                    var governingConditions = reorderConditions
                        .Where(condition => condition.Kind == item.ReorderStatus)
                        .ToList();
                    item.ReorderGap = governingConditions.Sum(condition => condition.Shortfall);
                    item.ReorderThreshold = governingConditions.Sum(condition => condition.Threshold);
                    item.ReorderWarehouseSummary = string.Join(", ", governingConditions
                        .Select(condition => condition.WarehouseName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct()
                        .OrderBy(name => name));

                    var itemIncoming = incomingRows.Where(x => x.ProductId == item.Id && x.OpenQuantity > 0m).ToList();
                    item.IncomingQuantity = itemIncoming.Sum(x => x.OpenQuantity);
                    item.NextIncomingOn = itemIncoming.Count == 0 ? null : itemIncoming.Min(x => x.ExpectedOn);
                    item.IncomingCommitmentCount = itemIncoming.Count;
                    item.IncomingWarehouseCount = itemIncoming.Select(x => x.WarehouseId).Distinct().Count();
                    if (lotSummaries.TryGetValue(item.Id, out var lotSummary))
                    {
                        item.MaterialLotCount = lotSummary.LotCount;
                        item.QuarantinedLotCount = lotSummary.QuarantinedLotCount;
                        item.MaterialLotRemainingQuantity = lotSummary.RemainingQuantity;
                    }
                    item.ExpiredCertificateCount = expiredCertificates.GetValueOrDefault(item.Id);
                }

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
                    PriceCurrencyId = product.PriceCurrencyId,
                    PriceCurrencyCode = product.PriceCurrency?.Code,
                    FinalLandedCost = product.FinalLandedCost,
                    FinalSalesPrice = product.FinalSalesPrice,
                    WarehouseId = product.WarehouseId,
                    WarehouseName = product.Warehouse?.WarehouseName,
                    PreferredSupplierId = product.PreferredSupplierId,
                    PreferredSupplierName = product.PreferredSupplier?.Name,
                    PreferredSupplierEmail = product.PreferredSupplier?.ContactEmail,
                    PreferredSupplierTier = product.PreferredSupplier?.Tier,
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
            // A product that does not exist in the caller's tenant is a 404, not a server error.
            //
            // ProductRepository.GetByIdAsync ends in `?? throw new KeyNotFoundException(...)`, so it
            // never returns null and the `if (product == null) return NotFound()` above is
            // unreachable. Without this handler that throw fell into the blanket catch below and
            // GET /api/Product/{id} answered 500 for every missing id — including the very first
            // one a fresh tenant asks for. GetStockDetails on this same controller already
            // translates the exception; this brings the read path into line with it.
            catch (KeyNotFoundException)
            {
                return NotFound();
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
                PriceCurrencyId = request.PriceCurrencyId,
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
            try
            {
                await _repository.AddAsync(product, request.Attachments);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "22001" })
            {
                // 22001 ONLY — "value too long for type character varying(n)". Deliberately not a
                // blanket catch: a bare catch(DbUpdateException) would also swallow foreign-key
                // violations (23503), unique violations (23505), RLS denials (42501 — this codebase
                // is deny-by-default under nexora_tenant_isolation) and serialization failures from
                // the serializable transaction in AllocateProductDocIdAsync, and would report every
                // one of them to the operator as "shorten the product name" while removing the log
                // entry that says what actually happened. Everything else escapes to the global
                // handler, on purpose.
                //
                // The DTO caps now mirror the columns, so this should be unreachable for the fields
                // this screen writes. It stays as the backstop for the ones it does not.
                return BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Product not created",
                    "One of the values is too long for the field it is stored in. Shorten it and try again."));
            }
            catch (ArgumentException ex) when (ex is not (ArgumentNullException or ArgumentOutOfRangeException))
            {
                // The subclasses are EXCLUDED, and that exclusion is the point of the filter.
                // ArgumentNullException and ArgumentOutOfRangeException both derive from
                // ArgumentException, and neither is ever a message to the operator — each is a bug
                // in this process. The traced path: PersistAttachmentAsync calls
                // Path.Combine(_environment.WebRootPath, subFolder), and WebRootPath is null on any
                // deployment without a wwwroot directory, so Path.Combine throws
                // ArgumentNullException. Caught here, a product saved WITH an attachment on such a
                // deployment would be reported to the user as a duplicate part number or a bad
                // category id — a sentence about their data describing a fault in ours, sending
                // them to correct a field that was never wrong. Excluded, it reaches the global
                // handler and stays in the log where somebody can fix the deployment.
                //
                // The repository signals a taken part number and five "does not exist" reference
                // failures through the SAME exception type, so the message text is the only thing
                // that separates a conflict from a bad request.
                //
                // This IS message-sniffing and it is knowingly accepted for now: it works against
                // today's strings and breaks silently the day somebody rewords one. The durable fix
                // is a typed exception (or a result object) out of ProductRepository; until then a
                // reworded message degrades to 400 rather than 409, which is wrong but not harmful.
                var duplicate = ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
                return duplicate
                    ? Conflict(TracedProblem(StatusCodes.Status409Conflict, "Product not created", ex.Message))
                    : BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Product not created", ex.Message));
            }

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
                PriceCurrencyId = savedProduct.PriceCurrencyId,
                PriceCurrencyCode = savedProduct.PriceCurrency?.Code,
                FinalLandedCost = savedProduct.FinalLandedCost,
                FinalSalesPrice = savedProduct.FinalSalesPrice,

                WarehouseId = savedProduct.WarehouseId,
                WarehouseName = savedProduct.Warehouse?.WarehouseName,
                PreferredSupplierId = savedProduct.PreferredSupplierId,
                PreferredSupplierName = savedProduct.PreferredSupplier?.Name,
                PreferredSupplierEmail = savedProduct.PreferredSupplier?.ContactEmail,
                PreferredSupplierTier = savedProduct.PreferredSupplier?.Tier,
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


        /// <summary>
        /// Puts a part into the catalogue, or keeps it out, without editing anything else. Used by
        /// the RFQ line's "Add to catalogue" for a part suppliers were asked about while kept out.
        /// </summary>
        [HttpPost("{id:long}/catalogue")]
        [RequireModulePermission("Products", PermissionAction.Edit)]
        public async Task<IActionResult> SetCatalogue(long id, [FromBody] SetProductCatalogueRequest request)
        {
            if (!TryGetTenantId(out var tenant)) return Forbid();
            var product = await _context.Products.SingleOrDefaultAsync(x => x.Id == id && x.Buid == tenant);
            if (product is null) return NotFound();
            product.IsCatalogItem = request.IsCatalogItem;
            product.ModifiedBy = Actor();
            product.ModifiedOn = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(new { product.Id, product.IsCatalogItem });
        }

        public sealed record SetProductCatalogueRequest(bool IsCatalogItem);

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
            if (request.ApplyPricing)
            {
                product.UnitCost = request.UnitCost;
                product.SellingPrice = request.SellingPrice;
                product.PriceCurrencyId = request.PriceCurrencyId;
            }

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

            try
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();
                    await _repository.UpdateAsync(product, request.Buid, request.Attachments, request.ApplyPricing);

                    // The product default and every warehouse row are one logical edit. Committing
                    // these separately used to let a failed stock sync return an error after the
                    // product and its price had already changed.
                    var stockRows = await _context.Set<Models.Inventory>()
                        .Where(x => x.Buid == request.Buid && x.ProductId == id
                                    && x.ReorderPoint != product.ReorderPoint)
                        .ToListAsync();
                    foreach (var row in stockRows)
                    {
                        row.ReorderPoint = product.ReorderPoint;
                        row.ModifiedBy = Actor();
                        row.ModifiedOn = DateTime.UtcNow;
                    }
                    if (stockRows.Count > 0) await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "22001" })
            {
                // 22001 ONLY — "value too long for type character varying(n)". Deliberately not a
                // blanket catch: a bare catch(DbUpdateException) would also swallow foreign-key
                // violations (23503), unique violations (23505), RLS denials (42501 — this codebase
                // is deny-by-default under nexora_tenant_isolation) and serialization failures from
                // the serializable transaction in AllocateProductDocIdAsync, and would report every
                // one of them to the operator as "shorten the product name" while removing the log
                // entry that says what actually happened. Everything else escapes to the global
                // handler, on purpose.
                //
                // The DTO caps now mirror the columns, so this should be unreachable for the fields
                // this screen writes. It stays as the backstop for the ones it does not.
                return BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Product not saved",
                    "One of the values is too long for the field it is stored in. Shorten it and try again."));
            }
            catch (ArgumentException ex) when (ex is not (ArgumentNullException or ArgumentOutOfRangeException))
            {
                // The subclasses are EXCLUDED, and that exclusion is the point of the filter.
                // ArgumentNullException and ArgumentOutOfRangeException both derive from
                // ArgumentException, and neither is ever a message to the operator — each is a bug
                // in this process. The traced path: PersistAttachmentAsync calls
                // Path.Combine(_environment.WebRootPath, subFolder), and WebRootPath is null on any
                // deployment without a wwwroot directory, so Path.Combine throws
                // ArgumentNullException. Caught here, a product saved WITH an attachment on such a
                // deployment would be reported to the user as a duplicate part number or a bad
                // category id — a sentence about their data describing a fault in ours, sending
                // them to correct a field that was never wrong. Excluded, it reaches the global
                // handler and stays in the log where somebody can fix the deployment.
                //
                // The repository signals a taken part number and five "does not exist" reference
                // failures through the SAME exception type, so the message text is the only thing
                // that separates a conflict from a bad request.
                //
                // This IS message-sniffing and it is knowingly accepted for now: it works against
                // today's strings and breaks silently the day somebody rewords one. The durable fix
                // is a typed exception (or a result object) out of ProductRepository; until then a
                // reworded message degrades to 400 rather than 409, which is wrong but not harmful.
                var duplicate = ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
                return duplicate
                    ? Conflict(TracedProblem(StatusCodes.Status409Conflict, "Product not saved", ex.Message))
                    : BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Product not saved", ex.Message));
            }

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
                PriceCurrencyId = savedProduct.PriceCurrencyId,
                PriceCurrencyCode = savedProduct.PriceCurrency?.Code,
                FinalLandedCost = savedProduct.FinalLandedCost,
                FinalSalesPrice = savedProduct.FinalSalesPrice,

                WarehouseId = savedProduct.WarehouseId,
                WarehouseName = savedProduct.Warehouse?.WarehouseName,
                PreferredSupplierId = savedProduct.PreferredSupplierId,
                PreferredSupplierName = savedProduct.PreferredSupplier?.Name,
                PreferredSupplierEmail = savedProduct.PreferredSupplier?.ContactEmail,
                PreferredSupplierTier = savedProduct.PreferredSupplier?.Tier,
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

        [HttpGet("lookups/currencies")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult> GetCurrencies()
        {
            if (!TryGetTenantId(out var targetBUId)) return Forbid();
            var currencies = await _context.Currencies.AsNoTracking()
                .Where(x => x.BusinessUnitId == targetBUId && x.IsActive == true)
                .OrderByDescending(x => x.IsBaseCurrency == true)
                .ThenBy(x => x.Code)
                .Select(x => new { x.Id, x.Code, IsBase = x.IsBaseCurrency == true })
                .ToListAsync();
            return Ok(currencies);
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

        /// <summary>
        /// FR-MDM-05 — the before/after trail for one product, newest first.
        ///
        /// <para>This is the endpoint that makes <c>FinalLandedCost</c> answerable. That column is
        /// the cost basis reported margin is computed from, it is hand-editable on the product
        /// screen and through column 28 of the import sheet, and before register item E44 was
        /// closed nothing anywhere recorded who moved it or from what.</para>
        /// </summary>
        [HttpGet("{id}/change-history")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<MasterDataChangeEventDto>>> GetChangeHistory(
            long id, [FromQuery] int limit = 50)
        {
            if (!TryGetTenantId(out var targetBUId)) return Forbid();

            try
            {
                return Ok(await _changeHistory.ReadAsync(
                    MasterDataEntityTypes.Product, id, targetBUId, limit, HttpContext.RequestAborted));
            }
            catch (Exception)
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "The product change history could not be loaded.");
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
