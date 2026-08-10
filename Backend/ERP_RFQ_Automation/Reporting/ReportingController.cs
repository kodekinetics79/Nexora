using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Reporting;

public sealed record ReportSubscriptionDTO(
    long Id,
    string ReportKey,
    string ReportTitle,
    string Cadence,
    string Format,
    int HourUtc,
    int DayOfWeek,
    int DayOfMonth,
    int WindowDays,
    string[] Recipients,
    bool IsActive,
    DateTime? NextRunOn,
    DateTime? LastRunOn,
    string? LastRunOutcome,
    string? LastRunDetail);

public sealed record ReportCatalogDTO(string[] ReportKeys, string[] Cadences, string[] Formats,
    int MaximumWindowDays, int MaximumDayOfMonth);

/// <summary>
/// FR-DSH-06 — the tenant's scheduled-report configuration, plus an on-demand download of the same
/// report the schedule would email.
///
/// <para>The download exists so the schedule is verifiable before anyone waits a week for the first
/// delivery, and so the emailed artefact and the on-screen one are provably the same builder.</para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/reporting")]
public sealed class ReportingController : ControllerBase
{
    private readonly IReportSubscriptionService _subscriptions;
    private readonly IReportBuilder _builder;
    private readonly IReportRenderer _renderer;

    public ReportingController(IReportSubscriptionService subscriptions, IReportBuilder builder,
        IReportRenderer renderer)
    {
        _subscriptions = subscriptions;
        _builder = builder;
        _renderer = renderer;
    }

    /// <summary>GET /api/reporting/catalog — the governed keys, so no client hardcodes the list.</summary>
    [HttpGet("catalog")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public ActionResult<ReportCatalogDTO> GetCatalog() => Ok(new ReportCatalogDTO(
        ReportKeys.All, ReportCadences.All, ReportFormats.All,
        ReportSubscriptionService.MaximumWindowDays, ReportSubscriptionService.MaximumDayOfMonth));

    /// <summary>GET /api/reporting/subscriptions — this tenant's schedules and their last outcome.</summary>
    [HttpGet("subscriptions")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public async Task<ActionResult<ReportSubscriptionDTO[]>> List(CancellationToken ct)
    {
        var businessUnitId = BusinessUnitId();
        if (businessUnitId <= 0) return Forbid();

        var rows = await _subscriptions.ListAsync(businessUnitId, ct);
        return Ok(rows.Select(Map).ToArray());
    }

    /// <summary>
    /// PUT /api/reporting/subscriptions — create or update. Manager-gated: a schedule mails tenant
    /// commercial data to addresses of the configurer's choosing, which is a distribution decision,
    /// not a viewing one.
    /// </summary>
    [HttpPut("subscriptions")]
    [RequireManagerRole]
    [RequireModulePermission("Dashboard", PermissionAction.Edit)]
    public async Task<ActionResult<ReportSubscriptionDTO>> Upsert(
        [FromBody] UpsertReportSubscriptionCommand command, CancellationToken ct)
    {
        var businessUnitId = BusinessUnitId();
        if (businessUnitId <= 0) return Forbid();

        try
        {
            var saved = await _subscriptions.UpsertAsync(businessUnitId, Actor(), command, ct);
            return Ok(Map(saved));
        }
        catch (ReportSubscriptionValidationException ex)
        {
            // The message IS the contract and reaches the user verbatim; the client invents no copy.
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("subscriptions/{id:long}")]
    [RequireManagerRole]
    [RequireModulePermission("Dashboard", PermissionAction.Edit)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var businessUnitId = BusinessUnitId();
        if (businessUnitId <= 0) return Forbid();

        return await _subscriptions.DeleteAsync(businessUnitId, id, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    /// GET /api/reporting/render/{reportKey}?format&amp;windowDays — the same document the schedule
    /// would attach, produced now.
    /// </summary>
    [HttpGet("render/{reportKey}")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public async Task<IActionResult> Render(string reportKey, [FromQuery] string format = ReportFormats.Pdf,
        [FromQuery] int windowDays = 30, CancellationToken ct = default)
    {
        var businessUnitId = BusinessUnitId();
        if (businessUnitId <= 0) return Forbid();

        if (!ReportKeys.IsKnown(reportKey))
            return BadRequest(new { message = $"'{reportKey}' is not a report this system produces." });
        if (!ReportFormats.IsKnown(format))
            return BadRequest(new { message = $"'{format}' is not a supported report format." });
        if (windowDays is < 1 or > ReportSubscriptionService.MaximumWindowDays)
            return BadRequest(new
            {
                message = $"The reporting window must be between 1 and {ReportSubscriptionService.MaximumWindowDays} days."
            });

        var now = DateTime.UtcNow;
        var document = await _builder.BuildAsync(businessUnitId, reportKey,
            $"Business unit {businessUnitId}", now.AddDays(-windowDays), now, now, ct);
        var rendered = _renderer.Render(document, format);
        return File(rendered.Content, rendered.ContentType, rendered.FileName);
    }

    private static ReportSubscriptionDTO Map(ReportSubscription s) => new(
        s.Id, s.ReportKey, ReportKeys.Title(s.ReportKey), s.Cadence, s.Format, s.HourUtc,
        s.DayOfWeek, s.DayOfMonth, s.WindowDays, s.RecipientAddresses(), s.IsActive,
        s.NextRunOn, s.LastRunOn, s.LastRunOutcome, s.LastRunDetail);

    private long BusinessUnitId() =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;

    private string Actor() => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                              ?? User.Identity?.Name ?? "unknown";
}
