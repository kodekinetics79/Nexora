using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Controllers;

/// <summary>
/// Platform-plane SaaS billing: live usage meters, rate cards, statement
/// compute/finalize, cost/margin. Every endpoint requires the
/// <see cref="PlatformPolicies.Billing"/> policy (Owner | BillingAdmin) via the
/// class-level default-deny attribute; mutations are audited through
/// <see cref="IPlatformAuditService"/>.
/// </summary>
[ApiController]
[Route("api/platform/billing")]
[Authorize(Policy = PlatformPolicies.Billing)]
public class PlatformBillingController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IBillingStatementService _billing;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<PlatformBillingController> _logger;

    public PlatformBillingController(
        ErpRfqAutomationContext context,
        IBillingStatementService billing,
        IPlatformAuditService audit,
        ILogger<PlatformBillingController> logger)
    {
        _context = context;
        _billing = billing;
        _audit = audit;
        _logger = logger;
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

        BillingStatement statement;
        try
        {
            statement = await _billing.ComputeStatementAsync(request.TenantId, period, request.RateCardId, ct);
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

    // POST /api/platform/billing/statements/{id}/finalize
    [HttpPost("statements/{id:long}/finalize")]
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

    private string Actor()
        => User.FindFirst("email")?.Value
           ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
           ?? "platform";

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
        statement.ComputedAtUtc, statement.FinalizedAtUtc, statement.FinalizedBy,
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
    DateTime ComputedAtUtc, DateTime? FinalizedAtUtc, string? FinalizedBy,
    IReadOnlyList<BillingStatementLineDto> Lines);
