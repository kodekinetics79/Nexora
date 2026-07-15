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
    public class StateController : ControllerBase
    {
        private readonly IStateRepository _repository;

        public StateController(IStateRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<StateResponseDTO>>> GetAll([FromQuery] long? buid = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (buid ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var states = await _repository.GetAllAsync(targetBUId);
                return Ok(states);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<StateResponseDTO>> GetById(int id)
        {
            try
            {
                var state = await _repository.GetByIdAsync(id);
                if (state == null) return NotFound();
                return Ok(state);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult<StateResponseDTO>> Create(StateCreateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.Buid = claimBUId;

                if (dto.Buid <= 0) return BadRequest("Business Unit ID is required.");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                var state = await _repository.CreateAsync(dto, userId);
                return CreatedAtAction(nameof(GetById), new { id = state.StateId }, state);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        [HttpPut("{id}")]
        public async Task<ActionResult<StateResponseDTO>> Update(int id, StateUpdateDTO dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) dto.Buid = claimBUId;

                if (dto.Buid <= 0) return BadRequest("Business Unit ID is required.");

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system";
                var state = await _repository.UpdateAsync(id, dto, userId);
                return Ok(state);
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
