// Controllers/ProductCategoryController.cs
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.ProductCategory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Security;

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

        /// <summary>
        /// RFC 7807 body carrying the request's trace identifier, so a caller reporting a failure
        /// gives support an id that ties straight back to the server log entry. Same helper, same
        /// shape and same name as the one on <c>ProductController</c>; deliberately NOT called
        /// <c>Problem</c>, because <see cref="ControllerBase"/> already declares that and two
        /// same-named helpers with different return types in one file is a trap.
        /// </summary>
        private ProblemDetails TracedProblem(int status, string title, string detail)
        {
            var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            return problem;
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
                return this.ServerError(ex, "Error.");
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
                return this.ServerError(ex, "Error.");
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
                    // RC-7 / Sec-A1: server-derived from the validated token, never the body.
                    CreatedBy = ActorContext.From(User).Stamp,
                    CreatedOn = DateTime.UtcNow
                };

                try
                {
                    await _repository.AddAsync(category);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "22001" })
                {
                    // 22001 ONLY — "value too long for type character varying(n)". Deliberately not a
                    // blanket catch: a bare catch(DbUpdateException) would also swallow foreign-key
                    // violations (23503), unique violations (23505) and RLS denials (42501 — this
                    // codebase is deny-by-default under nexora_tenant_isolation), and would report every
                    // one of them to the operator as "shorten the description" while removing the log
                    // entry that says what actually happened. Everything else falls through to the
                    // catch-all below, which still LOGS it and answers a 500.
                    //
                    // The DTO caps now mirror the columns, so this should be unreachable for the fields
                    // this screen writes. It stays as the backstop for the ones it does not.
                    return BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Category not created",
                        "One of the values is too long for the field it is stored in. Shorten it and try again."));
                }
                catch (ArgumentException ex) when (ex is not (ArgumentNullException or ArgumentOutOfRangeException))
                {
                    // The subclasses are EXCLUDED, and that exclusion is the point of the filter.
                    // ArgumentNullException and ArgumentOutOfRangeException both derive from
                    // ArgumentException, and neither is ever a message to the operator — each is a bug
                    // in this process. Caught here, one would be reported to the user as a duplicate
                    // category name or a bad parent id: a sentence about their data describing a fault
                    // in ours, sending them to correct a field that was never wrong. Excluded, it falls
                    // through to the catch-all, which logs it with the path and the tenant.
                    //
                    // ProductCategoryRepository signals a taken category name, a self-parent, a
                    // cross-BU move and a missing parent through the SAME exception type, so the message
                    // text is the only thing that separates a conflict from a bad request.
                    //
                    // This IS message-sniffing and it is knowingly accepted for now: it works against
                    // today's strings and breaks silently the day somebody rewords one. The durable fix
                    // is a typed exception (or a result object) out of ProductCategoryRepository; until
                    // then a reworded message degrades to 400 rather than 409, which is wrong but not
                    // harmful.
                    var duplicate = ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
                    return duplicate
                        ? Conflict(TracedProblem(StatusCodes.Status409Conflict, "Category not created", ex.Message))
                        : BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Category not created", ex.Message));
                }

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
                return this.ServerError(ex, "Error.");
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
                category.ModifiedBy = ActorContext.From(User).Stamp;
                category.ModifiedOn = DateTime.UtcNow;

                try
                {
                    await _repository.UpdateAsync(category);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "22001" })
                {
                    // 22001 ONLY — "value too long for type character varying(n)". Deliberately not a
                    // blanket catch: a bare catch(DbUpdateException) would also swallow foreign-key
                    // violations (23503), unique violations (23505) and RLS denials (42501 — this
                    // codebase is deny-by-default under nexora_tenant_isolation), and would report every
                    // one of them to the operator as "shorten the description" while removing the log
                    // entry that says what actually happened. Everything else falls through to the
                    // catch-all below, which still LOGS it and answers a 500.
                    //
                    // The DTO caps now mirror the columns, so this should be unreachable for the fields
                    // this screen writes. It stays as the backstop for the ones it does not.
                    return BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Category not saved",
                        "One of the values is too long for the field it is stored in. Shorten it and try again."));
                }
                catch (ArgumentException ex) when (ex is not (ArgumentNullException or ArgumentOutOfRangeException))
                {
                    // The subclasses are EXCLUDED, and that exclusion is the point of the filter.
                    // ArgumentNullException and ArgumentOutOfRangeException both derive from
                    // ArgumentException, and neither is ever a message to the operator — each is a bug
                    // in this process. Caught here, one would be reported to the user as a duplicate
                    // category name or a bad parent id: a sentence about their data describing a fault
                    // in ours, sending them to correct a field that was never wrong. Excluded, it falls
                    // through to the catch-all, which logs it with the path and the tenant.
                    //
                    // ProductCategoryRepository signals a taken category name, a self-parent, a
                    // cross-BU move and a missing parent through the SAME exception type, so the message
                    // text is the only thing that separates a conflict from a bad request.
                    //
                    // This IS message-sniffing and it is knowingly accepted for now: it works against
                    // today's strings and breaks silently the day somebody rewords one. The durable fix
                    // is a typed exception (or a result object) out of ProductCategoryRepository; until
                    // then a reworded message degrades to 400 rather than 409, which is wrong but not
                    // harmful.
                    var duplicate = ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
                    return duplicate
                        ? Conflict(TracedProblem(StatusCodes.Status409Conflict, "Category not saved", ex.Message))
                        : BadRequest(TracedProblem(StatusCodes.Status400BadRequest, "Category not saved", ex.Message));
                }

                return NoContent();
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error updating category.");
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
                return this.ServerError(ex, "Error deleting category.");
            }
        }
    }
}