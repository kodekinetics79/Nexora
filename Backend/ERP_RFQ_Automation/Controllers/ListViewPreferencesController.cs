using System.Security.Claims;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.ListViews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// AA-01 · per-user list-view column preferences.
///
/// Every route resolves the acting user and business unit from the token, never from the
/// request body or query string: a user must not be able to read or overwrite another
/// user's layout by changing a parameter. There is deliberately no "get another user's
/// preferences" route and no tenant-wide default route.
///
/// No module permission is required beyond being authenticated. Column layout is a
/// personal display preference, not a data grant — the underlying list endpoints still
/// enforce their own module permissions, so a user who cannot see Customers simply gets an
/// empty grid regardless of which columns they have ticked.
/// </summary>
[ApiController]
[Authorize]
[Route("api/list-views")]
public sealed class ListViewPreferencesController : ControllerBase
{
    private readonly IListViewPreferenceService _preferences;

    public ListViewPreferencesController(IListViewPreferenceService preferences) => _preferences = preferences;

    /// <summary>The acting user's resolved column layout for a view.</summary>
    [HttpGet("{viewKey}/columns")]
    public Task<ActionResult<ListViewColumnsResponse>> Get(string viewKey, CancellationToken ct) =>
        Execute(() => _preferences.GetAsync(TenantId(), UserId(), viewKey, ct));

    /// <summary>Replaces the acting user's layout for a view with the ordered set supplied.</summary>
    [HttpPut("{viewKey}/columns")]
    public Task<ActionResult<ListViewColumnsResponse>> Save(
        string viewKey, [FromBody] SaveColumnPreferenceCommand command, CancellationToken ct) =>
        Execute(() => _preferences.SaveAsync(TenantId(), UserId(), viewKey, command, Actor(), ct));

    /// <summary>Drops the acting user's layout and returns the view's declared default.</summary>
    [HttpDelete("{viewKey}/columns")]
    public Task<ActionResult<ListViewColumnsResponse>> Reset(string viewKey, CancellationToken ct) =>
        Execute(() => _preferences.ResetAsync(TenantId(), UserId(), viewKey, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (ListViewNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (CustomFieldDomainException ex) { return BadRequest(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private long TenantId() =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0
            ? id
            : throw new ListViewNotFoundException("Business Unit ID is required.");

    private long UserId() =>
        long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id)
        && id > 0
            ? id
            : throw new ListViewNotFoundException("An authenticated user is required.");

    private string Actor() =>
        User.FindFirst(ClaimTypes.Email)?.Value ??
        User.FindFirst(ClaimTypes.Name)?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        "authenticated-user";
}
