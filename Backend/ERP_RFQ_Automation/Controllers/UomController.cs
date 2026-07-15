using ERP_RFQ_Automation.DTOs.UomDTOs;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UomController : ControllerBase
    {
        private readonly IUomRepository _repository;

        public UomController(IUomRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<UomResponseDTO>>> GetAll([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var uoms = await _repository.GetAllAsync(targetBUId);
                return Ok(uoms);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<UomResponseDTO>> GetById(int id)
        {
            try
            {
                var uom = await _repository.GetByIdAsync(id);
                if (uom == null) return NotFound();
                return Ok(uom);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult<UomResponseDTO>> Create(UomCreateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.BusinessUnitId = claimBUId;

                if (dto.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                var uom = await _repository.CreateAsync(dto, userId);
                return CreatedAtAction(nameof(GetById), new { id = uom.UomId }, uom);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPut("{id}")]
        public async Task<ActionResult<UomResponseDTO>> Update(int id, UomUpdateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.BusinessUnitId = claimBUId;

                if (dto.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                var uom = await _repository.UpdateAsync(id, dto, userId);
                return Ok(uom);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(int id)
        {
            try
            {
                var result = await _repository.DeleteAsync(id);
                if (!result) return NotFound();
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }
    }
}
