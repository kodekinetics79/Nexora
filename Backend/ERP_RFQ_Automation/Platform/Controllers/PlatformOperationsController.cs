using System.Text.Json;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Controllers;

[ApiController]
[Route("api/platform")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class PlatformOperationsController(ErpRfqAutomationContext context) : ControllerBase
{
    [HttpGet("pipeline/queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var jobs = context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking();
        var succeeded = await jobs.CountAsync(j => j.UpdatedOn >= since && j.Status == ExtractionStatus.Succeeded, ct);
        var failed = await jobs.CountAsync(j => j.UpdatedOn >= since &&
            (j.Status == ExtractionStatus.Failed || j.Status == ExtractionStatus.DeadLetter), ct);
        var latencies = await jobs
            .Where(j => j.UpdatedOn >= since && j.Status == ExtractionStatus.Succeeded)
            .Select(j => new { j.CreatedOn, j.UpdatedOn })
            .ToListAsync(ct);

        return Ok(new
        {
            queueDepth = await jobs.CountAsync(j => j.Status == ExtractionStatus.Pending, ct),
            inFlight = await jobs.CountAsync(j => j.Status == ExtractionStatus.Leased ||
                j.Status == ExtractionStatus.Extracting || j.Status == ExtractionStatus.Persisting, ct),
            deadLetter = await jobs.CountAsync(j => j.Status == ExtractionStatus.DeadLetter, ct),
            processedLast24h = succeeded,
            avgLatencyMs = latencies.Count == 0 ? 0 : (long)latencies.Average(j => (j.UpdatedOn - j.CreatedOn).TotalMilliseconds),
            successRate = succeeded + failed == 0 ? 0d : (double)succeeded / (succeeded + failed)
        });
    }

    [HttpGet("pipeline/jobs")]
    public async Task<IActionResult> Jobs(
        [FromQuery] long? tenantId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var tenants = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.PrimaryBusinessUnitId != null)
            .ToDictionaryAsync(t => t.PrimaryBusinessUnitId!.Value, t => new { t.Id, t.Name }, ct);
        var query = context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking().AsQueryable();
        if (tenantId is long platformTenantId)
        {
            if (!tenants.Values.Any(t => t.Id == platformTenantId)) return Ok(Array.Empty<object>());
            var businessUnitId = tenants.Single(t => t.Value.Id == platformTenantId).Key;
            query = query.Where(j => j.BusinessUnitId == businessUnitId);
        }
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = status.ToLowerInvariant() switch
            {
                "queued" => ExtractionStatus.Pending,
                "in_flight" => ExtractionStatus.Extracting,
                "succeeded" => ExtractionStatus.Succeeded,
                "failed" => ExtractionStatus.Failed,
                "dead_letter" => ExtractionStatus.DeadLetter,
                _ => (ExtractionStatus?)null
            };
            if (parsed is null) return BadRequest(new { error = "Unknown extraction status." });
            query = status.Equals("in_flight", StringComparison.OrdinalIgnoreCase)
                ? query.Where(j => j.Status == ExtractionStatus.Leased || j.Status == ExtractionStatus.Extracting || j.Status == ExtractionStatus.Persisting)
                : query.Where(j => j.Status == parsed.Value);
        }

        var rows = await query.OrderByDescending(j => j.UpdatedOn).Take(500).ToListAsync(ct);
        return Ok(rows.Select(job =>
        {
            tenants.TryGetValue(job.BusinessUnitId, out var tenant);
            return new
            {
                id = job.Id.ToString(),
                tenantId = tenant?.Id.ToString() ?? string.Empty,
                tenantName = tenant?.Name ?? $"Business unit {job.BusinessUnitId}",
                documentName = job.FileName ?? "Unnamed document",
                status = MapStatus(job.Status),
                job.Attempts,
                job.MaxAttempts,
                enqueuedAt = job.CreatedOn,
                updatedAt = job.UpdatedOn,
                latencyMs = job.Status == ExtractionStatus.Succeeded
                    ? (long?)(job.UpdatedOn - job.CreatedOn).TotalMilliseconds
                    : null,
                error = job.LastError
            };
        }));
    }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        var plans = await context.Set<Plan>().AsNoTracking().Where(p => p.IsActive)
            .OrderBy(p => p.Weight).ToListAsync(ct);
        return Ok(plans.Select(plan => new
        {
            id = plan.Id.ToString(),
            plan.Name,
            tier = NormalizeTier(plan.Code),
            plan.Weight,
            concurrencyCap = plan.MaxConcurrentExtractionJobs,
            monthlyDocQuota = (int?)plan.MaxDocsPerMonth,
            seatQuota = (int?)plan.MaxSeats,
            priceMonthlyUsd = (decimal?)null,
            entitlements = ReadEnabledFeatures(plan.Features)
        }));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(
        [FromQuery] string? action,
        [FromQuery] long? tenantId,
        [FromQuery] string? result,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        if (string.Equals(result, "failure", StringComparison.OrdinalIgnoreCase))
            return Ok(Array.Empty<object>());

        var query = context.Set<PlatformAuditLog>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (tenantId is long id) query = query.Where(a => a.ActAsTenantId == id);
        var rows = await query.OrderByDescending(a => a.CreatedOn).Take(500).ToListAsync(ct);
        var actorIds = rows.Select(a => a.ActorPlatformUserId).Distinct().ToArray();
        var tenantIds = rows.Where(a => a.ActAsTenantId != null).Select(a => a.ActAsTenantId!.Value).Distinct().ToArray();
        var actors = await context.Set<PlatformUser>().AsNoTracking().Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
        var tenants = await context.Set<Tenant>().AsNoTracking().Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var filtered = string.IsNullOrWhiteSpace(search)
            ? rows
            : rows.Where(row =>
            {
                actors.TryGetValue(row.ActorPlatformUserId, out var actor);
                var tenant = row.ActAsTenantId is long id && tenants.TryGetValue(id, out var value) ? value : null;
                return row.Action.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (row.TargetId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (row.Metadata?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (actor?.Email.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (actor?.DisplayName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (tenant?.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
            });

        return Ok(filtered.Select(row =>
        {
            actors.TryGetValue(row.ActorPlatformUserId, out var actor);
            var tenant = row.ActAsTenantId is long id && tenants.TryGetValue(id, out var value) ? value : null;
            return new
            {
                id = row.Id.ToString(),
                timestamp = row.CreatedOn,
                actor = actor?.DisplayName ?? actor?.Email ?? $"Platform user {row.ActorPlatformUserId}",
                actorEmail = actor?.Email ?? string.Empty,
                row.Action,
                targetType = row.TargetType ?? string.Empty,
                targetId = row.TargetId ?? string.Empty,
                tenantId = row.ActAsTenantId?.ToString(),
                tenantName = tenant?.Name,
                ipAddress = row.Ip ?? string.Empty,
                result = "success",
                detail = row.Metadata
            };
        }));
    }

    private static string MapStatus(ExtractionStatus status) => status switch
    {
        ExtractionStatus.Pending => "queued",
        ExtractionStatus.Leased or ExtractionStatus.Extracting or ExtractionStatus.Persisting => "in_flight",
        ExtractionStatus.Succeeded or ExtractionStatus.Duplicate => "succeeded",
        ExtractionStatus.DeadLetter => "dead_letter",
        _ => "failed"
    };

    private static string NormalizeTier(string? code)
    {
        var value = (code ?? "pro").Trim().ToLowerInvariant();
        return value is "free" or "pro" or "enterprise" ? value : "pro";
    }

    private static string[] ReadEnabledFeatures(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject()
                    .Where(item => item.Value.ValueKind == JsonValueKind.True)
                    .Select(item => item.Name).OrderBy(item => item).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
