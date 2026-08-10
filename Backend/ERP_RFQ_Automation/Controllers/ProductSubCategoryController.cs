using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.ProductSubCategory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]   
    public class ProductSubCategoryController : ControllerBase
    {
        private readonly IProductSubCategoryRepository _repository;

        public ProductSubCategoryController(IProductSubCategoryRepository repository)
        {
            _repository = repository;
        }

        // GET: api/ProductSubCategory?pageNumber=1&pageSize=20&search=electronics&businessUnitId=1&isActive=true
        [HttpGet]
        [RequireModulePermission("Product Categories", PermissionAction.View)]
        public async Task<ActionResult<PaginatedProductSubCategoryResponseDTO>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be ≥ 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var subCategories = await _repository.GetAllAsync(targetBUId);
                var query = subCategories.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLowerInvariant();
                query = query.Where(s =>
                    s.SubCategoryName.ToLowerInvariant().Contains(search) ||
                    (s.Description != null && s.Description.ToLowerInvariant().Contains(search)));
            }

            if (isActive.HasValue)
                query = query.Where(s => (s.IsActive ?? true) == isActive.Value);

            var total = query.Count();

            var items = query
                .OrderBy(s => s.SubCategoryName)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(s => new ProductSubCategoryResponseDTO
                {
                    Id = s.Id,
                    SubCategoryName = s.SubCategoryName,
                    Description = s.Description,
                    BusinessUnitId = s.BusinessUnitId,
                    IsActive = s.IsActive ?? true,
                    CreatedBy = s.CreatedBy,
                    CreatedOn = s.CreatedOn,
                    ModifiedBy = s.ModifiedBy,
                    ModifiedOn = s.ModifiedOn
                })
                .ToList();

            return Ok(new PaginatedProductSubCategoryResponseDTO
            {
                Items = items,
                TotalItems = total,
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling(total / (double)pageSize)
            });
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error.");
            }
        }

        // GET: api/ProductSubCategory/5?businessUnitId=1
        [HttpGet("{id}")]
        [RequireModulePermission("Product Categories", PermissionAction.View)]
        public async Task<ActionResult<ProductSubCategoryResponseDTO>> GetById(int id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var sub = await _repository.GetByIdAsync(id, targetBUId);

            return Ok(new ProductSubCategoryResponseDTO
            {
                Id = sub.Id,
                SubCategoryName = sub.SubCategoryName,
                Description = sub.Description,
                BusinessUnitId = sub.BusinessUnitId,
                IsActive = sub.IsActive ?? true,
                CreatedBy = sub.CreatedBy,
                CreatedOn = sub.CreatedOn,
                ModifiedBy = sub.ModifiedBy,
                ModifiedOn = sub.ModifiedOn
            });
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error.");
            }
        }

        // POST: api/ProductSubCategory
        [HttpPost]
        [RequireModulePermission("Product Categories", PermissionAction.Create)]
        public async Task<ActionResult<ProductSubCategoryResponseDTO>> Create([FromBody] ProductSubCategoryCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var subCategory = new ProductSubCategory
                {
                    SubCategoryName = request.SubCategoryName,
                    Description = request.Description,
                    BusinessUnitId = request.BusinessUnitId,
                    IsActive = true,
                    // RC-7 / Sec-A1: server-derived from the validated token, never the body.
                    CreatedBy = ActorContext.From(User).Stamp,
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(subCategory);

            var response = new ProductSubCategoryResponseDTO
            {
                Id = subCategory.Id,
                SubCategoryName = subCategory.SubCategoryName,
                Description = subCategory.Description,
                BusinessUnitId = subCategory.BusinessUnitId,
                IsActive = subCategory.IsActive ?? true,
                CreatedBy = subCategory.CreatedBy,
                CreatedOn = subCategory.CreatedOn
            };

            return CreatedAtAction(nameof(GetById), new { id = subCategory.Id, businessUnitId = subCategory.BusinessUnitId }, response);
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error.");
            }
        }

        // PUT: api/ProductSubCategory/5
        [HttpPut("{id}")]
        [RequireModulePermission("Product Categories", PermissionAction.Edit)]
        public async Task<IActionResult> Update(int id, [FromBody] ProductSubCategoryUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var subCategory = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                subCategory.SubCategoryName = request.SubCategoryName;
                subCategory.Description = request.Description;
                subCategory.IsActive = request.IsActive ?? true;
                subCategory.ModifiedBy = ActorContext.From(User).Stamp;
                subCategory.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(subCategory);

                return NoContent();
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error updating sub-category.");
            }
        }

        // DELETE: api/ProductSubCategory/5?businessUnitId=1
        [HttpDelete("{id}")]
        [RequireModulePermission("Product Categories", PermissionAction.Delete)]
        public async Task<IActionResult> Delete(int id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);

                return NoContent();
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error deleting sub-category.");
            }
        }
    }
}