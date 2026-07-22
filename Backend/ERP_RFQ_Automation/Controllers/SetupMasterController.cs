using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.DTOs.SetupDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SetupMasterController : ControllerBase
    {
        private readonly ISetupMasterRepository _repository;

        public SetupMasterController(ISetupMasterRepository repository)
        {
            _repository = repository;
        }

        // GET: api/SetupMaster?pageNumber=1&pageSize=10&setupId=1&setupType=Type1&setupCode=CODE1&setupName=Name1&isActive=true&businessUnitId=1
        [HttpGet]
        public async Task<ActionResult<PaginatedSetupMasterResponseDTO>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? setupId = null,
            [FromQuery] string? setupType = null,
            [FromQuery] string? setupCode = null,
            [FromQuery] string? setupName = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var setupMasters = await _repository.GetAllAsync();

                // Apply filters
                var filteredSetupMasters = setupMasters.AsQueryable();

                if (setupId.HasValue)
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupId == setupId.Value);

                if (!string.IsNullOrWhiteSpace(setupType))
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupType != null && s.SetupType.Contains(setupType, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(setupCode))
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupCode != null && s.SetupCode.Contains(setupCode, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(setupName))
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.SetupValue != null && s.SetupValue.Contains(setupName, StringComparison.OrdinalIgnoreCase));

                if (isActive.HasValue)
                    filteredSetupMasters = filteredSetupMasters.Where(s => s.IsActive == isActive.Value);

                int totalItems = filteredSetupMasters.Count();
                var resultItems = filteredSetupMasters
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(MapToResponse);

                var response = new PaginatedSetupMasterResponseDTO
                {
                    Items = resultItems.ToList(),
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // GET: api/SetupMaster/5
        [HttpGet("{id}")]
        public async Task<ActionResult<SetupMasterResponseDTO>> GetById(long id)
        {
            try
            {
                var setupMaster = await _repository.GetByIdAsync(id);
                return Ok(MapToResponse(setupMaster));
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

        [HttpPost]
        [RequireManagerRole]
        public async Task<ActionResult<SetupMasterResponseDTO>> Create([FromBody] SetupMasterCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // BusinessUnitId is no longer strictly required for lookup but might still be stored
                // for legacy reasons or future use. We'll leave it in the DTO but won't use it for filtering.

                // Normalize: treat 0 as null
                long? parentId = (request.ParentSetupId.HasValue && request.ParentSetupId.Value == 0)
                    ? null
                    : request.ParentSetupId;

                var setupMaster = new SetupMaster
                {
                    SetupType = request.SetupType,
                    SetupCode = request.SetupCode,
                    SetupValue = request.SetupName,
                    Description = request.Description,
                    ParentSetupId = parentId,
                    BusinessUnitId = 1, // Store as System/Global BU
                    IsActive = request.IsActive,
                    CreatedBy = request.CreatedBy,
                    CreatedOn = DateTime.UtcNow
                };

                // Extra safety: if parentId == setupMaster.SetupId (unlikely on create), throw
                if (parentId.HasValue && parentId.Value == setupMaster.SetupId)
                    return BadRequest("ParentSetupId cannot be same as the entity SetupId.");

                await _repository.AddAsync(setupMaster);

                var response = MapToResponse(setupMaster);
                return CreatedAtAction(nameof(GetById), new { id = setupMaster.SetupId, businessUnitId = setupMaster.BusinessUnitId }, response);
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

        [HttpPut("{id}")]
        [RequireManagerRole]
        public async Task<ActionResult> Update(long id, [FromBody] SetupMasterUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var existing = await _repository.GetByIdAsync(id);

                // Normalize parent id (treat 0 as null)
                long? parentId = (request.ParentSetupId.HasValue && request.ParentSetupId.Value == 0)
                    ? null
                    : request.ParentSetupId;

                // Prevent self-parenting
                if (parentId.HasValue && parentId.Value == existing.SetupId)
                    return BadRequest("ParentSetupId cannot be same as the entity SetupId.");

                // Update relevant fields
                existing.SetupType = request.SetupType;
                existing.SetupCode = request.SetupCode;
                existing.SetupValue = request.SetupName;
                existing.Description = request.Description;
                existing.ParentSetupId = parentId;
                existing.BusinessUnitId = 1; // Ensure it's assigned to System/Global BU
                existing.IsActive = request.IsActive;
                existing.ModifiedBy = request.ModifiedBy;
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

        [HttpDelete("{id}")]
        [RequireManagerRole]
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

        // Helper: Map entity to response DTO
        private SetupMasterResponseDTO MapToResponse(SetupMaster setupMaster)
        {
            return new SetupMasterResponseDTO
            {
                SetupId = setupMaster.SetupId,
                SetupType = setupMaster.SetupType,
                SetupCode = setupMaster.SetupCode,
                SetupName = setupMaster.SetupValue,
                Description = setupMaster.Description,
                ParentSetupId = setupMaster.ParentSetupId,

                IsActive = setupMaster.IsActive,
                CreatedBy = setupMaster.CreatedBy,
                CreatedOn = setupMaster.CreatedOn,
                ModifiedBy = setupMaster.ModifiedBy,
                ModifiedOn = setupMaster.ModifiedOn
            };
        }
    }
}
