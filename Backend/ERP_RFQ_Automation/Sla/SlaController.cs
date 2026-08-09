using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
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

        /// <summary>
        /// Legacy wire name for <see cref="QuoteExpiryGraceDays"/>. The Setup UI still
        /// posts it ("Sent quotes past their validity by this many days are closed as
        /// Expired"), which is exactly what the grace allowance now means, so it keeps
        /// working; <see cref="QuoteExpiryGraceDays"/> wins if both are supplied.
        /// </summary>
        public int? QuoteAutoExpireDays { get; set; }

        /// <summary>FR-QTM-07 trigger 1: grace days after the validity date. 0 = expire on the date.</summary>
        public int? QuoteExpiryGraceDays { get; set; }

        /// <summary>FR-QTM-07 trigger 2: days after submission with no customer response.</summary>
        public int? QuoteNoResponseExpiryDays { get; set; }

        public int? ApprovalEscalationHours { get; set; }
        public int? DeadlineBufferHours { get; set; }

        /// <summary>FR-SPO-07: working days before a committed ship date to remind the buyer.</summary>
        public int? SupplierShipDateReminderDays { get; set; }

        /// <summary>FR-SPO-07: working hours without a supplier acknowledgement before escalating.</summary>
        public int? SupplierAckEscalationHours { get; set; }
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
    // Tenant-wide SLA thresholds are manager/admin only (GET stays open to any user).
    [HttpPut("policy")]
    [RequireManagerRole]
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
        // FR-QTM-07: the floor is 0, not 1 — "zero grace" (expire on the validity date)
        // is the requirement's default and was previously not expressible at all.
        if (dto.QuoteAutoExpireDays.HasValue) policy.QuoteExpiryGraceDays = Clamp(dto.QuoteAutoExpireDays.Value, 0, 365);
        if (dto.QuoteExpiryGraceDays.HasValue) policy.QuoteExpiryGraceDays = Clamp(dto.QuoteExpiryGraceDays.Value, 0, 365);
        // Floor of 1: a 0-day no-response window would expire every quote the moment it
        // was sent. Tenants who want the rule off set it to the 365-day ceiling.
        if (dto.QuoteNoResponseExpiryDays.HasValue) policy.QuoteNoResponseExpiryDays = Clamp(dto.QuoteNoResponseExpiryDays.Value, 1, 365);
        if (dto.ApprovalEscalationHours.HasValue) policy.ApprovalEscalationHours = Clamp(dto.ApprovalEscalationHours.Value, 1, 24 * 30);
        if (dto.DeadlineBufferHours.HasValue) policy.DeadlineBufferHours = Clamp(dto.DeadlineBufferHours.Value, 0, 24 * 30);
        // FR-SPO-07. Floor of 0 on the reminder means "the day it ships" is still expressible;
        // the escalation floor is 1 because a 0-hour window would escalate every order the
        // instant it left the building.
        if (dto.SupplierShipDateReminderDays.HasValue)
            policy.SupplierShipDateReminderDays = Clamp(dto.SupplierShipDateReminderDays.Value, 0, 90);
        if (dto.SupplierAckEscalationHours.HasValue)
            policy.SupplierAckEscalationHours = Clamp(dto.SupplierAckEscalationHours.Value, 1, 24 * 30);

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
        quoteAutoExpireDays = p.QuoteExpiryGraceDays,   // legacy name, kept for the existing Setup page
        quoteExpiryGraceDays = p.QuoteExpiryGraceDays,
        quoteNoResponseExpiryDays = p.QuoteNoResponseExpiryDays,
        approvalEscalationHours = p.ApprovalEscalationHours,
        deadlineBufferHours = p.DeadlineBufferHours,
        supplierShipDateReminderDays = p.SupplierShipDateReminderDays,
        supplierAckEscalationHours = p.SupplierAckEscalationHours
    };

    private long? ResolveBusinessUnit()
    {
        var raw = User.FindFirst("businessUnitId")?.Value;
        return long.TryParse(raw, out var bu) && bu > 0 ? bu : null;
    }
}
