using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Billing.Metering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Controllers;

/// <summary>
/// Platform-plane SaaS billing: live usage meters, rate cards, statement
/// compute/finalize, cost/margin, and the commercial terms that decide what a tenant is
/// charged. Every endpoint requires the <see cref="PlatformPolicies.Billing"/> policy
/// (Owner | BillingAdmin) via the class-level default-deny attribute; mutations are
/// audited through <see cref="IPlatformAuditService"/>.
///
/// <para><b>Sec9 separation of duties.</b> Everything that changes what a customer pays
/// lives behind this controller's Billing policy and NOT behind
/// <c>PlatformPolicies.TenantAdmin</c> (Owner | SupportAdmin). A support administrator can
/// suspend, resume and archive a tenant — operational acts — but must never be able to
/// move it onto a cheaper rate card, extend a trial, or convert it to an unbilled mode.
/// The one commercial operation that lives on <c>TenantsController</c>, plan assignment,
/// carries this same Billing policy explicitly for the same reason.</para>
/// </summary>
[ApiController]
[Route("api/platform/billing")]
[Authorize(Policy = PlatformPolicies.Billing)]
public class PlatformBillingController : ControllerBase
{
    /// <summary>
    /// Mirrors the floor <c>TenantsController</c> applies at provisioning. It is a length
    /// rule and not a presence rule because an exemption justified as "x" passes a
    /// required-field check while leaving a paper trail worth nothing, and the point of
    /// demanding a reason is that somebody has to read it later and understand why this
    /// customer was not charged. Duplicated rather than shared because provisioning and
    /// billing administration are separate controllers with separate owners; the two must
    /// agree, and this test suite pins the value so a drift is caught.
    /// </summary>
    internal const int MinimumBillingModeReasonLength = 15;

    private readonly ErpRfqAutomationContext _context;
    private readonly IBillingStatementService _billing;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<PlatformBillingController> _logger;
    private readonly ITenantAccessService? _tenantAccess;
    private readonly UsageBillingReadinessService _billingReadiness;

    public PlatformBillingController(
        ErpRfqAutomationContext context,
        IBillingStatementService billing,
        IPlatformAuditService audit,
        ILogger<PlatformBillingController> logger,
        ITenantAccessService? tenantAccess = null)
    {
        _context = context;
        _billing = billing;
        _audit = audit;
        _logger = logger;
        _tenantAccess = tenantAccess;
        _billingReadiness = new UsageBillingReadinessService(context);
    }

    // GET /api/platform/billing/usage/{tenantId}?period=YYYY-MM
    [HttpGet("usage/{tenantId:long}")]
    public async Task<ActionResult<TenantUsageReadout>> GetUsage(
        long tenantId, [FromQuery] string? period, CancellationToken ct)
    {
        if (!BillingPeriod.TryParse(period, out var billingPeriod))
            return BadRequest(new { error = "Query parameter 'period' is required in YYYY-MM format." });

        try
        {
            return Ok(await _billing.GetUsageAsync(tenantId, billingPeriod, ct));
        }
        catch (BillingNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // GET /api/platform/billing/readiness/{tenantId}?period=YYYY-MM
    [HttpGet("readiness/{tenantId:long}")]
    public async Task<ActionResult<BillingReadinessResult>> GetBillingReadiness(
        long tenantId, [FromQuery] string? period, CancellationToken ct)
    {
        if (!BillingPeriod.TryParse(period, out var billingPeriod))
            return BadRequest(new { error = "Query parameter 'period' is required in YYYY-MM format." });
        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { error = $"Tenant {tenantId} does not exist." });
        var meterKeys = tenant.RateCardId is long rateCardId
            ? await _context.Set<RateCardLine>().AsNoTracking().Where(x => x.RateCardId == rateCardId)
                .Select(x => x.MeterKey).ToListAsync(ct)
            : [];
        meterKeys.Add(BillingMeterKeys.BaseSubscription);
        var usage = await _billing.GetUsageAsync(tenantId, billingPeriod, ct);
        var resolved = await _billingReadiness.ResolveAsync(tenantId, billingPeriod, usage.Meters, meterKeys, ct);
        return Ok(resolved.Readiness);
    }

    [HttpGet("meter-catalog")]
    public ActionResult<object> GetMeterCatalog() => Ok(UsageMeterCatalog.Definitions
        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
        {
            eventType = x.Key,
            x.Value.BillingMeterKey,
            x.Value.Unit,
            certification = x.Value.Certification.ToString()
        }));

