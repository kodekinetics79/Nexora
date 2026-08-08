using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Billing.Metering;
using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Billing.Controllers;

[ApiController]
[Route("api/platform/billing/usage")]
[Authorize(Policy = PlatformPolicies.Billing)]
public sealed class PlatformUsageMeteringController(UsageMeteringService metering) : ControllerBase
{
    [HttpPost("events")]
    public async Task<IActionResult> Record([FromBody] RecordUsageRequest request, CancellationToken ct)
    {
        try
        {
            var value = await metering.RecordAsync(new RecordUsageEvent(
                request.UsageEventId, request.TenantId, request.EventType, request.Quantity, request.Unit,
                request.OccurredAtUtc, request.SourceRecordType, request.SourceRecordId, request.SourceSystem,
                request.ActorId, request.Provider, request.Model, request.CorrelationId, request.IdempotencyKey,
                request.CostAmount, request.Currency, request.EvidenceSha256, request.AdjustsUsageEventId,
                request.RateCardId, request.RateCardLineId, request.RateCardVersion,
                request.AllowanceApplied, request.UnitPrice), ct);
            return Ok(ToDto(value));
        }
        catch (UsageMeteringException exception) { return Conflict(new { error = exception.Message }); }
    }

    [HttpGet("minutes")]
    public async Task<IActionResult> Minutes([FromQuery] long tenantId, [FromQuery] DateTime fromUtc,
        [FromQuery] DateTime toUtc, CancellationToken ct)
    {
        try { return Ok(await metering.ReadMinutesAsync(tenantId, fromUtc, toUtc, ct)); }
        catch (UsageMeteringException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private static object ToDto(UsageEvent value) => new
    {
        value.UsageEventId, value.TenantId, value.Kind, value.EventType, value.Quantity, value.Unit,
        value.OccurredAtUtc, value.ReceivedAtUtc, value.SourceRecordType, value.SourceRecordId,
        value.SourceSystem, value.ActorId, value.Provider, value.Model, value.CorrelationId,
        value.CostAmount, value.Currency, value.EvidenceSha256, value.RatingStatus,
        value.AdjustsUsageEventId, value.RateCardId, value.RateCardLineId, value.RateCardVersion,
        value.AllowanceApplied, value.OverageQuantity, value.UnitPrice, value.RatedAmount
    };
}

public sealed record RecordUsageRequest(
    Guid UsageEventId,
    [property: Range(1, long.MaxValue)] long TenantId,
    [property: Required, StringLength(64)] string EventType,
    decimal Quantity,
    [property: Required, StringLength(32)] string Unit,
    DateTime OccurredAtUtc,
    [property: Required, StringLength(64)] string SourceRecordType,
    [property: Required, StringLength(128)] string SourceRecordId,
    [property: Required, StringLength(64)] string SourceSystem,
    [property: StringLength(256)] string? ActorId,
    [property: StringLength(128)] string? Provider,
    [property: StringLength(128)] string? Model,
    [property: Required, StringLength(128)] string CorrelationId,
    [property: Required, StringLength(128)] string IdempotencyKey,
    [property: Range(typeof(decimal), "0", "999999999999.999999")] decimal CostAmount,
    [property: Required, StringLength(3, MinimumLength = 3)] string Currency,
    [property: Required, StringLength(64, MinimumLength = 64)] string EvidenceSha256,
    Guid? AdjustsUsageEventId,
    long? RateCardId,
    long? RateCardLineId,
    long? RateCardVersion,
    decimal AllowanceApplied,
    decimal? UnitPrice);
