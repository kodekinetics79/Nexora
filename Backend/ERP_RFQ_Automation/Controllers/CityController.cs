using ERP_RFQ_Automation.DTOs.LocationDTOs;
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
                var city = await _repository.GetByIdAsync(id);
                if (city == null) return NotFound();
                return Ok(city);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPost]
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
        public async Task<ActionResult<CityResponseDTO>> Update(int id, CityUpdateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.Buid = claimBUId;

                if (dto.Buid <= 0) return BadRequest("Business Unit ID is required.");

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
