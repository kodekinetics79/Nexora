using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.Metrics;
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
        private readonly IBelowFloorGuard _floorGuard;
        private readonly IMetricRecorder? _metrics;

        // IMetricRecorder is optional (defaults to null) so the app keeps running
        // until the lead splices its registration (Sla/WAVEB-WIRING.md); the
        // pricing_applied metric simply doesn't fire until then.
        public PricingIntelligenceController(
            IPricingEngine engine,
            IBelowFloorGuard floorGuard,
            IMetricRecorder? metrics = null)
        {
            _engine = engine;
            _floorGuard = floorGuard;
            _metrics = metrics;
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

        /// <summary>
        /// POST /api/intelligence/rfqs/{id}/apply-pricing
        ///
        /// WP-B3: if any requested line price is below that line's freshly computed
        /// FloorUnitPrice, nothing is applied — the request is parked as a pending
        /// approval (approve_below_floor_quote) and 409 is returned so the caller
        /// can tell the user it is queued in the Approvals inbox.
        /// </summary>
        [HttpPost("{id}/apply-pricing")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<ApplyPricingResult>> ApplyPricing(
            long id, [FromBody] ApplyPricingRequest request, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            // Audit identity comes from the token, never the body ([JsonIgnore] on AppliedBy).
            request.AppliedBy = User.Identity?.Name ?? ActorEmail() ?? "PricingIntelligence";

            try
            {
                // WP-B3 below-floor gate (floors recomputed via the engine every time).
                var check = await _floorGuard.CheckApplyPricingAsync(id, businessUnitId, request, ct);
                if (check.IsBelowFloor)
                {
                    var approval = await _floorGuard.CreateApplyPricingHoldAsync(
                        id, businessUnitId, check, GetUserId(), request.AppliedBy, ct);

                    return Conflict(new
                    {
                        queuedForApproval = true,
                        approvalId = approval.Id,
                        summary = approval.Summary,
                        message = "Sent for approval — pricing is below your floor. Track it in Approvals.",
                        lines = check.Lines.Select(l => new
                        {
                            rfqItemId = l.RfqItemId,
                            unitPrice = l.UnitPrice,
                            floorUnitPrice = l.FloorUnitPrice,
                            delta = l.Delta
                        })
                    });
                }

                var result = await _engine.ApplyPricingAsync(id, businessUnitId, request, ct);

                // WP-B4 passive metric: per-line recommended vs applied + totals.
                // Reuses the preview the floor check already computed — zero extra queries.
                await RecordPricingAppliedMetricAsync(id, businessUnitId, request, check, result, ct);

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

        private async Task RecordPricingAppliedMetricAsync(
            long rfqId, long businessUnitId, ApplyPricingRequest request,
            BelowFloorCheck check, ApplyPricingResult result, CancellationToken ct)
        {
            if (_metrics is null) return;

            var recommendedByItem = (check.Preview?.Lines ?? new List<PriceLine>())
                .ToDictionary(l => l.RfqItemId, l => l.RecommendedUnitPrice);

            await _metrics.RecordAsync(businessUnitId, MetricEventTypes.PricingApplied, rfqId, new
            {
                rfqId,
                lines = request.Lines.Select(l => new
                {
                    rfqItemId = l.RfqItemId,
                    recommended = recommendedByItem.TryGetValue(l.RfqItemId, out var rec) ? (decimal?)rec : null,
                    applied = l.UnitPrice,
                    delta = recommendedByItem.TryGetValue(l.RfqItemId, out var r2) ? (decimal?)(l.UnitPrice - r2) : null
                }),
                totals = new
                {
                    recommendedTotal = check.Preview?.Totals.RecommendedTotal,
                    appliedTotal = result.Total,
                    appliedLines = result.Applied
                }
            }, ct);
        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;

        private long? GetUserId() =>
            long.TryParse(
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value,
                out var uid) ? uid : null;

        private string? ActorEmail() =>
            User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value;
    }
}