    [HttpPost("usage-events/{usageEventId:guid}/rating-corrections")]
    [Authorize(Policy = PlatformPolicies.Billing)]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> CorrectUsageRating(Guid usageEventId,
        [FromBody] CorrectUsageRatingRequest request, CancellationToken ct)
    {
        try
        {
            var rating = await _billingReadiness.CorrectRatingAsync(
                new CorrectUsageRatingCommand(usageEventId, request.IdempotencyKey, request.Reason), Actor(),
                (value, innerCt) => _audit.WriteAsync(User, "billing.usage.rating.correct",
                    nameof(UsageEventRating), value.Id.ToString(),
                    new { value.TenantId, value.UsageEventId, value.AttemptNumber, value.Status,
                        value.ReasonCode, correctionReason = request.Reason.Trim(), value.RateCardId,
                        value.RateCardLineId, value.RateCardVersion },
                    actAsTenantId: value.TenantId, httpContext: HttpContext, ct: innerCt), ct);
            return Ok(new { rating.Id, rating.TenantId, rating.UsageEventId, rating.AttemptNumber,
                status = rating.Status.ToString(), rating.ReasonCode, rating.RateCardId,
                rating.RateCardLineId, rating.RateCardVersion, rating.Currency, rating.AllowanceApplied,
                rating.OverageQuantity, rating.UnitPrice, rating.RatedAmount, rating.RatedAtUtc, rating.RatedBy });
        }
        catch (UsageMeteringException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("tenants/{tenantId:long}/document-coverage/proposals")]
    [Authorize(Policy = PlatformPolicies.Billing)]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> ProposeDocumentCoverage(long tenantId,
        [FromBody] DocumentCoverageProposalRequest request, CancellationToken ct)
    {
        if (!BillingPeriod.TryParse(request.Period, out var period))
            return BadRequest(new { error = "period must use YYYY-MM." });
        try
        {
            var policy = await _billingReadiness.ProposeDocumentCoverageAsync(
                new ProposeDocumentCoverageCommand(tenantId, period, request.MidPeriodCutoverUtc, request.Reason), Actor(),
                (value, innerCt) => _audit.WriteAsync(User, "billing.coverage.propose",
                    nameof(TenantMeterSourcePolicy), $"{value.TenantId}:{value.MeterKey}",
                    new { value.TenantId, value.MeterKey, value.Mode, value.ProposedEffectiveAtUtc, value.Version },
                    actAsTenantId: value.TenantId, httpContext: HttpContext, ct: innerCt), ct);
            return Ok(new { policy.TenantId, policy.MeterKey, mode = policy.Mode.ToString(),
                policy.ProposedEffectiveAtUtc, policy.ProposedBy, policy.ProposedAtUtc, policy.Version });
        }
        catch (UsageMeteringException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("tenants/{tenantId:long}/document-coverage/approve")]
    [Authorize(Policy = PlatformPolicies.Billing)]
    [Authorize(Policy = PlatformPolicies.Owner)]
    [Authorize(Policy = PlatformPolicies.Mfa)]
    public async Task<IActionResult> ApproveDocumentCoverage(long tenantId,
        [FromBody] DocumentCoverageApprovalRequest request, CancellationToken ct)
    {
        if (!BillingPeriod.TryParse(request.Period, out var period))
            return BadRequest(new { error = "period must use YYYY-MM." });
        try
        {
            var segments = await _billingReadiness.ApproveDocumentCoverageAsync(tenantId, period, Actor(), request.Reason,
                (values, innerCt) => _audit.WriteAsync(User, "billing.coverage.approve",
                    nameof(UsageCoverageSegment), $"{tenantId}:{period.Key}",
                    new { tenantId, period = period.Key, segmentIds = values.Select(x => x.Id),
                        quantity = values.Sum(x => x.QuantityTotal), amount = values.Sum(x => x.RatedAmountTotal) },
                    actAsTenantId: tenantId, httpContext: HttpContext, ct: innerCt), ct);
            return Ok(segments.Select(x => new { x.Id, x.TenantId, x.MeterKey, x.StartUtc, x.EndUtc,
                source = x.AuthoritativeSource.ToString(), completeness = x.Completeness.ToString(),
                x.EventCount, x.QuantityTotal, x.AllowanceAppliedTotal, x.OverageQuantityTotal,
                x.RatedAmountTotal, x.Currency, reconciliation = x.ReconciliationStatus.ToString(),
                x.EvidenceSha256, x.RateLineageSha256 }));
        }
        catch (UsageMeteringException ex) { return Conflict(new { error = ex.Message }); }
    }

    // GET /api/platform/billing/rate-cards
    [HttpGet("rate-cards")]
    public async Task<ActionResult<IEnumerable<RateCardDto>>> ListRateCards(CancellationToken ct)
    {
        var cards = await _context.Set<RateCard>().AsNoTracking()
            .Include(c => c.Lines)
            .OrderByDescending(c => c.EffectiveFromUtc).ThenByDescending(c => c.Id)
            .ToListAsync(ct);
        return Ok(cards.Select(ToDto));
    }

    // POST /api/platform/billing/rate-cards
    [HttpPost("rate-cards")]
    public async Task<ActionResult<RateCardDto>> CreateRateCard(
        [FromBody] CreateRateCardRequest request, CancellationToken ct)
    {
        var validationError = ValidateRateCardShape(request.Currency, request.Lines);
        if (validationError is not null)
            return BadRequest(new { error = validationError });
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Trim().Length > 64)
            return BadRequest(new { error = "Code is required (max 64 characters)." });

        var code = request.Code.Trim();
        if (await _context.Set<RateCard>().AnyAsync(c => c.Code == code, ct))
            return Conflict(new { error = $"A rate card with code '{code}' already exists." });

        var card = new RateCard
        {
            Code = code,
            Currency = request.Currency.Trim().ToUpperInvariant(),
            EffectiveFromUtc = request.EffectiveFromUtc,
            EffectiveToUtc = request.EffectiveToUtc,
            IsActive = request.IsActive,
            CreatedBy = Actor(),
            CreatedOn = DateTime.UtcNow,
            Lines = request.Lines.Select(ToEntity).ToList()
        };

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);
                _context.Set<RateCard>().Add(card);
                await _context.SaveChangesAsync(ct);
                await _audit.WriteAsync(User, "billing.ratecard.create", nameof(RateCard), card.Id.ToString(),
                    new { card.Code, card.Currency, Lines = card.Lines.Count },
                    httpContext: HttpContext, ct: ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception)
        {
            _logger.LogError("Failed to create rate card {Code}", code);
            return StatusCode(500, new { error = "Rate card creation failed." });
        }

        return CreatedAtAction(nameof(ListRateCards), null, ToDto(card));
    }

    // PUT /api/platform/billing/rate-cards/{id} — allowed only while NO Final statement pins this card.
    [HttpPut("rate-cards/{id:long}")]
    public async Task<ActionResult<RateCardDto>> UpdateRateCard(
        long id, [FromBody] UpdateRateCardRequest request, CancellationToken ct)
    {
        var validationError = ValidateRateCardShape(request.Currency, request.Lines);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        // P1-B4: the Final-reference check and the update run inside ONE
        // serializable transaction (via the execution strategy), so a finalize
        // that pins this card cannot slip between check and write — the check
        // either sees the Final statement (409) or the finalize serializes after
        // the update.
        RateCard? card = null;
        var notFound = false;
        var finalized = false;

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, ct);

                card = await _context.Set<RateCard>().Include(c => c.Lines)
                    .FirstOrDefaultAsync(c => c.Id == id, ct);
                if (card is null)
                {
                    notFound = true;
                    return;
                }

