using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Intelligence.Decision;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Lead Decision Brief endpoints. The business unit always comes from the JWT
    /// claim (same SEC-07 convention as PricingIntelligenceController) — never
    /// from the client.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/intelligence/leads")]
    public class LeadDecisionController : ControllerBase
    {
        /// <summary>Hard cap on ids per batch summaries request.</summary>
        public const int MaxSummaryLeadIds = 100;

        private readonly ILeadDecisionService _service;

        public LeadDecisionController(ILeadDecisionService service)
        {
            _service = service;
        }

        /// <summary>GET /api/intelligence/leads/{id}/decision-brief — full Bid/Review/Skip brief.</summary>
        [HttpGet("{id}/decision-brief")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<LeadDecisionBrief>> GetDecisionBrief(long id, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var brief = await _service.GetBriefAsync(id, businessUnitId, ct);
                return Ok(brief);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        /// <summary>
        /// POST /api/intelligence/leads/decision-summaries — cheap batch cards for
        /// list views. Body: { "leadIds": [1, 2, …] } (max 100). Unknown or
        /// foreign-tenant ids are omitted from the response, never errors.
        /// </summary>
        [HttpPost("decision-summaries")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<DecisionSummariesResponse>> GetDecisionSummaries(
            [FromBody] DecisionSummariesRequest request, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            if (request?.LeadIds is null || request.LeadIds.Count == 0)
                return BadRequest("At least one lead id is required.");
            if (request.LeadIds.Count > MaxSummaryLeadIds)
                return BadRequest($"At most {MaxSummaryLeadIds} lead ids per request.");

            var summaries = await _service.GetSummariesAsync(request.LeadIds, businessUnitId, ct);
            return Ok(new DecisionSummariesResponse { Summaries = summaries });
        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;
    }

    /// <summary>Request body for POST decision-summaries.</summary>
    public sealed class DecisionSummariesRequest
    {
        public List<long> LeadIds { get; set; } = new();
    }

    /// <summary>Response envelope: { summaries: { [leadId]: LeadDecisionSummary } }.</summary>
    public sealed class DecisionSummariesResponse
    {
        public Dictionary<long, LeadDecisionSummary> Summaries { get; set; } = new();
    }
}
