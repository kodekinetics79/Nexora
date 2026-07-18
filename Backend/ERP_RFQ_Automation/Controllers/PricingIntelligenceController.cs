using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Intelligence.Pricing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Pricing Intelligence endpoints. The business unit always comes from the JWT
    /// claim (same SEC-07 convention as RfqController.Approve) — never from the client.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/intelligence/rfqs")]
    public class PricingIntelligenceController : ControllerBase
    {
        private readonly IPricingEngine _engine;

        public PricingIntelligenceController(IPricingEngine engine)
        {
            _engine = engine;
        }

        /// <summary>GET /api/intelligence/rfqs/{id}/price-preview</summary>
        [HttpGet("{id}/price-preview")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<PricePreview>> GetPricePreview(long id, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var preview = await _engine.PriceRfqAsync(id, businessUnitId, ct);
                return Ok(preview);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        /// <summary>POST /api/intelligence/rfqs/{id}/apply-pricing</summary>
        [HttpPost("{id}/apply-pricing")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<ApplyPricingResult>> ApplyPricing(
            long id, [FromBody] ApplyPricingRequest request, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            // Audit identity comes from the token, never the body ([JsonIgnore] on AppliedBy).
            request.AppliedBy = User.Identity?.Name ?? "PricingIntelligence";

            try
            {
                var result = await _engine.ApplyPricingAsync(id, businessUnitId, request, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;
    }
}
