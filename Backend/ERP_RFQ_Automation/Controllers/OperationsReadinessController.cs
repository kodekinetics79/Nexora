using System.Security.Claims;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/operations/readiness")]
public sealed class OperationsReadinessController(
    ErpRfqAutomationContext db,
    HealthCheckService healthChecks,
    ExtractionDeadLetterService deadLetters) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult<OperationsReadinessResponse>> Get(CancellationToken ct)
    {
        var tenantId = TenantId();
        var health = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"), ct);

        var extraction = await db.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId)
            .GroupBy(x => x.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        var unresolvedExtractionDeadLetters = await db.Set<ExtractionJob>().AsNoTracking()
            .CountAsync(job => job.BusinessUnitId == tenantId
                && job.Status == ExtractionStatus.DeadLetter
                && (db.ExtractionDeadLetterEvents
                        .Where(disposition => disposition.BusinessUnitId == tenantId
                            && disposition.ExtractionJobId == job.Id
                            && disposition.AttemptNumber == job.Attempts)
                        .OrderByDescending(disposition => disposition.Id)
                        .Select(disposition => (ExtractionDeadLetterAction?)disposition.Action)
                        .FirstOrDefault() != ExtractionDeadLetterAction.SourceObjectUnavailable
                    || db.ExtractionDeadLetterEvents.Any(disposition =>
                        disposition.BusinessUnitId == tenantId
                        && disposition.ExtractionJobId == job.Id
                        && disposition.AttemptNumber == job.Attempts
                        && (disposition.Action == ExtractionDeadLetterAction.MalwareDetected
                            || disposition.Action == ExtractionDeadLetterAction.EvidenceIntegrityFailure))), ct);
        // THE EMAIL INTAKE CUTOVER DRAIN BOUNDARY, made countable.
        //
        // Before the cutover an email became one extraction job per attachment, with no owning
        // EmailInquiryComponent. Those jobs are now REFUSED at persistence (see the cutover
        // fence in LeadPersister): a per-document Lead from one part of a message is the defect
        // the assembly barrier exists to remove, so they are not quietly routed the old way.
        //
        // That makes "how much legacy work is still in flight?" an operational question with a
        // real answer, and this is where an operator already looks for one. It is deliberately
        // a COUNT and not a dual-routing switch: permanent dual routing is how a temporary
        // compatibility path becomes the path, and nobody ever finds out the migration never
        // finished. In-flight only — a legacy job that already succeeded or was dead-lettered
        // is history, and history does not drain.
        var legacyEmailJobs = await db.Set<ExtractionJob>().AsNoTracking()
            .Where(job => job.BusinessUnitId == tenantId
                && job.SourceType == ExtractionSourceType.Email
                && job.EmailInquiryComponentId == null
                && (job.Status == ExtractionStatus.Pending
                    || job.Status == ExtractionStatus.Leased
                    || job.Status == ExtractionStatus.Extracting
                    || job.Status == ExtractionStatus.Persisting))
            .GroupBy(_ => 1)
            .Select(group => new LegacyEmailIntakeDrain(
                group.Count(),
                group.Min(job => (DateTime?)job.CreatedOn)))
            .SingleOrDefaultAsync(ct) ?? new LegacyEmailIntakeDrain(0, null);

        var procurement = await db.ProcurementOutboxMessages.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId)
            .GroupBy(x => x.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        var delivery = await db.QuoteDeliveryRequests.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId)
            .GroupBy(_ => 1)
            .Select(group => new QueueCounts(
                group.Count(x => x.CompletedOn == null && x.DeadLetteredOn == null && x.LeaseUntil == null),
                group.Count(x => x.CompletedOn == null && x.DeadLetteredOn == null && x.LeaseUntil != null),
                group.Count(x => x.DeadLetteredOn != null)))
            .SingleOrDefaultAsync(ct) ?? new QueueCounts(0, 0, 0);

        var ceiling = await db.AiProcessingPolicies.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId)
            .Select(x => (decimal?)x.ExternalDependencyCeilingPercent)
            .SingleOrDefaultAsync(ct) ?? 10m;
        var aiSummary = await AiExternalDependencyEvaluator.EvaluateAsync(
            db.AiRequests, tenantId, ceiling, ct);

        var queues = new[]
        {
            new QueueStatus("extraction", "Lead extraction", Sum(extraction, ExtractionStatus.Pending),
                Sum(extraction, ExtractionStatus.Leased, ExtractionStatus.Extracting, ExtractionStatus.Persisting),
                unresolvedExtractionDeadLetters),
            new QueueStatus("supplier-rfq", "Supplier RFQ dispatch",
                Sum(procurement, ProcurementOutboxStatuses.Pending, ProcurementOutboxStatuses.Failed),
                Sum(procurement, ProcurementOutboxStatuses.Processing),
                Sum(procurement, ProcurementOutboxStatuses.DeadLettered)),
            new QueueStatus("quote-delivery", "Customer quote delivery",
                delivery.Pending, delivery.InFlight, delivery.DeadLetter)
        };
        var blockingReasons = health.Entries
            .Where(entry => entry.Value.Status == HealthStatus.Unhealthy)
            .Select(entry => $"Dependency {entry.Key} is unhealthy.")
            .Concat(queues.Where(queue => queue.DeadLetter > 0)
                .Select(queue => $"{queue.Label} has {queue.DeadLetter} dead-letter item(s)."))
            .Concat(aiSummary.CeilingBreached
                ? [$"Unauthorized external AI share is {aiSummary.ExternalSharePercent}% and exceeds the configured {aiSummary.CeilingPercent}% policy limit."]
                : Array.Empty<string>())
            // Stated as a blocking reason rather than a quiet number, because these jobs cannot
            // finish: they will be refused at persistence and the mail behind them will look
            // ingested while producing nothing. The remedy is one sentence and it is here.
            .Concat(legacyEmailJobs.InFlight > 0
                ? [$"{legacyEmailJobs.InFlight} email extraction job(s) predate the inquiry-assembly "
                    + "cutover and own no message component. They will not produce a lead; reprocess "
                    + "the messages from the inbound mail triage surface to drain them."]
                : Array.Empty<string>())
            .ToArray();
        var deploymentReadiness = blockingReasons.Length > 0
            ? HealthStatus.Unhealthy.ToString()
            : health.Status.ToString();

        var response = new OperationsReadinessResponse(
            DateTimeOffset.UtcNow,
            deploymentReadiness,
            blockingReasons,
            health.Entries.OrderBy(x => x.Key).Select(x => new ReadinessCheck(
                x.Key, x.Value.Status.ToString(), Math.Round(x.Value.Duration.TotalMilliseconds, 1))).ToArray(),
            queues,
            aiSummary,
            legacyEmailJobs);
        return Ok(response);
    }

    [HttpGet("extraction-dead-letters")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyList<ExtractionDeadLetterItem>>> GetExtractionDeadLetters(
        CancellationToken ct) => Ok(await deadLetters.ListAsync(TenantId(), ct));

    [HttpPost("extraction-dead-letters/{jobId:long}/recover")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    [RequireModulePermission("Leads", PermissionAction.Create)]
    public async Task<ActionResult<RecoverExtractionDeadLetterResult>> RecoverExtractionDeadLetter(
        long jobId,
        [FromBody] RecoverExtractionDeadLetterCommand command,
        CancellationToken ct)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(ClaimTypes.Email)
            ?? "authenticated-operator";
        try
        {
            return Ok(await deadLetters.RecoverAsync(TenantId(), jobId, actorId, command, ct));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status404NotFound });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status400BadRequest });
        }
        catch (DeadLetterDependencyUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status503ServiceUnavailable });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status409Conflict });
        }
    }

    private long TenantId()
    {
        var raw = User.FindFirstValue("businessUnitId");
        if (!long.TryParse(raw, out var tenantId) || tenantId <= 0)
            throw new InvalidOperationException("Authenticated tenant context is required.");
        return tenantId;
    }

    private static int Sum<TKey>(IReadOnlyDictionary<TKey, int> counts, params TKey[] statuses)
        where TKey : notnull => statuses.Sum(status => counts.GetValueOrDefault(status));
}

public sealed record OperationsReadinessResponse(
    DateTimeOffset CheckedAt,
    string DeploymentReadiness,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<ReadinessCheck> HealthChecks,
    IReadOnlyList<QueueStatus> Queues,
    AiExternalDependencySnapshot AiExternalDependency,
    LegacyEmailIntakeDrain LegacyEmailIntake);

/// <summary>How much pre-cutover email work is still in flight for this tenant.</summary>
/// <param name="InFlight">Email extraction jobs owning no inquiry component that are still
/// Pending/Leased/Extracting/Persisting. Zero means the drain is complete for this tenant.</param>
/// <param name="OldestCreatedOn">When the oldest of them was queued — null when there are
/// none. It answers "is this a stuck backlog or something that arrived this morning?".</param>
public sealed record LegacyEmailIntakeDrain(int InFlight, DateTime? OldestCreatedOn);

public sealed record ReadinessCheck(string Name, string Status, double DurationMilliseconds);
public sealed record QueueStatus(string Key, string Label, int Pending, int InFlight, int DeadLetter);
public sealed record QueueCounts(int Pending, int InFlight, int DeadLetter);
