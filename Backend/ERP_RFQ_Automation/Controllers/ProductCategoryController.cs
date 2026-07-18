// Controllers/ProductCategoryController.cs
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.ProductCategory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]                           
    public class ProductCategoryController : ControllerBase
    {
        private readonly IProductCategoryRepository _repository;

        public ProductCategoryController(IProductCategoryRepository repository)
        {
            _repository = repository;
        }

        // GET: api/ProductCategory?pageNumber=1&pageSize=20&search=electronics&isActive=true&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Product Categories", PermissionAction.View)]
        public async Task<ActionResult<PaginatedProductCategoryResponseDTO>> GetAll(
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

                var categories = await _repository.GetAllAsync(targetBUId);

            var query = categories.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                query = query.Where(c =>
                    c.CategoryName.ToLower().Contains(search) ||
                    (c.Description != null && c.Description.ToLower().Contains(search)));
            }

            if (isActive.HasValue)
                query = query.Where(c => c.IsActive == isActive.Value);

            var total = query.Count();

            var items = query
                .OrderBy(c => c.CategoryName)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(c => new ProductCategoryResponseDTO
                {
                    Id = c.Id,
                    CategoryName = c.CategoryName,
                    Description = c.Description,
                    ParentCategoryId = c.ParentCategoryId,
                    ParentCategoryName = c.ParentCategory != null? c.ParentCategory.CategoryName: null,
                    BusinessUnitId = c.BusinessUnitId,
                    IsActive = c.IsActive ?? true,
                    CreatedBy = c.CreatedBy,
                    CreatedOn = c.CreatedOn,
                    ModifiedBy = c.ModifiedBy,
                    ModifiedOn = c.ModifiedOn
                })
                .ToList();

            return Ok(new PaginatedProductCategoryResponseDTO
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        // GET: api/ProductCategory/5?businessUnitId=1
        [HttpGet("{id}")]
        [RequireModulePermission("Product Categories", PermissionAction.View)]
        public async Task<ActionResult<ProductCategoryResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var category = await _repository.GetByIdWithParentAsync(id, targetBUId);

            if (category == null) return NotFound();

            return Ok(new ProductCategoryResponseDTO
            {
                Id = category.Id,
                CategoryName = category.CategoryName,
                Description = category.Description,
                ParentCategoryId = category.ParentCategoryId,
                ParentCategoryName = category.ParentCategory?.CategoryName,
                BusinessUnitId = category.BusinessUnitId,
                IsActive = category.IsActive ?? true,
                CreatedBy = category.CreatedBy,
                CreatedOn = category.CreatedOn,
                ModifiedBy = category.ModifiedBy,
                ModifiedOn = category.ModifiedOn
            });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        // POST: api/ProductCategory
        [HttpPost]
        [RequireModulePermission("Product Categories", PermissionAction.Create)]
        public async Task<ActionResult<ProductCategoryResponseDTO>> Create([FromBody] ProductCategoryCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var category = new ProductCategory
                {
                    CategoryName = request.CategoryName,
                    Description = request.Description,
                    ParentCategoryId = request.ParentCategoryId,
                    BusinessUnitId = request.BusinessUnitId,
                    IsActive = true,
                    CreatedBy = request.CreatedBy,
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(category);

            var response = new ProductCategoryResponseDTO
            {
                Id = category.Id,
                CategoryName = category.CategoryName,
                Description = category.Description,
                ParentCategoryId = category.ParentCategoryId,
                BusinessUnitId = category.BusinessUnitId,
                IsActive = category.IsActive ?? true,
                CreatedBy = category.CreatedBy,
                CreatedOn = category.CreatedOn
            };

            return CreatedAtAction(nameof(GetById), new { id = category.Id, businessUnitId = category.BusinessUnitId }, response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        // PUT: api/ProductCategory/5
        [HttpPut("{id}")]
        [RequireModulePermission("Product Categories", PermissionAction.Edit)]
        public async Task<IActionResult> Update(long id, [FromBody] ProductCategoryUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var category = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                category.CategoryName = request.CategoryName;
                category.Description = request.Description;
                category.ParentCategoryId = request.ParentCategoryId;
                category.IsActive = request.IsActive ?? true;
                category.ModifiedBy = request.ModifiedBy;
                category.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(category);

                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error updating category: {ex.Message}");
            }
        }

        // DELETE: api/ProductCategory/5?businessUnitId=1
        [HttpDelete("{id}")]
        [RequireModulePermission("Product Categories", PermissionAction.Delete)]
        public async Task<IActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting category: {ex.Message}");
            }
        }
    }
}