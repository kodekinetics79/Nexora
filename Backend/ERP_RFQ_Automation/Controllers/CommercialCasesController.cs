using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-cases")]
public sealed class CommercialCasesController : ControllerBase
{
    private readonly ICommercialCaseQueryService _queries;

    public CommercialCasesController(ICommercialCaseQueryService queries) => _queries = queries;

    [HttpGet("search")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<CommercialCaseSearchResult>>> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetBusinessUnitId(out var businessUnitId))
            return BadRequest("Business Unit ID is required.");
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest("Search query must contain at least two characters.");

        return Ok(await _queries.SearchAsync(businessUnitId, q, limit, cancellationToken));
    }

    [HttpGet("{id:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<CommercialCaseDetail>> Get(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetBusinessUnitId(out var businessUnitId))
            return BadRequest("Business Unit ID is required.");

        var result = await _queries.GetAsync(businessUnitId, id, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    private bool TryGetBusinessUnitId(out long businessUnitId)
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId) && businessUnitId > 0;
}