                finalized = await _context.Set<BillingStatement>()
                    .AnyAsync(s => s.RateCardId == id && s.Status == BillingStatementStatus.Final, ct);
                if (finalized)
                    return;

                card.Currency = request.Currency.Trim().ToUpperInvariant();
                card.EffectiveFromUtc = request.EffectiveFromUtc;
                card.EffectiveToUtc = request.EffectiveToUtc;
                card.IsActive = request.IsActive;
                _context.Set<RateCardLine>().RemoveRange(card.Lines);
                card.Lines.Clear();
                foreach (var line in request.Lines.Select(ToEntity))
                    card.Lines.Add(line);
                card.Version++;
                await _context.SaveChangesAsync(ct);
                await _audit.WriteAsync(User, "billing.ratecard.update", nameof(RateCard), card.Id.ToString(),
                    new { card.Code, card.Currency, Lines = card.Lines.Count, card.IsActive },
                    httpContext: HttpContext, ct: ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception)
        {
            _logger.LogError("Failed to update rate card {Id}", id);
            return StatusCode(500, new { error = "Rate card update failed." });
        }

        if (notFound)
            return NotFound(new { error = $"Rate card {id} does not exist." });
        if (finalized)
            return Conflict(new
            {
                error = $"Rate card {id} is referenced by a Final billing statement and is immutable; create a new rate card instead."
            });

