using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

public interface IEmailInquiryAssemblyCoordinator
{
    /// <summary>
    /// Resolves a component by its FULL identity.
    ///
    /// <para><b>`AssemblyId` is not optional.</b> The unique index is
    /// <c>(BusinessUnitId, AssemblyId, ComponentKey)</c>, and a lookup on tenant + key alone is a
    /// PREFIX of it — which matches more than one row whenever one tenant receives the same
    /// message in two monitored mailboxes. That is routine: a customer mails <c>sales@</c> and
    /// CCs <c>quotes@</c>, or a distribution list fans out. `ComponentKey` derives from the
    /// Message-Id, and assemblies are unique per <c>(tenant, mailbox, message)</c> — so the two
    /// messages produce byte-identical component keys, and a prefix lookup binds one message's
    /// extraction outcome onto the other's row: one advances on evidence it does not own, the
    /// other stalls at the barrier forever.</para>
    /// </summary>
    Task<EmailInquiryComponent?> FindComponentAsync(
        long businessUnitId, long assemblyId, string componentKey, CancellationToken ct = default);

    Task RecordComponentQueuedAsync(
        long businessUnitId, long assemblyId, string componentKey, long extractionJobId,
        CancellationToken ct = default);

    Task RecordComponentOutcomeAsync(
        long businessUnitId, long assemblyId, string componentKey,
        EmailInquiryComponentStatus status,
        string? reasonCode, string? reasonDetail, long? sourceDocumentOccurrenceId,
        CancellationToken ct = default);

    /// <summary>
    /// Records a component's extraction result durably, completes the component and
    /// re-evaluates the message — as ONE transaction.
    /// </summary>
    Task<EmailInquiryAssemblyEvaluation> RecordComponentResultAsync(
        long businessUnitId,
        long componentId,
        long extractionJobId,
        EmailInquiryComponentResultPayload payload,
        CancellationToken ct = default);

    Task<EmailInquiryAssemblyEvaluation> ReevaluateAsync(
        long assemblyId, long businessUnitId, CancellationToken ct = default);

    Task MarkNoInquiryAsync(EmailInquiryAssembly assembly, string reason, CancellationToken ct = default);

    /// <summary>Sends a message to human review with a machine-readable reason.</summary>
    Task HoldForReviewAsync(
        long businessUnitId, long assemblyId, string reasonCode, string reasonDetail,
        CancellationToken ct = default);

    /// <summary>Marks a message assembled once its single Lead exists.</summary>
    Task MarkAssembledAsync(long businessUnitId, long assemblyId, CancellationToken ct = default);

    /// <summary>
    /// Whether an extraction job exists AND is the job this exact component owns.
    ///
    /// <para>A non-null <c>ExtractionJobId</c> is a claim, not proof. Tenant plus id is not
    /// enough either: within one tenant that would accept a job belonging to a different
    /// component, or to a different message entirely, and the component would be counted as
    /// scheduled while its own work was never queued — a message that waits at the barrier
    /// forever for something nobody is running.</para>
    ///
    /// <para>The full tuple is checked: the job's tenant, its batch (derived from the assembly,
    /// so a job from another message cannot match), and its
    /// <c>SourceDocumentOccurrenceId</c> resolved from the occurrence whose idempotency key is
    /// built from THIS component's persisted key. Anything short of all three is treated as
    /// unscheduled work and rescheduled.</para>
    /// </summary>
    Task<bool> DurableJobBelongsToComponentAsync(
        long businessUnitId, long extractionJobId, Guid expectedBatchId, string componentKey,
        CancellationToken ct = default);
}

