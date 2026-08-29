using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/leads/{leadId:long}")]
public sealed class LeadParticipationController : ControllerBase
{
    private readonly ILeadParticipationService _participation;
    private readonly IRfqPromotionService _promotion;
    private readonly ILeadDecisionWorkbenchService _workbench;
    private readonly IRfqRevisionImpactResolutionService _rfqImpactResolution;
    private readonly ICommercialAccessContext _commercialAccess;
    private readonly IRoleGate _roleGate;

    public LeadParticipationController(ILeadParticipationService participation, IRfqPromotionService promotion,
        ILeadDecisionWorkbenchService workbench, IRfqRevisionImpactResolutionService rfqImpactResolution,
        ICommercialAccessContext commercialAccess, IRoleGate roleGate)
    {
        _participation = participation;
        _promotion = promotion;
        _workbench = workbench;
        _rfqImpactResolution = rfqImpactResolution;
        _commercialAccess = commercialAccess;
        _roleGate = roleGate;
    }

    [HttpGet("participation")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<LeadParticipationResult?>> GetCurrent(long leadId, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out _)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        return Ok(await _participation.GetCurrentDecisionAsync(businessUnitId, leadId, ct));
    }

    [HttpPost("participation/fit-assessments")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<LeadFitAssessmentResult>> Assess(
        long leadId, [FromBody] FitAssessmentRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            var result = await _participation.RecordFitAssessmentAsync(businessUnitId, leadId,
                new RecordLeadFitAssessmentCommand(request.ExpectedLeadRevisionId, request.ExpectedDecisionVersion,
                    request.ExpectedFitVersion, request.OverallDecision, request.Rationale,
                    (request.Criteria ?? Array.Empty<FitCriterionRequest>())
                        .Select(x => new LeadFitCriterionCommand(x.Code, x.Decision, x.Note)).ToArray(), key, actor), ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "Assessment refused", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "Assessment refused", ex.Message)); }
    }

    [HttpPost("participation/decisions")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<LeadParticipationResult>> Decide(
        long leadId, [FromBody] ParticipationDecisionRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (request.Commit
            && !await ParticipationDecisionAuthority.CanCommitOrPromoteAsync(User, businessUnitId, _roleGate))
            return Forbid();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            var lines = (request.Lines ?? Array.Empty<ParticipationLineRequest>())
                .Select(x => new LeadLineParticipationCommand(x.LeadItemRevisionId, x.Choice,
                    x.ReasonCode, x.ReasonNotes, x.ProductId, x.Quantity, x.UnitOfMeasure, x.Currency)).ToArray();
            var result = await _participation.CommitDecisionAsync(businessUnitId, leadId,
                new CommitLeadParticipationCommand(request.ExpectedLeadRevisionId, request.ExpectedDecisionVersion,
                    request.ExpectedParticipationVersion, request.Commit, request.FitAssessmentId,
                    lines, key, actor, request.ReasonCode, request.Notes), ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "Decision refused", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "Decision refused", ex.Message)); }
    }

    [HttpPost("participation/promote")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    [RequireModulePermission("RFQ Management", PermissionAction.Create)]
    [RequireManagerRole]
    public async Task<ActionResult<RfqPromotionResult>> Promote(
        long leadId, [FromBody] PromoteRfqRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            var result = await _promotion.PromoteAsync(businessUnitId, leadId,
                new PromoteLeadToRfqCommand(request.ExpectedLeadRevisionId,
                    request.ExpectedDecisionVersion, request.ExpectedParticipationVersion,
                    request.ParticipationDecisionId, key, actor), ct);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "RFQ promotion refused", ex.Message)); }
        catch (LeadInquiryPromotionRouteException ex) { return Conflict(PromotionRouteProblem(ex)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "RFQ promotion refused", ex.Message)); }
    }

    [HttpGet("decision-workbench")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<LeadDecisionWorkbenchDto>> GetWorkbench(long leadId, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out _)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        try { return Ok(await _workbench.GetAsync(businessUnitId, leadId, ct)); }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "Workbench unavailable", ex.Message)); }
    }

    [HttpPut("fit-assessment")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<FitAssessmentDto>> SaveFitAssessment(
        long leadId, [FromBody] SaveFitAssessmentRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            await _participation.RecordFitAssessmentAsync(businessUnitId, leadId,
                new RecordLeadFitAssessmentCommand(request.ExpectedLeadRevisionId, request.ExpectedDecisionVersion,
                    request.ExpectedFitVersion, request.OverallDecision, request.Rationale,
                    (request.Criteria ?? Array.Empty<FitCriterionRequest>())
                        .Select(x => new LeadFitCriterionCommand(x.Code, x.Decision, x.Note)).ToArray(), key, actor), ct);
            var workbench = await _workbench.GetAsync(businessUnitId, leadId, ct);
            return Ok(workbench.FitAssessment);
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "Assessment refused", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "Assessment refused", ex.Message)); }
    }

    [HttpPut("participation")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<SaveParticipationResponse>> SaveParticipation(
        long leadId, [FromBody] SaveParticipationRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (request.Commit
            && !await ParticipationDecisionAuthority.CanCommitOrPromoteAsync(User, businessUnitId, _roleGate))
            return Forbid();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            var lines = (request.Lines ?? Array.Empty<SaveParticipationLineRequest>())
                .Select(x => new LeadLineParticipationCommand(x.RevisionLineId, x.Decision,
                    x.ReasonCode, x.Note, x.ProductId, x.Quantity, x.UnitOfMeasure, x.Currency)).ToArray();
            var result = await _participation.CommitDecisionAsync(businessUnitId, leadId,
                new CommitLeadParticipationCommand(request.ExpectedLeadRevisionId, request.ExpectedDecisionVersion,
                    request.ExpectedParticipationVersion, request.Commit, null, lines, key, actor,
                    request.ReasonCode, request.Notes), ct);
            return Ok(new SaveParticipationResponse(request.ExpectedDecisionVersion, result.Sequence,
                result.IsCommitted ? "COMMITTED" : "DRAFT"));
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "Participation refused", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "Participation refused", ex.Message)); }
    }

    [HttpPost("promote-to-rfq")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    [RequireModulePermission("RFQ Management", PermissionAction.Create)]
    public async Task<ActionResult<RfqPromotionResult>> PromoteToRfq(
        long leadId, [FromBody] PromoteToRfqRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        // Resolve tenant-scoped resource visibility before the role gate. Keeping the manager
        // policy as an action-level authorization attribute returned 403 before this code ran,
        // disclosing that a foreign-tenant Lead id existed. Same-tenant callers still receive
        // the truthful 403 below when they lack commercial decision authority.
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (!await ParticipationDecisionAuthority.CanCommitOrPromoteAsync(User, businessUnitId, _roleGate))
            return Forbid();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            return Ok(await _promotion.PromoteAsync(businessUnitId, leadId,
                new PromoteLeadToRfqCommand(request.ExpectedLeadRevisionId, request.ExpectedDecisionVersion,
                    request.ExpectedParticipationVersion, null, key, actor), ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "Lead not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "RFQ promotion refused", ex.Message)); }
        catch (LeadInquiryPromotionRouteException ex) { return Conflict(PromotionRouteProblem(ex)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "RFQ promotion refused", ex.Message)); }
    }

    [HttpPost("rfq-revision-impact/resolve")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
    [RequireManagerRole]
    public async Task<ActionResult<RfqRevisionImpactResolutionResult>> ResolveRfqRevisionImpact(
        long leadId, [FromBody] ResolveRfqRevisionImpactRequest request, CancellationToken ct)
    {
        if (!TryContext(out var businessUnitId, out var actor)) return Unauthorized();
        if (!await _commercialAccess.CanAccessLeadAsync(leadId, ct)) return NotFound();
        if (!TryIdempotencyKey(out var key)) return IdempotencyRequired();
        try
        {
            return Ok(await _rfqImpactResolution.ResolveAsync(businessUnitId, leadId,
                new ResolveRfqRevisionImpactCommand(request.RfqId, request.ExpectedLeadRevisionId,
                    request.ReconciliationReason, request.ConfirmedHistoricalRfqUnchanged,
                    key, actor), ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(Problem(404, "RFQ amendment review not found", ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(Problem(400, "RFQ amendment review refused", ex.Message)); }
        catch (InvalidOperationException ex) { return Conflict(Problem(409, "RFQ amendment review refused", ex.Message)); }
    }

    private bool TryContext(out long businessUnitId, out string actor)
    {
        actor = User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name ?? string.Empty;
        return long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId)
            && businessUnitId > 0 && !string.IsNullOrWhiteSpace(actor);
    }

    private bool TryIdempotencyKey(out string key)
    {
        key = Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim() ?? string.Empty;
        return key.Length is > 0 and <= 256;
    }

    private ActionResult IdempotencyRequired() => BadRequest(Problem(400, "Idempotency key required",
        "Send a stable Idempotency-Key header so a retry cannot duplicate a commercial decision."));

    private ProblemDetails Problem(int status, string title, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return problem;
    }

    private ProblemDetails PromotionRouteProblem(LeadInquiryPromotionRouteException error)
    {
        var problem = Problem(409, "RFQ promotion routed to governed workflow", error.Message);
        problem.Extensions["reasonCode"] = error.ReasonCode;
        return problem;
    }
}

public sealed record FitCriterionRequest(string Code, string Decision, string? Note);
public sealed record FitAssessmentRequest(long ExpectedLeadRevisionId, int ExpectedDecisionVersion,
    int? ExpectedFitVersion, string OverallDecision, string Rationale, IReadOnlyList<FitCriterionRequest>? Criteria);
public sealed record ParticipationLineRequest(
    long LeadItemRevisionId,
    LeadLineParticipationChoice Choice,
    string? ReasonCode,
    string? ReasonNotes,
    long? ProductId,
    decimal? Quantity,
    string? UnitOfMeasure,
    string? Currency);
public sealed record ParticipationDecisionRequest(
    long ExpectedLeadRevisionId,
    int ExpectedDecisionVersion,
    int? ExpectedParticipationVersion,
    bool Commit,
    long? FitAssessmentId,
    IReadOnlyList<ParticipationLineRequest>? Lines,
    string? ReasonCode,
    string? Notes);
public sealed record PromoteRfqRequest(long ExpectedLeadRevisionId, int ExpectedDecisionVersion,
    int ExpectedParticipationVersion, long? ParticipationDecisionId);
public sealed record SaveFitAssessmentRequest(long ExpectedLeadRevisionId, int ExpectedDecisionVersion,
    int? ExpectedFitVersion, string OverallDecision, string Rationale, IReadOnlyList<FitCriterionRequest>? Criteria);
public sealed record SaveParticipationLineRequest(long RevisionLineId, LeadLineParticipationChoice Decision,
    string? ReasonCode, string? Note, long? ProductId, decimal? Quantity, string? UnitOfMeasure, string? Currency);
public sealed record SaveParticipationRequest(long ExpectedLeadRevisionId, int ExpectedDecisionVersion,
    int? ExpectedParticipationVersion, bool Commit, IReadOnlyList<SaveParticipationLineRequest>? Lines,
    string? ReasonCode, string? Notes);
public sealed record SaveParticipationResponse(int DecisionVersion, int ParticipationVersion, string ParticipationStatus);
public sealed record PromoteToRfqRequest(long ExpectedLeadRevisionId, int ExpectedDecisionVersion,
    int ExpectedParticipationVersion, string? IdempotencyKey);
public sealed record ResolveRfqRevisionImpactRequest(long RfqId, long ExpectedLeadRevisionId,
    string ReconciliationReason, bool ConfirmedHistoricalRfqUnchanged);
