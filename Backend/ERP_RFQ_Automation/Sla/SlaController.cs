using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// HTTP surface for the per-tenant SLA policy. Mirrors the AgentController
/// policy endpoints: [Authorize], BU resolved from the <c>businessUnitId</c> JWT
/// claim, GET returns the stored row or the conservative default, PUT upserts.
/// </summary>
[ApiController]
[Route("api/sla")]
[Authorize]
public sealed class SlaController : ControllerBase
{
    private readonly ErpRfqAutomationContext _db;

    public SlaController(ErpRfqAutomationContext db) => _db = db;

    public sealed class SlaPolicyUpdateDto
    {
        public int? UnassignedHours { get; set; }
        public int? WarnDaysBeforeClose { get; set; }
        public int? CriticalDaysBeforeClose { get; set; }
        public int? StaleQuoteDays { get; set; }
        public int? QuoteAutoExpireDays { get; set; }
        public int? ApprovalEscalationHours { get; set; }
        public int? DeadlineBufferHours { get; set; }
    }

    // -------- GET /api/sla/policy --------
    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var bu = ResolveBusinessUnit();
        if (bu is null) return Unauthorized();

        var policy = await _db.Set<SlaPolicy>().AsNoTracking()
                         .FirstOrDefaultAsync(p => p.BusinessUnitId == bu.Value, ct)
                     ?? SlaPolicy.Default(bu.Value);

        return Ok(ToDto(policy));
    }

    // -------- PUT /api/sla/policy --------
    [HttpPut("policy")]
    public async Task<IActionResult> PutPolicy([FromBody] SlaPolicyUpdateDto dto, CancellationToken ct)
    {
        var bu = ResolveBusinessUnit();
        if (bu is null) return Unauthorized();

        var policy = await _db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.BusinessUnitId == bu.Value, ct);
        var isNew = policy is null;
        if (policy is null)
        {
            policy = SlaPolicy.Default(bu.Value);
            policy.CreatedOn = DateTime.UtcNow;
        }

        // Patch semantics like AgentController: only supplied fields change.
        if (dto.UnassignedHours.HasValue) policy.UnassignedHours = Clamp(dto.UnassignedHours.Value, 0, 24 * 30);
        if (dto.WarnDaysBeforeClose.HasValue) policy.WarnDaysBeforeClose = Clamp(dto.WarnDaysBeforeClose.Value, 0, 90);
        if (dto.CriticalDaysBeforeClose.HasValue) policy.CriticalDaysBeforeClose = Clamp(dto.CriticalDaysBeforeClose.Value, 0, 90);
        if (dto.StaleQuoteDays.HasValue) policy.StaleQuoteDays = Clamp(dto.StaleQuoteDays.Value, 1, 365);
        if (dto.QuoteAutoExpireDays.HasValue) policy.QuoteAutoExpireDays = Clamp(dto.QuoteAutoExpireDays.Value, 1, 365);
        if (dto.ApprovalEscalationHours.HasValue) policy.ApprovalEscalationHours = Clamp(dto.ApprovalEscalationHours.Value, 1, 24 * 30);
        if (dto.DeadlineBufferHours.HasValue) policy.DeadlineBufferHours = Clamp(dto.DeadlineBufferHours.Value, 0, 24 * 30);

        if (policy.CriticalDaysBeforeClose > policy.WarnDaysBeforeClose)
            return BadRequest("The critical alert must be at or after the first warning (critical days <= warn days).");

        policy.UpdatedOn = DateTime.UtcNow;

        if (isNew) _db.Set<SlaPolicy>().Add(policy);
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(policy));
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static object ToDto(SlaPolicy p) => new
    {
        businessUnitId = p.BusinessUnitId,
        unassignedHours = p.UnassignedHours,
        warnDaysBeforeClose = p.WarnDaysBeforeClose,
        criticalDaysBeforeClose = p.CriticalDaysBeforeClose,
        staleQuoteDays = p.StaleQuoteDays,
        quoteAutoExpireDays = p.QuoteAutoExpireDays,
        approvalEscalationHours = p.ApprovalEscalationHours,
        deadlineBufferHours = p.DeadlineBufferHours
    };

    private long? ResolveBusinessUnit()
    {
        var raw = User.FindFirst("businessUnitId")?.Value;
        return long.TryParse(raw, out var bu) && bu > 0 ? bu : null;
    }
}
