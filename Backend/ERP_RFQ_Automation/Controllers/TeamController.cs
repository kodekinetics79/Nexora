using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.TeamDTOs;
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
    public class TeamController : ControllerBase
    {
        private readonly ITeamRepository _repository;
        private static readonly int[] AllowedPageSizes = { 5, 10, 25, 50 };

        public TeamController(ITeamRepository repository)
        {
            _repository = repository;
        }

        // GET: api/Team?pageNumber=1&pageSize=10&id=1&teamName=dev&subTeamId=2
        [HttpGet]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<TeamResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? teamName = null,
            [FromQuery] long? subTeamId = null)
        {
            try
            {
                // SEC: claim-only tenant scope — the businessUnitId query value is ignored.
                var targetBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");

                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (teams, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, teamName, subTeamId, targetBUId);

                var teamDTOs = teams.Select(t => new TeamResponseDTO
                {
                    Id = t.Id,
                    TeamName = t.TeamName,
                    SubTeamId = t.SubTeamId,
                    SubTeamName = t.SubTeam != null ? t.SubTeam.TeamName : null,
                    ManagerId = t.ManagerId,
                    BusinessUnitId = t.BusinessUnitId,
                    CreatedBy = t.CreatedBy,
                    CreatedOn = t.CreatedOn,
                    ModifiedBy = t.ModifiedBy,
                    ModifiedOn = t.ModifiedOn
                }).ToList();

                var response = new PaginatedResponseDTO<TeamResponseDTO>
                {
                    Items = teamDTOs,
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

        // GET: api/Team/5
        [HttpGet("{id}")]
        [RequireModulePermission("Users", PermissionAction.View)]
        public async Task<ActionResult<TeamResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                // SEC: claim-only tenant scope — the businessUnitId query value is ignored.
                var targetBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var team = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(team));
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

        // POST: api/Team
        [HttpPost]
        [RequireModulePermission("Users", PermissionAction.Create)]
        public async Task<ActionResult<TeamResponseDTO>> Create([FromBody] TeamCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // SEC: claim-only tenant scope — a client-supplied BusinessUnitId is overwritten.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                request.BusinessUnitId = claimBUId;

                var team = new Team
                {
                    TeamName = request.TeamName,
                    SubTeamId = request.SubTeamId,
                    ManagerId = request.ManagerId,
                    BusinessUnitId = request.BusinessUnitId,
                    CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(team);

                var response = MapToResponse(team);
                return CreatedAtAction(nameof(GetById), new { id = team.Id, businessUnitId = team.BusinessUnitId }, response);
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

        // PUT: api/Team/5
        [HttpPut("{id}")]
        [RequireModulePermission("Users", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] TeamUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // SEC: claim-only tenant scope — a client-supplied BusinessUnitId is overwritten.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return Forbid();
                request.BusinessUnitId = claimBUId;

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitId);

                existing.TeamName = request.TeamName;
                existing.SubTeamId = request.SubTeamId;
                existing.ManagerId = request.ManagerId;
                existing.BusinessUnitId = request.BusinessUnitId;
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

        // DELETE: api/Team/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Users", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                // SEC: claim-only tenant scope — the businessUnitId query value is ignored.
                var targetBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
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

        private TeamResponseDTO MapToResponse(Team team)
        {
            return new TeamResponseDTO
            {
                Id = team.Id,
                TeamName = team.TeamName,
                SubTeamId = team.SubTeamId,
                SubTeamName = team.SubTeam != null ? team.SubTeam.TeamName : null,
                ManagerId = team.ManagerId,
                BusinessUnitId = team.BusinessUnitId,
                CreatedBy = team.CreatedBy,
                CreatedOn = team.CreatedOn,
                ModifiedBy = team.ModifiedBy,
                ModifiedOn = team.ModifiedOn
            };
        }
    }
}