/// <summary>
/// The one writer of assembly and component state after capture.
///
/// <para>It exists so that "what is this message now?" is answered by
/// <see cref="EmailInquiryAssemblyStateMachine"/> from the durable component rows, in one
/// place, every time — rather than inferred by whichever worker happened to finish last. The
/// previous pipeline had no such place, which is why a body finishing before its attachment
/// silently became a complete commercial fact.</para>
/// </summary>
public sealed class EmailInquiryAssemblyCoordinator : IEmailInquiryAssemblyCoordinator
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<EmailInquiryAssemblyCoordinator> _logger;

    public EmailInquiryAssemblyCoordinator(
        ErpRfqAutomationContext context, ILogger<EmailInquiryAssemblyCoordinator> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<EmailInquiryComponent?> FindComponentAsync(
        long businessUnitId, long assemblyId, string componentKey, CancellationToken ct = default)
        // SingleOrDefault, not FirstOrDefault: this tuple IS the unique index, so a second match
        // is a corrupt index rather than a row to pick between. Throwing surfaces it; guessing
        // writes one message's outcome onto another's component and hides it forever.
        => _context.EmailInquiryComponents
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                                       && x.AssemblyId == assemblyId
                                       && x.ComponentKey == componentKey, ct)!;

    public async Task RecordComponentQueuedAsync(
        long businessUnitId, long assemblyId, string componentKey, long extractionJobId,
        CancellationToken ct = default)
    {
        var component = await FindComponentAsync(businessUnitId, assemblyId, componentKey, ct);
        // A missing component is an ownership failure, not a no-op. Returning silently is how a
        // scheduled job ends up owned by nobody and its message waits at the barrier forever.
        if (component is null)
            throw new InvalidOperationException(
                $"Component '{componentKey}' of assembly {assemblyId} was not found for business "
                + $"unit {businessUnitId}; the job binding has no owner.");

        component.ExtractionJobId = extractionJobId;
        // Pending -> Extracting only. A component that already reached a terminal state is left
        // alone: a replayed enqueue must not walk a finished part backwards and reopen a
        // barrier that has already been satisfied.
        if (component.Status == EmailInquiryComponentStatus.Pending)
            component.Status = EmailInquiryComponentStatus.Extracting;
        component.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // The MESSAGE moves too, in the same transaction.
        //
        // This used to save the component and stop. The assembly therefore stayed at Captured
        // for its entire life, and when the barrier finally evaluated ReadyForAssembly the
        // transition Captured -> ReadyForAssembly was illegal, so the verdict was logged and
        // thrown away. Every component completed with a durable result and no message was ever
        // assembled — a total stall that looked, in the data, like a state machine working.
        await ExecuteInTransactionAsync(async () =>
        {
            await _context.SaveChangesAsync(ct);
            await ReevaluateCoreAsync(component.AssemblyId, businessUnitId, ct);
        }, ct);
    }

    /// <summary>
    /// Records what became of one component and re-evaluates the message.
    ///
    /// <para>Idempotent by construction: re-reporting the same outcome is a no-op, and a
    /// terminal component is never moved by a later report. That is what makes a replay after a
    /// crash safe — the worker cannot know whether its previous attempt committed.</para>
    /// </summary>
    public async Task RecordComponentOutcomeAsync(
        long businessUnitId, long assemblyId, string componentKey,
        EmailInquiryComponentStatus status,
        string? reasonCode, string? reasonDetail, long? sourceDocumentOccurrenceId,
        CancellationToken ct = default)
    {
        var component = await FindComponentAsync(businessUnitId, assemblyId, componentKey, ct);
        // Fail visibly. Logging and returning meant an extraction outcome could evaporate with
        // the message left mid-flight and nothing anywhere recording that it had happened.
        if (component is null)
            throw new InvalidOperationException(
                $"Component '{componentKey}' of assembly {assemblyId} was not found for business "
                + $"unit {businessUnitId}; its outcome could not be recorded.");

        // A refusal on security grounds is the one outcome allowed to overwrite a terminal
        // component: the scanner's verdict outranks a result produced before it was known.
        if (component.IsTerminal && status != EmailInquiryComponentStatus.RefusedSecurity)
            return;

        component.Status = status;
        component.ReasonCode = reasonCode;
        component.ReasonDetail = Truncate(reasonDetail, 1000);
        if (sourceDocumentOccurrenceId.HasValue)
            component.SourceDocumentOccurrenceId = sourceDocumentOccurrenceId;
        component.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // The component outcome and the message's re-evaluation are ONE unit of work.
        //
        // They used to be two independent saves. The loser of a concurrency race committed its
        // component outcome and then threw out of the re-evaluation, leaving a message with
        // every component terminal but the assembly never recomputed — permanently one step
        // short of ReadyForAssembly, with nothing sweeping it. The concurrency token turned a
        // silent lost update into a loud one and stopped there; this closes the other half.
        await ExecuteInTransactionAsync(async () =>
        {
            await _context.SaveChangesAsync(ct);
            await ReevaluateCoreAsync(component.AssemblyId, businessUnitId, ct);
        }, ct);
    }

    /// <summary>
    /// Runs a unit of work in a transaction, retrying once on a concurrency conflict with fresh
    /// state.
    ///
    /// <para>Retry is safe because the evaluation is a pure function of the component rows: after
    /// reloading, recomputing converges on the same answer. PostgreSQL raises the conflict two
    /// different ways depending on isolation level — <see cref="DbUpdateConcurrencyException"/>
    /// under read-committed, SQLSTATE 40001 under repeatable-read or serializable — so both are
    /// caught. Catching only the first would leave the stranded-message failure intact on any
    /// deployment that tightens isolation.</para>
    /// </summary>
    private async Task ExecuteInTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        const int MaxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (_context.Database.CurrentTransaction is not null)
                {
                    await work();
                    return;
                }

                await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                await work();
                await transaction.CommitAsync(ct);
                return;
            }
            catch (Exception exception) when (IsConcurrencyConflict(exception) && attempt < MaxAttempts)
            {
                // Discard the failed attempt's tracked state entirely. Re-saving an entry that
                // already failed would stamp it a second time and race the same stale original
                // value again, so the retry must start from the database.
                _context.ChangeTracker.Clear();
                _logger.LogInformation(
                    "Concurrency conflict on attempt {Attempt} while recording an email inquiry "
                    + "outcome; reloading and retrying.", attempt);
            }
        }
    }

    private static bool IsConcurrencyConflict(Exception exception)
        => exception is DbUpdateConcurrencyException
           || exception is Npgsql.PostgresException { SqlState: "40001" }
           || exception.InnerException is Npgsql.PostgresException { SqlState: "40001" };

    /// <summary>
    /// Re-reads every component of the message and applies the state machine's verdict.
    ///
    /// <para>Deliberately recomputed from the rows rather than accumulated in a counter: a
    /// counter can only be right if every increment happened exactly once, which is precisely
    /// what a crash, a retry or two concurrent workers cannot promise.</para>
    /// </summary>
    /// <summary>
    /// The write that closes the loop: the extraction output becomes durable, the component
    /// becomes Completed, and the message is re-evaluated — atomically.
    ///
    /// <para><b>Why atomic and not three calls.</b> Each pair of these has a failure mode that
    /// costs real money. Result without completion: the work is done and the barrier waits
    /// forever. Completion without result: the barrier proceeds and builds a Lead missing an
    /// attachment's lines, silently under-quoting the customer. Completion without
    /// re-evaluation: the last component finishes and nothing ever notices the message is
    /// ready — the failure this coordinator already had to fix once for the outcome path.</para>
    ///
    /// <para>The upsert is on the COMPONENT, not the job. A re-run under a new job id must
    /// replace that component's single answer rather than append a second one, or the barrier
    /// reads two contradictory results for one attachment and takes whichever the query
    /// ordering happened to return.</para>
    /// </summary>
    public async Task<EmailInquiryAssemblyEvaluation> RecordComponentResultAsync(
        long businessUnitId,
        long componentId,
        long extractionJobId,
        EmailInquiryComponentResultPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var component = await _context.EmailInquiryComponents
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == componentId, ct)
            ?? throw new InvalidOperationException(
                $"Email inquiry component {componentId} was not found for business unit "
                + $"{businessUnitId}; its extraction result has no owner to attach to.");

        EmailInquiryAssemblyEvaluation evaluation = default;
        await ExecuteInTransactionAsync(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await _context.Set<EmailInquiryComponentResult>()
                .FirstOrDefaultAsync(
                    x => x.BusinessUnitId == businessUnitId && x.ComponentId == componentId, ct);

            if (existing is null)
            {
                existing = new EmailInquiryComponentResult
                {
                    BusinessUnitId = businessUnitId,
                    AssemblyId = component.AssemblyId,
                    ComponentId = componentId,
                    CreatedAtUtc = now
                };
                _context.Set<EmailInquiryComponentResult>().Add(existing);
            }

            existing.ExtractionJobId = extractionJobId;
            existing.PayloadContractVersion = EmailInquiryComponentResult.CurrentPayloadContractVersion;
            existing.PayloadJson = payload.PayloadJson;
            existing.ProcessingPath = payload.ProcessingPath;
            existing.AiProviderClass = payload.AiProviderClass;
            existing.ModelIdentifier = payload.ModelIdentifier;
            existing.HeaderConfidence = payload.HeaderConfidence;
            existing.ExpectedItemCount = payload.ExpectedItemCount;
            existing.ExtractedItemCount = payload.ExtractedItemCount;
            existing.ReviewReason = Truncate(payload.ReviewReason, 1000);
            existing.DiagnosticsJson = payload.DiagnosticsJson;
            existing.UpdatedAtUtc = now;
            // Npgsql does not generate int concurrency tokens the way it generates xmin, so an
            // unincremented token stays 0 forever and every concurrent update matches — the
            // protection reads as present and does nothing.
            existing.ConcurrencyVersion++;

            // Only now is the component finished. A security refusal recorded in the meantime
            // outranks a result produced before the verdict was known, so it is not overwritten.
            if (!component.IsTerminal)
            {
                component.Status = EmailInquiryComponentStatus.Completed;
                component.ReasonCode = null;
                component.ReasonDetail = null;
                component.UpdatedAtUtc = now;
            }

            await _context.SaveChangesAsync(ct);
            evaluation = await ReevaluateCoreAsync(component.AssemblyId, businessUnitId, ct);
        }, ct);

        return evaluation;
    }

    public async Task<EmailInquiryAssemblyEvaluation> ReevaluateAsync(
        long assemblyId, long businessUnitId, CancellationToken ct = default)
    {
        EmailInquiryAssemblyEvaluation evaluated = default;
        await ExecuteInTransactionAsync(
            async () => evaluated = await ReevaluateCoreAsync(assemblyId, businessUnitId, ct), ct);
        return evaluated;
    }

    private async Task<EmailInquiryAssemblyEvaluation> ReevaluateCoreAsync(
        long assemblyId, long businessUnitId, CancellationToken ct)
    {
        var assembly = await _context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.Id == assemblyId && x.BusinessUnitId == businessUnitId, ct)
            ?? throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for this tenant.");

        var statuses = assembly.Components.Select(c => c.Status).ToList();
        var evaluation = EmailInquiryAssemblyStateMachine.Evaluate(
            assembly.ExpectedComponentCount, statuses);

        assembly.CompletedComponentCount = evaluation.CompletedComponentCount;

        // An illegal transition is a defect in the caller, but throwing here would strand the
        // message mid-pipeline with no record of why. It is logged loudly and the status is
        // left where it was, so the assembly stays visible and recoverable rather than lost.
        if (EmailInquiryAssemblyStateMachine.CanTransition(assembly.Status, evaluation.Status))
        {
            assembly.Status = evaluation.Status;
            assembly.StatusReason = Truncate(evaluation.Reason, 1000);
        }
        else
        {
            _logger.LogError(
                "Email inquiry assembly {AssemblyId} evaluated to {Evaluated} but is {Current}, "
                + "which is not a legal transition. The status is unchanged and the message is held.",
                assembly.Id, evaluation.Status, assembly.Status);
        }

        assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
        return evaluation;
    }

    public async Task<bool> DurableJobBelongsToComponentAsync(
        long businessUnitId, long extractionJobId, Guid expectedBatchId, string componentKey,
        CancellationToken ct = default)
    {
        var job = await _context.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == extractionJobId)
            .Select(x => new { x.BatchId, x.SourceDocumentOccurrenceId })
            .FirstOrDefaultAsync(ct);

        // Purged, or never committed by a pass that died after writing the id.
        if (job is null) return false;

        // A job from another message cannot match: the batch is derived from the assembly.
        if (job.BatchId != expectedBatchId) return false;

        // And within this message, it must be THIS component's occurrence. The occurrence key is
        // rebuilt from the component's persisted key, so a sibling attachment's job is refused.
        if (job.SourceDocumentOccurrenceId is not { } occurrenceId) return false;

        var expectedOccurrenceKey = ERP_RFQ_Automation.Extraction.SourceOccurrenceIdentity.BuildKey(
            expectedBatchId,
            ERP_RFQ_Automation.Extraction.ExtractionSourceType.Email,
            new ERP_RFQ_Automation.Extraction.ExtractionJobMetadata { SourceOccurrenceId = componentKey });

        return await _context.Set<SourceDocumentOccurrence>()
            .AsNoTracking()
            .AnyAsync(x => x.BusinessUnitId == businessUnitId
                           && x.Id == occurrenceId
                           && x.IdempotencyKey == expectedOccurrenceKey, ct);
    }

    public async Task HoldForReviewAsync(
        long businessUnitId, long assemblyId, string reasonCode, string reasonDetail,
        CancellationToken ct = default)
    {
        await ExecuteInTransactionAsync(async () =>
        {
            var assembly = await _context.EmailInquiryAssemblies
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId, ct)
                ?? throw new InvalidOperationException(
                    $"Email inquiry assembly {assemblyId} was not found for this tenant.");

            if (!EmailInquiryAssemblyStateMachine.CanTransition(
                    assembly.Status, EmailInquiryAssemblyStatus.NeedsReview))
            {
                // Loud, not fatal. Throwing would strand the message with no record of why;
                // leaving it where it is keeps it visible and recoverable.
                _logger.LogError(
                    "Assembly {AssemblyId} is {Status}, which cannot move to NeedsReview ({Reason}).",
                    assemblyId, assembly.Status, reasonCode);
                return;
            }

            assembly.Status = EmailInquiryAssemblyStatus.NeedsReview;
            assembly.StatusReason = Truncate($"{reasonCode}: {reasonDetail}", 1000);
            assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task MarkAssembledAsync(
        long businessUnitId, long assemblyId, CancellationToken ct = default)
    {
        await ExecuteInTransactionAsync(async () =>
        {
            var assembly = await _context.EmailInquiryAssemblies
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId, ct)
                ?? throw new InvalidOperationException(
                    $"Email inquiry assembly {assemblyId} was not found for this tenant.");

            // Idempotent: a re-entrant assemble that lost the race finds the message already
            // Assembled and does nothing, rather than failing a transition to its own state.
            if (assembly.Status == EmailInquiryAssemblyStatus.Assembled) return;

            if (!EmailInquiryAssemblyStateMachine.CanTransition(
                    assembly.Status, EmailInquiryAssemblyStatus.Assembled))
            {
                _logger.LogError(
                    "Assembly {AssemblyId} is {Status}, which cannot move to Assembled.",
                    assemblyId, assembly.Status);
                return;
            }

            assembly.Status = EmailInquiryAssemblyStatus.Assembled;
            assembly.StatusReason = null;
            assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(ct);
        }, ct);
    }

    public async Task MarkNoInquiryAsync(
        EmailInquiryAssembly assembly, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!EmailInquiryAssemblyStateMachine.CanTransition(
                assembly.Status, EmailInquiryAssemblyStatus.NoInquiry))
            return;

        assembly.Status = EmailInquiryAssemblyStatus.NoInquiry;
        assembly.StatusReason = Truncate(reason, 1000);
        assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
