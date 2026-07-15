using ERP_RFQ_Automation.DTOs.ModuleDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ModuleController : ControllerBase
    {
        private readonly IModuleRepository _repository;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public ModuleController(IModuleRepository repository)
        {
            _repository = repository;
        }

        // GET: api/Module?pageNumber=1&pageSize=10&id=1&moduleName=admin&isActive=true&businessUnitId=1
        [HttpGet]
        public async Task<ActionResult<PaginatedResponseDTO<ModuleResponseDTO>>> GetAll(
           
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? moduleName = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (modules, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, moduleName, isActive);
                var moduleDTOs = modules.Select(MapToResponse).ToList();

                var response = new PaginatedResponseDTO<ModuleResponseDTO>
                {
                    Items = moduleDTOs,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // GET: api/Module/5
        [HttpGet("{id}")]
        public async Task<ActionResult<ModuleResponseDTO>> GetById(long id)
        {
            try
            {
               
                var module = await _repository.GetByIdAsync(id);
                return Ok(MapToResponse(module));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // POST: api/Module
        [HttpPost]
        public async Task<ActionResult<ModuleResponseDTO>> Create([FromBody] ModuleCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var module = new Module
                {
                    ModuleName = request.ModuleName,
                    Description = request.Description,
                    IsActive = true, // Always true on create
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(module);

                var response = MapToResponse(module);
                return CreatedAtAction(nameof(GetById), new { id = module.Id }, response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error creating data: {ex.Message}");
            }
        }

        // PUT: api/Module/5
        [HttpPut("{id}")]
        public async Task<ActionResult> Update(long id, [FromBody] ModuleUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var existing = await _repository.GetByIdAsync(id);

                existing.ModuleName = request.ModuleName;
                existing.Description = request.Description;
                existing.IsActive = request.IsActive ?? true;
                existing.ModifiedBy = User.Identity?.Name ?? request.ModifiedBy ?? "System";
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error updating data: {ex.Message}");
            }
        }

        // DELETE: api/Module/5
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                await _repository.DeleteAsync(id);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting data: {ex.Message}");
            }
        }

        private ModuleResponseDTO MapToResponse(Module module)
        {
            return new ModuleResponseDTO
            {
                Id = module.Id,
                ModuleName = module.ModuleName,
                Description = module.Description,
                IsActive = module.IsActive,
                CreatedBy = module.CreatedBy,
                CreatedOn = module.CreatedOn,
                ModifiedBy = module.ModifiedBy,
                ModifiedOn = module.ModifiedOn,
            };
        }
    }
}