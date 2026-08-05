using ERP_RFQ_Automation.DTOs.LocationDTOs;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CityController : ControllerBase
    {
        private readonly ICityRepository _repository;

        public CityController(ICityRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<CityResponseDTO>>> GetAll([FromQuery] long? buid = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (buid ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var cities = await _repository.GetAllAsync(targetBUId);
                return Ok(cities);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<CityResponseDTO>> GetById(int id)
        {
            try
            {
                // SEC-H2: the repository lookup carries no tenant predicate, so ownership
                // has to be enforced here or any tenant can read another tenant's row.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return BadRequest("Business Unit ID is required.");

                var city = await _repository.GetByIdAsync(id);
                if (city == null || city.Buid != claimBUId) return NotFound();
                return Ok(city);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPost]
        [RequireManagerRole]
        public async Task<ActionResult<CityResponseDTO>> Create(CityCreateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.Buid = claimBUId;

                if (dto.Buid <= 0) return BadRequest("Business Unit ID is required.");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                var city = await _repository.CreateAsync(dto, userId);
                return CreatedAtAction(nameof(GetById), new { id = city.CityId }, city);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPut("{id}")]
        [RequireManagerRole]
        public async Task<ActionResult<CityResponseDTO>> Update(int id, CityUpdateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.Buid = claimBUId;

                if (dto.Buid <= 0) return BadRequest("Business Unit ID is required.");

                // SEC-H2: UpdateAsync resolves the row by primary key alone. Without this
                // ownership check a tenant could rewrite - and by rewriting Buid, steal -
                // another tenant's reference row.
                var existing = await _repository.GetByIdAsync(id);
                if (existing == null || existing.Buid != claimBUId) return NotFound();

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                var city = await _repository.UpdateAsync(id, dto, userId);
                return Ok(city);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpDelete("{id}")]
        [RequireManagerRole]
        public async Task<ActionResult> Delete(int id)
        {
            try
            {
                // SEC-H2: DeleteAsync resolves the row by primary key alone; verify the row
                // belongs to the caller's tenant before removing it.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id);
                if (existing == null || existing.Buid != claimBUId) return NotFound();

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