        return Ok(ToDto(card!));
    }

    // GET /api/platform/billing/statements?tenantId=&status=
    [HttpGet("statements")]
    public async Task<ActionResult<IEnumerable<BillingStatementDto>>> ListStatements(
        [FromQuery] long? tenantId, [FromQuery] string? status, CancellationToken ct)
    {
        var query = _context.Set<BillingStatement>().AsNoTracking().Include(s => s.Lines).AsQueryable();
        if (tenantId is long tid)
            query = query.Where(s => s.TenantId == tid);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<BillingStatementStatus>(status, true, out var parsed))
                return BadRequest(new { error = $"Unknown statement status '{status}'." });
            query = query.Where(s => s.Status == parsed);
        }

        var statements = await query
            .OrderByDescending(s => s.PeriodStartUtc).ThenBy(s => s.TenantId)
            .ToListAsync(ct);
        return Ok(statements.Select(ToDto));
    }

    // POST /api/platform/billing/statements/compute  { tenantId, period, rateCardId? }
    [HttpPost("statements/compute")]
    public async Task<ActionResult<BillingStatementDto>> ComputeStatement(
        [FromBody] ComputeStatementRequest request, CancellationToken ct)
    {
        if (!BillingPeriod.TryParse(request.Period, out var period))
            return BadRequest(new { error = "'period' is required in YYYY-MM format." });

        // Supplying a card is a one-run pricing override for an unpinned tenant.
        // BillingAdmin may calculate the ordinary server-selected price, but only
        // Owner may choose a different commercial input for that calculation.
        if (request.RateCardId is not null && !HoldsOwnerAuthority())
            return Forbid();

        BillingStatement statement;
        try
        {
            statement = await _billing.ComputeStatementAsync(
                request.TenantId, period, request.RateCardId, ct, Actor());
        }
        catch (BillingNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (BillingConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        // Sec3 (documented exception): the COMPUTE audit deliberately stays
        // post-commit. Compute is idempotent (recompute of a Draft is a no-op for
        // identical inputs; a Final statement is returned unchanged), so if this
        // audit write fails the caller retries and re-emits it without risking a
        // duplicate charge. The FINALIZE audit, by contrast, runs inside the
        // finalize transaction (see FinalizeStatement below).
        await _audit.WriteAsync(User, "billing.statement.compute", nameof(BillingStatement),
            statement.Id.ToString(),
            new { request.TenantId, period.Key, statement.TotalAmount, Status = statement.Status.ToString() },
            actAsTenantId: request.TenantId, httpContext: HttpContext, ct: ct);

        return Ok(ToDto(statement));
    }

    // GET /api/platform/billing/statements/{id}
    /// <summary>
    /// One statement with every line, for the review an operator does BEFORE finalizing.
    /// Finalizing is permanent — the unique index blocks a second statement for the period
    /// and the database trigger blocks correcting this one — so the console needs a way to
    /// read exactly what it is about to freeze, including the revenue-risk and exemption
    /// marker lines that explain a total of zero.
    /// </summary>
    [HttpGet("statements/{id:long}")]
    public async Task<ActionResult<BillingStatementDto>> GetStatement(long id, CancellationToken ct)
    {
        var statement = await _context.Set<BillingStatement>().AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        return statement is null
            ? NotFound(new { error = $"Billing statement {id} does not exist." })
            : Ok(ToDto(statement));
    }

    // POST /api/platform/billing/statements/{id}/finalize
    [HttpPost("statements/{id:long}/finalize")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<BillingStatementDto>> FinalizeStatement(long id, CancellationToken ct)
    {
        BillingStatement statement;
        try
        {
            // Sec3: the finalize audit is written INSIDE the finalize transaction
            // (callback runs after the Draft→Final save, before commit) so the
            // status flip and its audit row are atomic: audit failure rolls the
            // finalize back. Idempotent re-finalize of an already-Final statement
            // does not emit a second audit row.
            statement = await _billing.FinalizeAsync(id, Actor(),
                onFinalized: (s, innerCt) => _audit.WriteAsync(User, "billing.statement.finalize",
                    nameof(BillingStatement), s.Id.ToString(),
                    new { s.TenantId, s.PeriodStartUtc, s.TotalAmount },
                    actAsTenantId: s.TenantId, httpContext: HttpContext, ct: innerCt),
                ct: ct);
        }
        catch (BillingNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (BillingConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        return Ok(ToDto(statement));
    }

    // GET /api/platform/billing/cost/{tenantId}?period=YYYY-MM
    [HttpGet("cost/{tenantId:long}")]
    public async Task<ActionResult<TenantCostReport>> GetCost(
        long tenantId, [FromQuery] string? period, CancellationToken ct)
    {
        if (!BillingPeriod.TryParse(period, out var billingPeriod))
            return BadRequest(new { error = "Query parameter 'period' is required in YYYY-MM format." });

        try
        {
            return Ok(await _billing.GetCostAsync(tenantId, billingPeriod, ct));
        }
        catch (BillingNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // GET /api/platform/billing/revenue-risk?onlyAtRisk=&includeArchived=
    /// <summary>
    /// The fleet's revenue posture, one row per tenant: billing mode and its recorded
    /// reason, plan, pinned rate card, the last statement and whether it actually charged
    /// anything, trial state, and every machine-readable leak reason.
    ///
    /// <para>Read-only and therefore unaudited, unlike the mutations on this controller.
    /// It exists because "which of our customers is getting this for free?" had no answer
    /// short of opening each tenant in turn, and a question that expensive to ask is a
    /// question nobody asks.</para>
    /// </summary>
    [HttpGet("revenue-risk")]
    public async Task<ActionResult<RevenueRiskReportDto>> GetRevenueRisk(
        [FromQuery] bool onlyAtRisk, [FromQuery] bool includeArchived,
        [FromQuery] bool onlyCommercialConfigurationRequired, CancellationToken ct)
    {
        var tenants = await _billing.GetRevenueRiskAsync(includeArchived, ct);

        var filtered = tenants;
        if (onlyCommercialConfigurationRequired)
            filtered = filtered.Where(t => t.CommercialConfigurationRequired).ToList();
        else if (onlyAtRisk)
            filtered = filtered.Where(t => t.AtRisk).ToList();

        // The counts are computed over the WHOLE population before any filtering, so
        // "3 of 40 tenants at risk" stays true even when the caller asked to see only the 3.
        return Ok(new RevenueRiskReportDto(
            DateTime.UtcNow,
            tenants.Count,
            tenants.Count(t => t.AtRisk),
            tenants.Count(t => t.TrialExpired),
            tenants.Count(t => t.BillingMode == nameof(TenantBillingMode.Billable) && !t.LastStatementCharged),
            filtered)
        {
            CommercialConfigurationRequiredCount = tenants.Count(t => t.CommercialConfigurationRequired)
        });
    }

    // GET /api/platform/billing/tenants/{tenantId}
    /// <summary>
    /// One tenant's whole commercial picture: how it is charged, against which plan and
    /// pinned rate card, its trial and billing-start dates, its commercial-configuration
    /// state, and its statement history. This is the tenant billing tab — assembled server
    /// side so the console renders one call instead of stitching four together and
    /// disagreeing with the revenue board about what "charged" means.
    /// </summary>
    [HttpGet("tenants/{tenantId:long}")]
    public async Task<ActionResult<TenantBillingProfileDto>> GetTenantBillingProfile(
        long tenantId, CancellationToken ct)
    {
        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan)
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return NotFound(new { error = $"Tenant {tenantId} does not exist." });

        RateCard? pinned = null;
        if (tenant.RateCardId is long pinnedId)
            pinned = await _context.Set<RateCard>().AsNoTracking().Include(c => c.Lines)
                .FirstOrDefaultAsync(c => c.Id == pinnedId, ct);

        var statements = await _context.Set<BillingStatement>().AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.PeriodStartUtc)
            .Take(24)
            .ToListAsync(ct);

        var latest = statements.FirstOrDefault();
        var risk = RevenueLeakEvaluator.Describe(tenant, tenant.Plan, pinned, latest, DateTime.UtcNow);

        return Ok(new TenantBillingProfileDto(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Status.ToString(),
            tenant.BillingMode.ToString(),
            tenant.BillingModeReason,
            tenant.PlanId,
            tenant.Plan?.Code,
            tenant.Plan?.Name,
            tenant.Plan?.MonthlyPriceUsd,
            tenant.RateCardId,
            pinned?.Code,
            // A pinned card that no longer exists refuses to compute rather than repricing
            // the tenant onto the active card, so the console has to be able to SEE the
            // dangling pin — otherwise the only symptom is statements that stop appearing.
            tenant.RateCardId is not null && pinned is null,
            tenant.BillingStartsOn,
            tenant.TrialEndsOn,
            tenant.ContractStartOn,
            tenant.ContractEndOn,
            tenant.PaymentTermsDays,
            tenant.PurchaseOrderReference,
            tenant.BillingContactName,
            tenant.BillingContactEmail,
            tenant.BillingAddress,
            tenant.AccountOwnerEmail,
            risk,
            statements.Select(s => new BillingStatementSummaryDto(
                s.Id,
                s.PeriodStartUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                s.PeriodStartUtc, s.PeriodEndUtc, s.RateCardId, s.Currency,
                s.Status.ToString(), s.TotalAmount, s.ComputedAtUtc, s.ComputedBy,
                s.FinalizedAtUtc, s.FinalizedBy))
                .ToList()));
    }

    // PUT /api/platform/billing/tenants/{tenantId}/rate-card   { rateCardId, reason }
    /// <summary>
    /// Pins the price list this tenant's statements are computed against, or clears the pin.
    ///
    /// <para>Pinning is what stops a customer on negotiated terms being silently repriced the
    /// day somebody activates a new card, so CLEARING one is treated as the significant act it
    /// is: a null <c>rateCardId</c> is accepted, but it drops the tenant back onto
    /// "whichever card is active", and the reason is mandatory in that direction.</para>
    /// </summary>
    [HttpPut("tenants/{tenantId:long}/rate-card")]
    public async Task<ActionResult<TenantBillingProfileDto>> SetTenantRateCard(
        long tenantId, [FromBody] SetTenantRateCardRequest request, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (request.RateCardId is null && string.IsNullOrWhiteSpace(reason))
            return BadRequest(new
            {
                error = "Clearing a tenant's pinned rate card returns it to whichever card happens to be active for " +
                        "the period, which is how a negotiated customer gets silently repriced. A reason is required."
            });

        RateCard? card = null;
        if (request.RateCardId is long rateCardId)
        {
            card = await _context.Set<RateCard>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == rateCardId, ct);
            if (card is null)
                return BadRequest(new { error = $"Rate card {rateCardId} does not exist." });
            // Caught here rather than at compute: a non-USD pin produces a tenant whose
            // statements 409 every month until somebody reads the billing run's logs.
            if (!string.Equals(card.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new
                {
                    error = $"Rate card {card.Id} ('{card.Code}') is denominated in '{card.Currency}'; v1 billing is " +
                            "USD-only and a tenant pinned to it could never produce a statement."
                });
            var now = DateTime.UtcNow;
            if (!card.IsActive || card.EffectiveFromUtc > now || card.EffectiveToUtc is { } end && end <= now)
                return BadRequest(new
                {
                    error = $"Rate card {card.Id} ('{card.Code}') is not active and effective now. " +
                            "Activate the governed card before assigning it; future commercial changes require a scheduled approval workflow."
                });
        }

        var updated = await MutateTenantAsync(tenantId, "billing.tenant.rate-card", ct, tenant =>
        {
            var previous = tenant.RateCardId;
            tenant.RateCardId = request.RateCardId;
            return new { fromRateCardId = previous, toRateCardId = request.RateCardId, rateCardCode = card?.Code, reason };
        });

        return updated ?? await GetTenantBillingProfile(tenantId, ct);
    }

    // PUT /api/platform/billing/tenants/{tenantId}/commercial-terms
    /// <summary>
    /// Sets how and from when a tenant is charged: billing mode, the written reason behind any
    /// exemption, the trial end date and the billing start date.
    ///
    /// <para>This is the remediation lever for the two states the revenue board reports as
    /// "Commercial Configuration Required": it is how an operator records the reason behind a
    /// legacy <c>Internal</c>/<c>Partner</c> tenant, and how an expired trial is converted to
    /// <c>Billable</c>. It deliberately does NOT assign the plan — plan assignment is
    /// <c>PUT /api/platform/tenants/{id}/plan</c>, which carries the same Billing policy — so
    /// there is exactly one endpoint that decides what a tenant's plan is.</para>
    ///
    /// <para>The validation mirrors provisioning exactly. A tenant that could not have been
    /// CREATED in a given commercial shape must not be able to reach that shape by being edited
    /// into it afterwards, which is how the rules that provisioning enforces get quietly
    /// bypassed.</para>
    /// </summary>
    [HttpPut("tenants/{tenantId:long}/commercial-terms")]
    public async Task<ActionResult<TenantBillingProfileDto>> SetTenantCommercialTerms(
        long tenantId, [FromBody] SetTenantCommercialTermsRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<TenantBillingMode>(request.BillingMode?.Trim(), ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
            return BadRequest(new
            {
                error = $"billingMode must be one of: {string.Join(", ", Enum.GetNames<TenantBillingMode>())}."
            });

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return NotFound(new { error = $"Tenant {tenantId} does not exist." });

        var reason = request.BillingModeReason?.Trim();
        var validationError = ValidateCommercialTerms(mode, reason, request, tenant, DateTime.UtcNow);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        var updated = await MutateTenantAsync(tenantId, "billing.tenant.commercial-terms", ct, target =>
        {
            var before = new
            {
                billingMode = target.BillingMode.ToString(),
                target.BillingModeReason,
                target.TrialEndsOn,
                target.BillingStartsOn
            };

            // Converting a trial mid-month has exactly the shape of a mid-month billing
            // start, so it is expressed as one: an unspecified billing start on a
            // Trial -> charged conversion inherits the trial's end date, and the ordinary
            // proration path then bills from that day. Without this default, converting on
            // the 20th would charge the customer for the 19 days they were still on trial —
            // the over-billing that proration exists to avoid — and the operator would have
            // to know to set the date by hand every time.
            var convertingFromTrial = target.BillingMode == TenantBillingMode.Trial
                                      && mode != TenantBillingMode.Trial
                                      && target.TrialEndsOn is not null;
            var billingStartsOn = request.BillingStartsOn
                                  ?? (convertingFromTrial ? target.TrialEndsOn : null);

            target.BillingMode = mode;
            target.BillingModeReason = mode == TenantBillingMode.Billable ? null : reason;
            target.TrialEndsOn = mode == TenantBillingMode.Trial ? request.TrialEndsOn : null;
            target.BillingStartsOn = billingStartsOn;
            return new
            {
                before,
                after = new
                {
                    billingMode = mode.ToString(),
                    billingModeReason = target.BillingModeReason,
                    target.TrialEndsOn,
                    target.BillingStartsOn
                }
            };
        });

        return updated ?? await GetTenantBillingProfile(tenantId, ct);
    }

    // PUT /api/platform/billing/tenants/{tenantId}/account-contact
    /// <summary>
    /// Sets who at the customer receives an invoice, where it is sent, on what terms it falls due,
    /// which of their references it must quote, the contract dates, and which of our own people
    /// owns the account.
    ///
    /// <para><b>Why this endpoint had to exist.</b> Every one of these columns was written exactly
    /// once, at provisioning, and never again — while <c>SubscriptionInvoiceService</c> reads all of
    /// them. It <i>refuses to invoice at all</i> without <c>BillingContactEmail</c>, computes
    /// <c>DueAtUtc</c> from <c>PaymentTermsDays</c>, and freezes
    /// <c>BillingAddress/BillingContactName/BillingContactEmail/PurchaseOrderReference</c> into the
    /// invoice's immutable buyer snapshot. So a customer who moved their accounts-payable mailbox
    /// after go-live had no correction path short of direct SQL: their invoices kept being addressed
    /// to a mailbox nobody reads, and — because the offboarding readiness gate requires a finalized
    /// subscription invoice — a tenant whose contact was wrong could not even be offboarded.</para>
    ///
    /// <para><b>Which authority.</b> Billing (Owner | BillingAdmin), the same policy as commercial
    /// terms and rate cards, because these values decide when a customer's money is due and where
    /// the demand for it is sent. Support must not be able to redirect an invoice.</para>
    ///
    /// <para>Older invoices are deliberately NOT re-snapshotted. An issued invoice is evidence of
    /// what was sent to whom on the day it was sent; correcting the tenant record must not silently
    /// rewrite history. The change takes effect on the next invoice.</para>
    /// </summary>
    [HttpPut("tenants/{tenantId:long}/account-contact")]
    public async Task<ActionResult<TenantBillingProfileDto>> SetTenantAccountContact(
        long tenantId, [FromBody] SetTenantAccountContactRequest request, CancellationToken ct)
    {
        if (request is null) return BadRequest(new { error = "A request body is required." });

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length < MinimumAccountContactReasonLength)
            return BadRequest(new
            {
                error = $"A reason of at least {MinimumAccountContactReasonLength} characters is required. " +
                        "Redirecting a customer's invoice is exactly the change that has to be " +
                        "attributable to somebody months later."
            });

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return NotFound(new { error = $"Tenant {tenantId} does not exist." });

        if (ValidateAccountContact(request, tenant) is string validationError)
            return BadRequest(new { error = validationError });

        var updated = await MutateTenantAsync(tenantId, "billing.tenant.account-contact", ct, target =>
        {
            var before = AccountContactSnapshot(target);

            target.BillingContactName = Normalize(request.BillingContactName);
            target.BillingContactEmail = Normalize(request.BillingContactEmail);
            target.BillingAddress = Normalize(request.BillingAddress);
            target.PurchaseOrderReference = Normalize(request.PurchaseOrderReference);
            target.PaymentTermsDays = request.PaymentTermsDays;
            target.AccountOwnerEmail = Normalize(request.AccountOwnerEmail);
            target.ContractStartOn = request.ContractStartOn;
            target.ContractEndOn = request.ContractEndOn;

            return new { before, after = AccountContactSnapshot(target), reason };
        });

        return updated ?? await GetTenantBillingProfile(tenantId, ct);
    }

    /// <summary>
    /// Same floor as a billing exemption. "Redirected the invoice" is not a reason; the person
    /// reading this in a dispute needs to know on whose instruction it was redirected.
    /// </summary>
    internal const int MinimumAccountContactReasonLength = 15;

    /// <summary>
    /// The longest payment terms this accepts. Not a guess: <c>SubscriptionInvoiceService</c>
    /// computes <c>DueAtUtc = IssuedAtUtc.AddDays(PaymentTermsDays ?? 30)</c>, so a mistyped 3650
    /// produces an invoice that falls due in ten years and silently leaves the AR balance out of
    /// every collections view for the rest of the company's life. Zero is allowed and means "due on
    /// receipt", which is a real commercial term; negative is not a term, it is a typo that back-dates
    /// the due date before the issue date.
    /// </summary>
    internal const int MaximumPaymentTermsDays = 365;

    /// <summary>
    /// Rejects the WRONG values, not merely the impossible ones — every rule here has a named
    /// downstream reader that would otherwise fail later, quietly, and somewhere else.
    /// </summary>
    private static string? ValidateAccountContact(SetTenantAccountContactRequest request, Tenant tenant)
    {
        var contactEmail = Normalize(request.BillingContactEmail);

        // Null and empty are values, and this is the one that matters: SubscriptionInvoiceService
        // throws BillingConflictException without it, so clearing this field on a charged tenant
        // stops their invoicing entirely and the symptom appears a month later in a billing run.
        if (contactEmail is null && tenant.BillingMode != TenantBillingMode.Internal)
            return "billingContactEmail cannot be cleared: invoicing refuses to issue without an " +
                   "invoice recipient, so this tenant would stop being billable and could not be " +
                   "offboarded either, because offboarding requires a finalized invoice. Supply an " +
                   "address, or move the tenant to billingMode Internal first.";

        if (contactEmail is not null && !IsPlausibleEmail(contactEmail))
            return $"billingContactEmail '{contactEmail}' is not an email address.";

        if (Normalize(request.AccountOwnerEmail) is string owner && !IsPlausibleEmail(owner))
            return $"accountOwnerEmail '{owner}' is not an email address.";

        if (request.PaymentTermsDays is int terms && (terms < 0 || terms > MaximumPaymentTermsDays))
            return $"paymentTermsDays must be between 0 and {MaximumPaymentTermsDays}. It is added to " +
                   "the issue date to compute when the invoice falls due, so a value outside that " +
                   "range produces an invoice that is either overdue on issue or effectively never due.";

        if (request.ContractStartOn is DateTime start && request.ContractEndOn is DateTime end
            && end <= start)
            return "contractEndOn must fall after contractStartOn.";

        return null;
    }

    private static object AccountContactSnapshot(Tenant tenant) => new
    {
        tenant.BillingContactName,
        tenant.BillingContactEmail,
        tenant.BillingAddress,
        tenant.PurchaseOrderReference,
        tenant.PaymentTermsDays,
        tenant.AccountOwnerEmail,
        tenant.ContractStartOn,
        tenant.ContractEndOn
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A shape check, not an RFC 5322 parser. It exists to catch the transposed address and the
    /// pasted display name, which are the real failures; deliverability is proven by sending, and
    /// the invitation surface already reports whether a provider accepted the message.
    /// </summary>
    private static bool IsPlausibleEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1) return false;
        var domain = value[(at + 1)..];
        return !value.Any(char.IsWhiteSpace)
               && domain.Contains('.')
               && !domain.StartsWith('.') && !domain.EndsWith('.');
    }

    /// <summary>
    /// The write half shared by the commercial mutations: load, apply, audit and commit in one
    /// transaction, then evict the cached tenant+plan snapshot.
    ///
    /// <para>Returns null on success (the caller re-reads the profile) or the error result. The
    /// audit runs INSIDE the transaction: a commercial term change with no audit row is a
    /// change to what a customer pays that nobody can attribute, so it commits with the write
    /// or not at all.</para>
    /// </summary>
    private async Task<ActionResult<TenantBillingProfileDto>?> MutateTenantAsync(
        long tenantId, string auditAction, CancellationToken ct, Func<Tenant, object> apply)
    {
        var missing = false;
        long? businessUnitId = null;

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
                if (tenant is null)
                {
                    missing = true;
                    return;
                }

                var auditPayload = apply(tenant);
                tenant.ModifiedBy = Actor();
                tenant.ModifiedOn = DateTime.UtcNow;
                businessUnitId = tenant.PrimaryBusinessUnitId;

                await _context.SaveChangesAsync(ct);
                await _audit.WriteAsync(User, auditAction, nameof(Tenant), tenant.Id.ToString(),
                    auditPayload, actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Billing mutation {Action} failed for tenant {TenantId}.", auditAction, tenantId);
            return StatusCode(500, new { error = "Tenant billing update failed." });
        }

        if (missing)
            return NotFound(new { error = $"Tenant {tenantId} does not exist." });

        // The entitlement floor reads billing mode and plan through a ~60s cache; drop it so
        // a tenant whose terms were just completed regains capacity on this node immediately
        // rather than staying capped for another minute after the operator fixed it.
        if (businessUnitId is long bu)
            _tenantAccess?.Evict(bu);
        return null;
    }

    /// <summary>
    /// The same rules provisioning applies, evaluated against the state the tenant would be
    /// left in. Kept as one method because the rules only make sense as a set: what a tenant is
    /// charged, from when, and on whose authority.
    /// </summary>
    private static string? ValidateCommercialTerms(
        TenantBillingMode mode, string? reason, SetTenantCommercialTermsRequest request,
        Tenant tenant, DateTime nowUtc)
    {
        if (mode == TenantBillingMode.Billable && tenant.PlanId is null)
            return "This tenant has no plan, so making it Billable would produce statements with no base " +
                   "subscription line and it would never be charged. Assign a plan first " +
                   "(PUT /api/platform/tenants/{id}/plan), then set the billing mode.";

        if (mode != TenantBillingMode.Billable
            && (string.IsNullOrEmpty(reason) || reason.Length < MinimumBillingModeReasonLength))
            return $"billingMode '{mode}' means this tenant is not charged; billingModeReason is required and must " +
                   $"be at least {MinimumBillingModeReasonLength} characters so the exemption is attributable to a " +
                   "real decision.";

        if (mode == TenantBillingMode.Trial)
        {
            if (request.TrialEndsOn is not DateTime trialEnd)
                return "A trial tenant must carry trialEndsOn. An open-ended trial is free service with no " +
                       "conversion date.";
            if (trialEnd <= nowUtc)
                return $"trialEndsOn {trialEnd:yyyy-MM-dd} is not in the future. To record that a trial has already " +
                       "ended, convert the tenant to Billable, Internal or Partner instead of back-dating it.";
        }

        return null;
    }

    private string Actor()
        => User.FindFirst("email")?.Value
           ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
           ?? "platform";

    private bool HoldsOwnerAuthority() =>
        User.HasClaim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner));

    private static string? ValidateRateCardShape(string? currency, IReadOnlyList<RateCardLineRequest>? lines)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
            return "Currency must be a 3-letter ISO code.";
        // P0-B3 (v1 constraint): billing is USD-only — plan base prices are
        // MonthlyPriceUsd and statement math has no FX conversion, so a non-USD
        // rate card would mix currencies on a single statement. Rejected here
        // (400) for create AND update; compute additionally 409s on any legacy
        // non-USD card. Multi-currency support is deferred (see LEDGER).
        if (!string.Equals(currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase))
            return "v1 billing is USD-only: rate card currency must be 'USD'.";
        if (lines is null || lines.Count == 0)
            return "At least one rate card line is required.";
        if (lines.Any(l => string.IsNullOrWhiteSpace(l.MeterKey) || string.IsNullOrWhiteSpace(l.Unit)))
            return "Every line requires a MeterKey and a Unit.";
        if (lines.Any(l => l.IncludedQuantity < 0 || l.UnitPrice < 0))
            return "IncludedQuantity and UnitPrice must be non-negative.";
        var uncertified = lines.Select(l => l.MeterKey.Trim()).FirstOrDefault(key =>
            UsageMeterCatalog.BillingCertification(key) != MeterCertificationStatus.BillingCertified);
        if (uncertified is not null)
            return $"Meter '{uncertified}' is not BILLING_CERTIFIED and cannot be placed on a rate card.";
        if (lines.Select(l => l.MeterKey.Trim()).Distinct(StringComparer.Ordinal).Count() != lines.Count)
            return "MeterKey must be unique per rate card.";
        return null;
    }

    private static RateCardLine ToEntity(RateCardLineRequest request) => new()
    {
        MeterKey = request.MeterKey.Trim(),
        IncludedQuantity = request.IncludedQuantity,
        UnitPrice = request.UnitPrice,
        Unit = request.Unit.Trim(),
        TierNote = string.IsNullOrWhiteSpace(request.TierNote) ? null : request.TierNote.Trim()
    };

    private static RateCardDto ToDto(RateCard card) => new(
        card.Id, card.Code, card.Currency, card.EffectiveFromUtc, card.EffectiveToUtc,
        card.IsActive, card.CreatedOn, card.CreatedBy, card.Version,
        card.Lines.OrderBy(l => l.MeterKey, StringComparer.Ordinal)
            .Select(l => new RateCardLineDto(l.Id, l.MeterKey, l.IncludedQuantity, l.UnitPrice, l.Unit, l.TierNote))
            .ToList());

    private static BillingStatementDto ToDto(BillingStatement statement) => new(
        statement.Id, statement.TenantId, statement.PeriodStartUtc, statement.PeriodEndUtc,
        statement.RateCardId, statement.Currency, statement.Status.ToString(), statement.TotalAmount,
        statement.ComputedAtUtc, statement.ComputedBy, statement.FinalizedAtUtc, statement.FinalizedBy,
        statement.Lines.OrderBy(l => l.MeterKey, StringComparer.Ordinal)
            .Select(l => new BillingStatementLineDto(
                l.MeterKey, l.Description, l.MeteredQuantity, l.IncludedQuantity,
                l.BillableQuantity, l.UnitPrice, l.Amount, l.SourceNote, l.CoverageNote))
            .ToList());
}

