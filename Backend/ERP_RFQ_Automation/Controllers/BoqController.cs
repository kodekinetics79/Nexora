using System.Security.Claims;
using System.Text;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Boq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Service RFQ → BOQ endpoints (Boq/BoqBuilderService). The business unit
    /// always comes from the JWT claim (SEC-07 convention, same as
    /// PricingIntelligenceController) — never from the client. Gated by the
    /// "Quotations" module: a BOQ is priced quote material, so the people
    /// allowed to see/edit quotes are the ones allowed to see/edit BOQs.
    ///
    /// NOTE: requires AddBoqEngine() to be spliced into Program.cs
    /// (see Boq/BOQ-WIRING.md) — until then these endpoints fail DI resolution.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/boq")]
    public class BoqController : ControllerBase
    {
        private readonly IBoqBuilderService _boq;

        public BoqController(IBoqBuilderService boq) => _boq = boq;

        /// <summary>POST /api/boq/draft — {leadId} or {title, text, serviceCategory?} → drafted document (full tree).</summary>
        [HttpPost("draft")]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<ActionResult<BoqDocumentDto>> Draft([FromBody] BoqDraftRequest request, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            request.CreatedBy = User.Identity?.Name ?? ActorEmail() ?? "BoqEngine";

            try
            {
                var dto = await _boq.DraftFromTextAsync(request, businessUnitId, ct);
                return Ok(dto);
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
        }

        /// <summary>GET /api/boq — paged list.</summary>
        [HttpGet]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<BoqListResultDto>> List(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
            [FromQuery] string? status = null, [FromQuery] string? search = null,
            CancellationToken ct = default)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            return Ok(await _boq.ListAsync(businessUnitId, page, pageSize, status, search, ct));
        }

        /// <summary>GET /api/boq/{id} — full tree.</summary>
        [HttpGet("{id:long}")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<BoqDocumentDto>> Get(long id, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            var dto = await _boq.GetAsync(id, businessUnitId, ct);
            return dto is null ? NotFound($"BOQ {id} was not found.") : Ok(dto);
        }

        /// <summary>PUT /api/boq/{id} — header/sections/items upsert (review-workbench style).</summary>
        [HttpPut("{id:long}")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<BoqDocumentDto>> Update(long id, [FromBody] BoqUpdateRequest request, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var dto = await _boq.UpdateAsync(id, businessUnitId, request, ct);
                return dto is null ? NotFound($"BOQ {id} was not found.") : Ok(dto);
            }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        }

        /// <summary>POST /api/boq/{id}/approve — refuses while any line still needs details.</summary>
        [HttpPost("{id:long}/approve")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<BoqDocumentDto>> Approve(long id, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var approvedBy = User.Identity?.Name ?? ActorEmail() ?? "BoqEngine";
                var dto = await _boq.ApproveAsync(id, businessUnitId, approvedBy, ct);
                return dto is null ? NotFound($"BOQ {id} was not found.") : Ok(dto);
            }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        }

        /// <summary>GET /api/boq/assemblies — tenant rate-library (lazily seeds the starter set).</summary>
        [HttpGet("assemblies")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<BoqAssemblyDto>>> Assemblies(CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            return Ok(await _boq.GetAssembliesAsync(businessUnitId, ct));
        }

        /// <summary>POST /api/boq/items/{id}/explode?code=… — replace an item with its assembly components.</summary>
        [HttpPost("items/{id:long}/explode")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<BoqDocumentDto>> Explode(long id, [FromQuery] string? code, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                return Ok(await _boq.ExplodeAssemblyAsync(id, businessUnitId, code, ct));
            }
            catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        }

        /// <summary>GET /api/boq/{id}/export.csv — sections/items/rates/totals; TBD lines marked.</summary>
        [HttpGet("{id:long}/export.csv")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(
            ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Exports)]
        public async Task<IActionResult> ExportCsv(long id, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            var csv = await _boq.ExportCsvAsync(id, businessUnitId, ct);
            if (csv is null) return NotFound($"BOQ {id} was not found.");

            // UTF-8 BOM so Excel opens the file with correct encoding.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            return File(bytes, "text/csv", $"boq-{id}.csv");
        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;

        private string? ActorEmail() =>
            User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value;
    }
}
