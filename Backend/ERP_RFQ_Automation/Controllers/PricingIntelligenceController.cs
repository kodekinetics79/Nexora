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
        public PricingIntelligenceController(IPricingEngine engine) => _engine = engine;

        /// <summary>GET /api/intelligence/rfqs/{id}/price-preview</summary>
        [HttpGet("{id}/price-preview")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<PricePreview>> GetPricePreview(long id, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid authentication context",
                Detail = "A valid authenticated tenant claim is required."
            });

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

        /// <summary>
        /// POST /api/intelligence/rfqs/{id}/apply-pricing
        ///
        /// V2.3 recommendations are shadow-only. Authoritative pricing uses the
        /// immutable Supplier award to Customer Quote decision endpoint.
        /// </summary>
        [HttpPost("{id}/apply-pricing")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public ActionResult<ApplyPricingResult> ApplyPricing(
            long id, [FromBody] ApplyPricingRequest request, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid authentication context",
                Detail = "A valid authenticated tenant claim is required."
            });

            // V2.3 pricing is deliberately shadow-only. Confirmed prices must be created
            // through the immutable Supplier award -> Customer Quote pricing decision.
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Shadow pricing cannot be applied directly",
                Detail = "Use the governed Supplier award to Customer Quote pricing workflow."
            });

        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;
    }
}