public sealed record RateCardLineRequest(
    string MeterKey, decimal IncludedQuantity, decimal UnitPrice, string Unit, string? TierNote);

public sealed record CreateRateCardRequest(
    string Code, string Currency, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc,
    bool IsActive, IReadOnlyList<RateCardLineRequest> Lines);

public sealed record UpdateRateCardRequest(
    string Currency, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc,
    bool IsActive, IReadOnlyList<RateCardLineRequest> Lines);

public sealed record ComputeStatementRequest(long TenantId, string Period, long? RateCardId);
public sealed record CorrectUsageRatingRequest(string IdempotencyKey, string Reason);
public sealed record DocumentCoverageProposalRequest(string Period, DateTime? MidPeriodCutoverUtc, string Reason);
public sealed record DocumentCoverageApprovalRequest(string Period, string Reason);

public sealed record RateCardLineDto(
    long Id, string MeterKey, decimal IncludedQuantity, decimal UnitPrice, string Unit, string? TierNote);

public sealed record RateCardDto(
    long Id, string Code, string Currency, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc,
    bool IsActive, DateTime CreatedOn, string? CreatedBy, long Version,
    IReadOnlyList<RateCardLineDto> Lines);

/// <summary>
/// One statement line on the wire. <paramref name="SourceNote"/> is provenance
/// only; <paramref name="CoverageNote"/> is the meter's signal-coverage caveat
/// (null when the signal is complete). They are separate fields so a priced page
/// line still visibly carries its NOT-BILLING-READY warning without an operator
/// having to parse it back out of the provenance string.
/// </summary>
public sealed record BillingStatementLineDto(
    string MeterKey, string Description, decimal MeteredQuantity, decimal IncludedQuantity,
    decimal BillableQuantity, decimal UnitPrice, decimal Amount, string? SourceNote,
    string? CoverageNote);

