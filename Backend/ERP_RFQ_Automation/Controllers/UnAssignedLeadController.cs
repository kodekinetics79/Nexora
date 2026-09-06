using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.CommercialRouting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UnAssignedLeadController : ControllerBase
    {
        private readonly ILeadRepository _repository;
        private readonly ICommercialRoutingApplicationService _routing;
        private readonly ICommercialAccessContext _commercialAccess;

        public UnAssignedLeadController(
            ILeadRepository repository,
            ICommercialRoutingApplicationService routing,
            ICommercialAccessContext commercialAccess)
        {
            _repository = repository;
            _routing = routing;
            _commercialAccess = commercialAccess;
        }

        /// <summary>
        /// The caller's own row scope, or null when their identity does not resolve.
        ///
        /// <para>Never taken from a query-string owner id. <c>assignedToId</c> arrives from a
        /// filter control and can only ever NARROW what this scope already permits.</para>
        /// </summary>
        private Task<CommercialActorScope?> ActorAsync() =>
            _commercialAccess.ResolveAsync(HttpContext.RequestAborted);

        [HttpGet]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<AcceptedLeadResponseDTO>>> GetAcceptedLeads(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? assignedToId = null,
            [FromQuery] string? search = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] bool excludeAssigned = false,
            [FromQuery] bool onlyAssigned = false)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest("Business Unit ID is required");

            if (pageNumber < 1)
                return BadRequest("Page number must be greater than or equal to 1.");

            // Relaxed validation: Allow any page size up to 1000
            if (pageSize < 1 || pageSize > 1000)
                return BadRequest("Page size must be between 1 and 1000.");

            var actor = await ActorAsync();
            if (actor == null || actor.BusinessUnitId != targetBUId) return Forbid();

            var (leads, total) = await _repository.GetAcceptedLeadsAsync(
                pageNumber, pageSize, targetBUId, assignedToId, search, startDate, endDate, excludeAssigned, onlyAssigned,
                actor.AccountScope);

            return Ok(new PaginatedResponseDTO<AcceptedLeadResponseDTO>
            {
                Items = leads,
                TotalCount = total,
                PageNumber = pageNumber,
                PageSize = pageSize
            });
        }

        [HttpGet("assigned")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<AcceptedLeadResponseDTO>>> GetAssignedLeads(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? assignedToId = null,
            [FromQuery] string? search = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest("Business Unit ID is required");

            if (pageNumber < 1)
                return BadRequest("Page number must be greater than or equal to 1.");

            // Relaxed validation: Allow any page size up to 1000
            if (pageSize < 1 || pageSize > 1000)
                return BadRequest("Page size must be between 1 and 1000.");

            var actor = await ActorAsync();
            if (actor == null || actor.BusinessUnitId != targetBUId) return Forbid();

            var (leads, total) = await _repository.GetAcceptedLeadsAsync(
                pageNumber, pageSize, targetBUId, assignedToId, search, startDate, endDate, excludeAssigned: false, onlyAssigned: true,
                actor.AccountScope);

            return Ok(new PaginatedResponseDTO<AcceptedLeadResponseDTO>
            {
                Items = leads,
                TotalCount = total,
                PageNumber = pageNumber,
                PageSize = pageSize
            });
        }

        /// <summary>
        /// The assignee dropdown for the three lead/RFQ assignment dialogs.
        ///
        /// <para>Two things were wrong here. The action carried no module permission at all —
        /// alone among its siblings on this controller — so any authenticated user in the tenant
        /// could enumerate the tenant's staff. And the list was the raw set of active users,
        /// while <c>POST assign</c> immediately below refuses anyone without an eligible governed
        /// Sales Rep profile. Every name was offered and most of them answered 409.</para>
        ///
        /// <para>The list is still every active user, deliberately: narrowing it to the eligible
        /// set would show an EMPTY dropdown on a tenant that has not set up any profiles yet,
        /// which explains even less than a 409 does. Instead each name now carries the verdict
        /// and the routing engine's own reason, so the dialog can grey out who cannot be picked
        /// and say why. Users absent from the routing candidate set have no profile row by
        /// definition, which is exactly the reason named for them.</para>
        /// </summary>
        [HttpGet("users-for-assignment")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<UserDropdownDTO>>> GetAssignmentUsers(
            [FromQuery] long? businessUnitId = null,
            CancellationToken ct = default)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest("Business Unit ID is required");

            var users = (await _repository.GetUsersForAssignmentAsync(targetBUId)).ToList();
            var eligibility = (await _routing.GetOwnerOptionsAsync(targetBUId, ct))
                .ToDictionary(option => option.UserId);
            foreach (var user in users)
            {
                if (eligibility.TryGetValue(user.Id, out var option))
                {
                    user.IsEligibleForAssignment = option.IsAvailable;
                    user.EligibilityReason = option.EligibilityReason;
                    user.CapacityPercent = option.CapacityPercent;
                    user.WorkloadPoints = option.Workload.WorkloadPoints;
                }
                else
                {
                    user.IsEligibleForAssignment = false;
                    user.EligibilityReason = RoutingEligibilityReasons.ProfileRequired;
                }
            }

            return Ok(users);
        }

        [HttpPost("assign")]
        [RequireManagerRole]
        [RequireModulePermission("Leads", PermissionAction.Edit)]
        public async Task<ActionResult> AssignLead([FromBody] AssignLeadRequestDTO request)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                var userClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                ?? User.FindFirst("sub")?.Value;
                long? assignedByUserId = long.TryParse(userClaim, out var parsedUserId) ? parsedUserId : null;
                await _routing.AssignLeadAsync(businessUnitId, new ManualAssignLeadCommand(
                    request.LeadId,
                    request.AssignedToUserId,
                    assignedByUserId,
                    request.IdempotencyKey,
                    request.CorrelationId,
                    AssignmentScope.LeadOnly,
                    request.Comment,
                    EnforceExpectedAssignee: true,
                    request.ExpectedAssigneeId),
                    HttpContext.RequestAborted);

                return Ok(new { message = "Lead assigned successfully" });
            }
            catch (RoutingNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (RoutingConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        [HttpGet("{id}")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<AcceptedLeadResponseDTO>> GetAcceptedLeadById(long id)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var lead = await _repository.GetAcceptedLeadByIdAsync(id, businessUnitId);
            if (lead == null)
                return NotFound($"Accepted lead with ID {id} not found");

            // Both halves of the rule CommercialAccessFilters states. While an inquiry is
            // unassigned it belongs to the governed routing queue and has to be readable by
            // whoever might claim it — that is what this screen is for. The moment it has an
            // owner it is that rep's opportunity, and this read answers the way api/leads/{id}
            // does rather than handing over the line items and attachments on the tenant
            // predicate alone. The row is fetched first only to learn whether it has an owner;
            // nothing out of scope is returned.
            if (lead.AssignedToId != null
                && !await _commercialAccess.CanAccessLeadAsync(id, HttpContext.RequestAborted))
                return NotFound($"Accepted lead with ID {id} not found");

            return Ok(lead);
        }
    }
}
