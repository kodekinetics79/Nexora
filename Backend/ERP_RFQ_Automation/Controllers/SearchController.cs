using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// FR-DSH-04 — the top-bar quick search.
///
/// <para>Deliberately NOT decorated with <c>[RequireModulePermission]</c>. A single module gate
/// here would be the wrong shape twice over: it would deny the whole search to a caller who may
/// read four of the eight families, and it would grant all eight to a caller who holds one. The
/// permission is evaluated per family inside <see cref="IGlobalSearchService"/>, and the families
/// the caller may not read come back named in <c>deniedEntities</c> rather than silently missing.</para>
/// </summary>
[Route("api/search")]
[ApiController]
[Authorize]
public sealed class SearchController : ControllerBase
{
    private readonly IGlobalSearchService _search;
    private readonly IAccountTeamScopeResolver _accountScope;

    public SearchController(IGlobalSearchService search, IAccountTeamScopeResolver accountScope)
    {
        _search = search;
        _accountScope = accountScope;
    }

    /// <summary>
    /// One term across customers, suppliers, products, leads, RFQs, quotes, orders and shipments,
    /// with the date-range and status filters FR-DSH-04 names.
    /// </summary>
    /// <param name="q">The search term. At least two characters.</param>
    /// <param name="entities">Comma-separated subset; omit for all.</param>
    /// <param name="from">Inclusive lower bound.</param>
    /// <param name="to">EXCLUSIVE upper bound — [from, to), the convention used everywhere else.</param>
    /// <param name="status">Exact status wording (code or display value).</param>
    /// <param name="limit">Rows per family, 1..25.</param>
    [HttpGet]
    public async Task<ActionResult<GlobalSearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] string? entities,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? status,
        [FromQuery] int limit = GlobalSearchService.DefaultPerEntityLimit,
        CancellationToken cancellationToken = default)
    {
        var businessUnitId = ClaimId("businessUnitId");
        var roleId = ClaimId("roleId");
        var userId = ClaimId(ClaimTypes.NameIdentifier);
        if (userId <= 0) userId = ClaimId("sub");
        if (businessUnitId <= 0 || roleId <= 0 || userId <= 0) return Forbid();

        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < GlobalSearchService.MinimumQueryLength)
            return BadRequest($"Enter at least {GlobalSearchService.MinimumQueryLength} characters to search.");

        var normalizedFrom = Normalize(from);
        var normalizedTo = Normalize(to);
        if (normalizedFrom is not null && normalizedTo is not null && normalizedFrom >= normalizedTo)
            return BadRequest("The search 'from' value must be earlier than 'to'.");

        var scope = await _accountScope.ResolveAsync(
            userId, roleId, businessUnitId, DateTime.UtcNow, cancellationToken);

        try
        {
            return Ok(await _search.SearchAsync(new GlobalSearchRequest(
                q,
                entities?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                normalizedFrom,
                normalizedTo,
                status,
                limit,
                businessUnitId,
                userId,
                roleId,
                scope), cancellationToken));
        }
        catch (ArgumentException exception)
        {
            // Surfaced verbatim: the client invents no copy that contradicts the server.
            return BadRequest(exception.Message);
        }
    }

    private long ClaimId(string claimType) =>
        long.TryParse(User.FindFirst(claimType)?.Value, out var id) ? id : 0;

    private static DateTime? Normalize(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
    };
}