public sealed record BillingStatementDto(
    long Id, long TenantId, DateTime PeriodStartUtc, DateTime PeriodEndUtc,
    long RateCardId, string Currency, string Status, decimal TotalAmount,
    DateTime ComputedAtUtc, string ComputedBy, DateTime? FinalizedAtUtc, string? FinalizedBy,
    IReadOnlyList<BillingStatementLineDto> Lines);

/// <summary>
/// The revenue-risk readout. The headline counts come first because they are the part an
/// operator reads: a page of green rows and one number saying "4 billable tenants charged
/// nothing" is read; a list you have to scan for red is not.
/// </summary>
public sealed record RevenueRiskReportDto(
    DateTime GeneratedAtUtc,
    int TenantCount,
    int AtRiskCount,
    int ExpiredTrialCount,
    int BillableTenantsChargedNothingCount,
    IReadOnlyList<TenantRevenueRisk> Tenants)
{
    /// <summary>
    /// Tenants consuming the platform under terms nobody set. Always computed over the whole
    /// fleet, never over the filtered list: this is the number the board badges, and a badge
    /// that changes when you apply a filter is a badge nobody trusts.
    /// </summary>
    public int CommercialConfigurationRequiredCount { get; init; }
}

/// <summary>Statement header for the tenant billing tab — the lines are fetched per statement.</summary>
public sealed record BillingStatementSummaryDto(
    long Id, string Period, DateTime PeriodStartUtc, DateTime PeriodEndUtc, long RateCardId,
    string Currency, string Status, decimal TotalAmount, DateTime ComputedAtUtc,
    string ComputedBy, DateTime? FinalizedAtUtc, string? FinalizedBy);

