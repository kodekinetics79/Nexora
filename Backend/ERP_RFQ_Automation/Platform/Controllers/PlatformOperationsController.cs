using System.Text.Json;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Operations;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Controllers;

[ApiController]
[Route("api/platform")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class PlatformOperationsController(
    ErpRfqAutomationContext context,
    IPlatformAuditService audit,
    IAuthorizationService authorization,
    PlatformDeadLetterRecoveryService? deadLetterRecovery = null) : ControllerBase
{
    [HttpGet("pipeline/queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddHours(-24);
        var jobs = context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking();
        var succeeded = await jobs.CountAsync(j => j.UpdatedOn >= since && j.Status == ExtractionStatus.Succeeded, ct);
        var failed = await jobs.CountAsync(j => j.UpdatedOn >= since &&
            (j.Status == ExtractionStatus.Failed || j.Status == ExtractionStatus.DeadLetter), ct);
        var latencies = await jobs
            .Where(j => j.UpdatedOn >= since && j.Status == ExtractionStatus.Succeeded)
            .Select(j => new { j.CreatedOn, j.UpdatedOn })
            .ToListAsync(ct);
        var invariantBlocked = jobs.Where(j => j.Status == ExtractionStatus.DeadLetter
            && j.LastError != null && j.LastError.StartsWith("[EXTRACTION_INTAKE_"));
        var blockedCount = await invariantBlocked.CountAsync(ct);
        var oldestBlocked = await invariantBlocked.Select(j => (DateTime?)j.CreatedOn).MinAsync(ct);
        var blockedTenants = await invariantBlocked.Select(j => j.BusinessUnitId).Distinct().CountAsync(ct);
        var repeatedInvariantViolations = await invariantBlocked.CountAsync(j => j.Attempts > 0, ct);
        var recent = jobs.Where(j => j.UpdatedOn >= since);
        var recentCount = await recent.CountAsync(ct);
        var retried = await recent.CountAsync(j => j.Attempts > 1, ct);
        var occurrences = context.Set<SourceDocumentOccurrence>().IgnoreQueryFilters().AsNoTracking();
        var invalidClaimCandidates = await jobs.CountAsync(j =>
            (j.Status == ExtractionStatus.Pending
                || ((j.Status == ExtractionStatus.Leased
                        || j.Status == ExtractionStatus.Extracting
                        || j.Status == ExtractionStatus.Persisting)
                    && (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now)))
            && (j.SourceDocumentOccurrenceId == null
                || !occurrences.Any(o => o.BusinessUnitId == j.BusinessUnitId
                    && o.Id == j.SourceDocumentOccurrenceId
                    && o.ExtractionJobId == j.Id
                    && (o.IntakeStatus == IntakeOccurrenceStatus.Queued
                        || o.IntakeStatus == IntakeOccurrenceStatus.Retryable
                        || (o.IntakeStatus == IntakeOccurrenceStatus.Processing
                            && (j.Status == ExtractionStatus.Leased
                                || j.Status == ExtractionStatus.Extracting
                                || j.Status == ExtractionStatus.Persisting)
                            && (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now))))), ct);

        return Ok(new
        {
            queueDepth = await jobs.CountAsync(j => j.Status == ExtractionStatus.Pending, ct),
            inFlight = await jobs.CountAsync(j => j.Status == ExtractionStatus.Leased ||
                j.Status == ExtractionStatus.Extracting || j.Status == ExtractionStatus.Persisting, ct),
            deadLetter = await jobs.CountAsync(j => j.Status == ExtractionStatus.DeadLetter, ct),
            processedLast24h = succeeded,
            // NULL, not 0, when there is nothing to average or divide.
            //
            // A quiet 24 hours used to report "0ms average latency" and "0.0% success" — the
            // two readings an operator would page someone over, produced by a pipeline that had
            // simply had nothing to do. Absence of data is now reported as absence, and the
            // console renders it as an em dash.
            avgLatencyMs = latencies.Count == 0
                ? (long?)null
                : (long)latencies.Average(j => (j.UpdatedOn - j.CreatedOn).TotalMilliseconds),
            successRate = succeeded + failed == 0 ? (double?)null : (double)succeeded / (succeeded + failed),
            successfulClaimRate = succeeded + failed == 0 ? (double?)null : (double)succeeded / (succeeded + failed),
            retryRate = recentCount == 0 ? (double?)null : (double)retried / recentCount,
            invalidClaimCandidates,
            reconciliationRequired = blockedCount,
            oldestBlockedAt = oldestBlocked,
            affectedTenantCount = blockedTenants,
            repeatedInvariantViolations
        });
    }

    [HttpGet("pipeline/jobs")]
    public async Task<IActionResult> Jobs(
        [FromQuery] long? tenantId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        // Group rather than ToDictionary: two tenants pointing at the same primary
        // business unit (data drift / re-provisioning) must not 500 the endpoint.
        // The earliest tenant (lowest id) deterministically represents the unit.
        var tenantRows = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.PrimaryBusinessUnitId != null)
            .Select(t => new { t.Id, t.Name, BusinessUnitId = t.PrimaryBusinessUnitId!.Value })
            .ToListAsync(ct);
        var tenants = tenantRows
            .GroupBy(t => t.BusinessUnitId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Id).First());
        var query = context.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking().AsQueryable();
        if (tenantId is long platformTenantId)
        {
            var match = tenantRows.FirstOrDefault(t => t.Id == platformTenantId);
            if (match is null) return Ok(Array.Empty<object>());
            query = query.Where(j => j.BusinessUnitId == match.BusinessUnitId);
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
        var mayReadCustomerContent = (await authorization.AuthorizeAsync(
            User, PlatformPolicies.TenantAdmin)).Succeeded;
        return Ok(rows.Select(job =>
        {
            tenants.TryGetValue(job.BusinessUnitId, out var tenant);
            return new
            {
                id = job.Id.ToString(),
                tenantId = tenant?.Id.ToString() ?? string.Empty,
                tenantName = tenant?.Name ?? $"Business unit {job.BusinessUnitId}",
                documentName = mayReadCustomerContent
                    ? job.FileName ?? "Unnamed document"
                    : "Restricted document",
                status = MapStatus(job.Status),
                job.Attempts,
                job.MaxAttempts,
                enqueuedAt = job.CreatedOn,
                updatedAt = job.UpdatedOn,
                latencyMs = job.Status == ExtractionStatus.Succeeded
                    ? (long?)(job.UpdatedOn - job.CreatedOn).TotalMilliseconds
                    : null,
                // Exception messages can contain provider diagnostics, document fragments or
                // infrastructure details. Broad fleet roles get a truthful classified state;
                // only the narrower support/Owner policy may read the diagnostic text.
                error = job.LastError is null
                    ? null
                    : mayReadCustomerContent
                        ? job.LastError
                        : "Processing failed; diagnostic details are restricted."
            };
        }));
    }

    [HttpPost("tenants/{tenantId:long}/dead-letters/recover")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> RecoverDeadLetter(
        long tenantId, [FromBody] RecoverPlatformDeadLetterCommand command, CancellationToken ct)
    {
        if (deadLetterRecovery is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Dead-letter recovery is unavailable." });
        try
        {
            return Ok(await deadLetterRecovery.RecoverAsync(tenantId, command, User, HttpContext, ct));
        }
        catch (KeyNotFoundException exception) { return NotFound(new { error = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (DeadLetterDependencyUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = exception.Message });
        }
        catch (InvalidOperationException exception) { return Conflict(new { error = exception.Message }); }
    }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct)
    {
        // Platform console listing: includes INACTIVE plans (isActive flag in the
        // response) so operators can see and reactivate deactivated plans. Consumer
        // paths that assign plans still enforce IsActive themselves
        // (TenantsController.ChangePlan rejects inactive plans).
        var plans = await context.Set<Plan>().AsNoTracking()
            .OrderBy(p => p.Weight).ThenBy(p => p.Id).ToListAsync(ct);
        return Ok(plans.Select(ToPlanResponse));
    }

    // POST /api/platform/plans
    [HttpPost("plans")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<IActionResult> CreatePlan([FromBody] UpsertPlanRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        var validationError = ValidatePlanRequest(request, out var code, out var features);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        if (await context.Set<Plan>().AnyAsync(p => p.Code.ToLower() == code, ct))
            return Conflict(new { error = $"A plan with code '{code}' already exists." });

        // Sec3: the plan row and its audit record commit atomically — a plan can
        // never exist without its "plan.create" trail (and vice versa).
        var plan = new Plan
        {
            Code = code,
            Name = request.Name.Trim(),
            Weight = request.Weight,
            MaxConcurrentExtractionJobs = request.MaxConcurrentExtractionJobs,
            MaxDocsPerMonth = request.MaxDocsPerMonth,
            MaxSeats = request.MaxSeats,
            MonthlyPriceUsd = request.MonthlyPriceUsd,
            Features = features,
            AiPackage = AI.AiPackages.Resolve(request.AiPackage).Key,
            AiMonthlyTokenAllowance = request.AiMonthlyTokenAllowance,
            AiAllowanceUnlimited = request.AiAllowanceUnlimited,
            IsActive = request.IsActive,
            CreatedOn = DateTime.UtcNow
        };
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);

            context.Set<Plan>().Add(plan);
            await context.SaveChangesAsync(ct);

            await audit.WriteAsync(User, "plan.create", nameof(Plan), plan.Id.ToString(),
                new { plan.Code, plan.Name, plan.Weight, plan.MonthlyPriceUsd, plan.IsActive },
                httpContext: HttpContext, ct: ct);

            await tx.CommitAsync(ct);
        });

        return CreatedAtAction(nameof(Plans), new { }, ToPlanResponse(plan));
    }

    // PUT /api/platform/plans/{id}
    [HttpPut("plans/{id:long}")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<IActionResult> UpdatePlan(
        long id, [FromBody] UpsertPlanRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        var validationError = ValidatePlanRequest(request, out var code, out var features);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        var plan = await context.Set<Plan>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null)
            return NotFound();

        if (await context.Set<Plan>().AnyAsync(p => p.Id != id && p.Code.ToLower() == code, ct))
            return Conflict(new { error = $"A plan with code '{code}' already exists." });

        var before = new
        {
            plan.Code, plan.Name, plan.Weight, plan.MaxConcurrentExtractionJobs,
            plan.MaxDocsPerMonth, plan.MaxSeats, plan.MonthlyPriceUsd, plan.IsActive
        };
        plan.Code = code;
        plan.Name = request.Name.Trim();
        plan.Weight = request.Weight;
        plan.MaxConcurrentExtractionJobs = request.MaxConcurrentExtractionJobs;
        plan.MaxDocsPerMonth = request.MaxDocsPerMonth;
        plan.MaxSeats = request.MaxSeats;
        plan.MonthlyPriceUsd = request.MonthlyPriceUsd;
        plan.Features = features;
        plan.AiPackage = AI.AiPackages.Resolve(request.AiPackage).Key;
        plan.AiMonthlyTokenAllowance = request.AiMonthlyTokenAllowance;
        plan.AiAllowanceUnlimited = request.AiAllowanceUnlimited;
        plan.IsActive = request.IsActive;

        var after = new
        {
            plan.Code, plan.Name, plan.Weight, plan.MaxConcurrentExtractionJobs,
            plan.MaxDocsPerMonth, plan.MaxSeats, plan.MonthlyPriceUsd, plan.IsActive
        };

        // Sec3: the plan mutation and its before/after audit record commit atomically.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);

            await context.SaveChangesAsync(ct);

            await audit.WriteAsync(User, "plan.update", nameof(Plan), plan.Id.ToString(),
                new { before, after }, httpContext: HttpContext, ct: ct);

            await tx.CommitAsync(ct);
        });

        return Ok(ToPlanResponse(plan));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(
        [FromQuery] string? action,
        [FromQuery] long? tenantId,
        [FromQuery] string? result,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var query = context.Set<PlatformAuditLog>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (tenantId is long id) query = query.Where(a => a.ActAsTenantId == id);

        // Real result filter over the persisted Result column (no fabrication).
        // Non-canonical values ("all", etc.) mean "no filter", matching the old
        // lenient query surface.
        if (!string.IsNullOrWhiteSpace(result))
        {
            var normalizedResult = result.Trim().ToLowerInvariant();
            if (normalizedResult is PlatformAuditResults.Success or PlatformAuditResults.Failure)
                query = query.Where(a => a.Result == normalizedResult);
        }

        // Search is applied server-side BEFORE Take(500), so a match older than the
        // newest 500 rows is still found. Actor/tenant matches are resolved to id
        // sets first; Metadata is excluded here because it is a jsonb column and
        // has no portable SQL text-search translation.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            var matchingActorIds = await context.Set<PlatformUser>().AsNoTracking()
                .Where(u => u.Email.ToLower().Contains(term) ||
                            (u.DisplayName != null && u.DisplayName.ToLower().Contains(term)))
                .Select(u => u.Id).ToListAsync(ct);
            var matchingTenantIds = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Name.ToLower().Contains(term))
                .Select(t => t.Id).ToListAsync(ct);
            query = query.Where(a =>
                a.Action.ToLower().Contains(term) ||
                (a.TargetType != null && a.TargetType.ToLower().Contains(term)) ||
                (a.TargetId != null && a.TargetId.ToLower().Contains(term)) ||
                matchingActorIds.Contains(a.ActorPlatformUserId) ||
                (a.ActAsTenantId != null && matchingTenantIds.Contains(a.ActAsTenantId.Value)));
        }

        var rows = await query.OrderByDescending(a => a.CreatedOn).Take(500).ToListAsync(ct);
        var actorIds = rows.Select(a => a.ActorPlatformUserId).Distinct().ToArray();
        var tenantIds = rows.Where(a => a.ActAsTenantId != null).Select(a => a.ActAsTenantId!.Value).Distinct().ToArray();
        var actors = await context.Set<PlatformUser>().AsNoTracking().Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);
        var tenants = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var disclosure = await PlatformAuditDisclosure.ResolveAsync(
            authorization, User, rows.Select(r => r.Action), ct);
        var mayReadOperatorPii = (await authorization.AuthorizeAsync(User, PlatformPolicies.Owner)).Succeeded;

        return Ok(rows.Select(row =>
        {
            actors.TryGetValue(row.ActorPlatformUserId, out var actor);
            var tenant = row.ActAsTenantId is long rowTenantId && tenants.TryGetValue(rowTenantId, out var value) ? value : null;
            return new
            {
                id = row.Id.ToString(),
                timestamp = row.CreatedOn,
                actor = actor?.DisplayName ?? actor?.Email ?? (row.ActorPlatformUserId == PlatformAuditService.SystemActorId
                    ? "system"
                    : $"Platform user {row.ActorPlatformUserId}"),
                actorEmail = mayReadOperatorPii ? actor?.Email ?? string.Empty : string.Empty,
                row.Action,
                targetType = row.TargetType ?? string.Empty,
                targetId = row.TargetId ?? string.Empty,
                tenantId = row.ActAsTenantId?.ToString(),
                tenantName = tenant?.Name,
                ipAddress = mayReadOperatorPii ? row.Ip ?? string.Empty : string.Empty,
                result = row.Result,
                detail = disclosure.MayDisclose(row.Action) ? row.Metadata : null,
                metadataDisclosed = disclosure.MayDisclose(row.Action),
                requiredPolicy = AuditDisclosureGate.RequiredPolicyFor(row.Action)
            };
        }));
    }

    /// <summary>
    /// Durable policy decision for historical audit identifiers. Security occurrences stay
    /// append-only; raw operator email/IP is Owner-restricted and action metadata remains behind
    /// its existing least-privilege disclosure gate. Customer erasure must never rewrite evidence
    /// about the operator who performed a privileged action.
    /// </summary>
    [HttpGet("audit/pii-policy")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public IActionResult AuditPiiPolicy() => Ok(new
    {
        policyCode = "RETAIN_RESTRICT_MINIMIZE_V1",
        decision = "retain_restricted_security_evidence",
        historicalRowsRewritten = false,
        directOperatorEmailAndIpPolicy = "owner_only",
        metadataPolicy = "action_specific_least_privilege",
        tenantErasurePolicy = "customer_identity_erasure_does_not_rewrite_operator_security_evidence",
        rationale = "Rewriting append-only audit occurrences would permit breach concealment; disclosure is minimized instead."
    });

    private static string MapStatus(ExtractionStatus status) => status switch
    {
        ExtractionStatus.Pending => "queued",
        ExtractionStatus.Leased or ExtractionStatus.Extracting or ExtractionStatus.Persisting => "in_flight",
        ExtractionStatus.Succeeded or ExtractionStatus.Duplicate => "succeeded",
        ExtractionStatus.DeadLetter => "dead_letter",
        _ => "failed"
    };

    private object ToPlanResponse(Plan plan) => new
    {
        id = plan.Id.ToString(),
        plan.Name,
        code = plan.Code,
        tier = NormalizeTier(plan.Code),
        plan.Weight,
        concurrencyCap = plan.MaxConcurrentExtractionJobs,
        monthlyDocQuota = (int?)plan.MaxDocsPerMonth,
        seatQuota = (int?)plan.MaxSeats,
        priceMonthlyUsd = plan.MonthlyPriceUsd,
        isActive = plan.IsActive,
        entitlements = ReadEnabledFeatures(plan.Features),
        aiPackage = plan.AiPackage,
        aiMonthlyTokenAllowance = plan.AiMonthlyTokenAllowance,
        aiAllowanceUnlimited = plan.AiAllowanceUnlimited,
        // Shipped with the plan rather than duplicated in the console: a package whose meaning is
        // not printed next to it is an opaque label, and the operator ends up guessing what they
        // sold. One source, rendered wherever the package is chosen.
        aiPackageMeans = AI.AiPackages.Resolve(plan.AiPackage).WhatItTurnsOn,
        aiPackageSoldAs = AI.AiPackages.Resolve(plan.AiPackage).SoldAs
    };

    private static string? ValidatePlanRequest(UpsertPlanRequest request, out string code, out string features)
    {
        code = request.Code.Trim().ToLowerInvariant();
        features = string.IsNullOrWhiteSpace(request.Features) ? "{}" : request.Features.Trim();
        if (code.Length == 0 || code.Length > 64)
            return "Plan code must be between 1 and 64 characters.";
        if (string.IsNullOrWhiteSpace(request.Name))
            return "Plan name is required.";
        if (!Entitlements.TypedEntitlementCatalog.TryParse(features, out var entitlementValues, out var entitlementError))
            return entitlementError;
        var unavailableEnabled = entitlementValues
            .Where(item => item.Value && !Entitlements.TypedEntitlementCatalog.IsRuntimeAvailable(item.Key))
            .Select(item => item.Key)
            .Order()
            .ToArray();
        if (unavailableEnabled.Length > 0)
            return $"Unavailable capabilities cannot be enabled on a plan: {string.Join(", ", unavailableEnabled)}.";

        // Stored COMPLETE, with any key the caller omitted written as false. A plan saved with no
        // entitlements at all used to persist as "{}" — valid, default-deny, and indistinguishable
        // from a deliberately empty package — and then failed entitlements.typed-hard-limits on
        // every tenant that ever used it, because that control asks whether every key is PRESENT.
        // Plan "001 / Test" was created that way, and Noor Sons could not be activated: eight of
        // twelve controls green, provisioning succeeded, and the block pointed at a plan nobody
        // had touched since. Completing here costs nothing and cannot enable anything.
        features = Entitlements.TypedEntitlementCatalog.Complete(features);

        if (!AI.AiPackages.IsKnown(request.AiPackage))
            return "AI package must be one of: "
                + string.Join(", ", AI.AiPackages.All.Select(x => x.Key)) + ".";

        if (!string.Equals(request.AiPackage, AI.AiPackages.Off, StringComparison.OrdinalIgnoreCase))
        {
            // An incoherent plan, caught where it is typed rather than where a tenant's documents
            // stop extracting three weeks later. Selling an AI package while withholding the
            // capability that reaches AI at all provisions a tenant whose every control reads open
            // and whose every request is refused by the entitlement gate.
            if (!Entitlements.TypedEntitlementCatalog.IsEnabled(features, "capability.ai"))
                return "A plan selling an AI package must also grant capability.ai — otherwise "
                    + "every AI request from a tenant on this plan is refused by the entitlement gate.";

            // The same rule the tenant's own setup uses: "nobody decided" and "we decided not to
            // cap it" are different answers, and only one of them is a decision.
            if (request.AiAllowanceUnlimited == request.AiMonthlyTokenAllowance.HasValue)
                return "Give this plan a monthly AI token allowance, or mark it deliberately "
                    + "uncapped. Exactly one of the two.";
            if (request.AiMonthlyTokenAllowance is <= 0)
                return "A monthly AI token allowance must be above zero. Zero refuses every "
                    + "document while every other control reads open; to sell no AI, use the Off package.";
        }

        return null;
    }

    /// <summary>
    /// A plan's tier is its own (lowercased) code; an absent/blank code reports
    /// "none". Nothing is ever silently bucketed as "pro".
    /// </summary>
    private static string NormalizeTier(string? code)
        => string.IsNullOrWhiteSpace(code) ? "none" : code.Trim().ToLowerInvariant();

    private static string[] ReadEnabledFeatures(string json)
        => Entitlements.TypedEntitlementCatalog.TryParse(json, out var values, out _)
            ? values.Where(item => item.Value).Select(item => item.Key).Order().ToArray()
            : [];
}