/// <summary>
/// Everything the console's tenant billing tab renders. <paramref name="PinnedRateCardMissing"/>
/// is true when the tenant carries a RateCardId that no longer resolves — the pin has no foreign
/// key behind it, and billing refuses to compute rather than repricing the tenant onto whatever
/// card is active, so the console has to be able to see the dangling pin directly.
/// </summary>
public sealed record TenantBillingProfileDto(
    long TenantId,
    string Name,
    string Slug,
    string Status,
    string BillingMode,
    string? BillingModeReason,
    long? PlanId,
    string? PlanCode,
    string? PlanName,
    decimal? PlanMonthlyPriceUsd,
    long? PinnedRateCardId,
    string? PinnedRateCardCode,
    bool PinnedRateCardMissing,
    DateTime? BillingStartsOn,
    DateTime? TrialEndsOn,
    DateTime? ContractStartOn,
    DateTime? ContractEndOn,
    int? PaymentTermsDays,
    string? PurchaseOrderReference,
    string? BillingContactName,
    string? BillingContactEmail,
    // Present so the console can SHOW what the next invoice's buyer snapshot will say. It was
    // settable at provisioning, frozen into every invoice thereafter, and visible nowhere.
    string? BillingAddress,
    string? AccountOwnerEmail,
    TenantRevenueRisk RevenueRisk,
    IReadOnlyList<BillingStatementSummaryDto> Statements);

public sealed record SetTenantRateCardRequest(long? RateCardId, string? Reason);

public sealed record SetTenantCommercialTermsRequest(
    string? BillingMode,
    string? BillingModeReason,
    DateTime? TrialEndsOn,
    DateTime? BillingStartsOn);

/// <summary>
/// Who is invoiced, where, on what terms, under which contract, and who owns the account here.
///
/// <para>Every field is a full replacement rather than a patch: an omitted field clears the value,
/// which is why the endpoint demands the whole block and audits before/after. A partial-update
/// shape would make "the operator left this blank" and "the operator wants this cleared"
/// indistinguishable on a set of fields where clearing one stops the customer being invoiced.</para>
/// </summary>
public sealed record SetTenantAccountContactRequest(
    string? BillingContactName,
    string? BillingContactEmail,
    string? BillingAddress,
    string? PurchaseOrderReference,
    int? PaymentTermsDays,
    string? AccountOwnerEmail,
    DateTime? ContractStartOn,
    DateTime? ContractEndOn,
    string? Reason);
