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
        CancellationToken ct = default, string? evidenceUri = null,
        long? sourceDocumentOccurrenceId = null);

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

    /// <summary>
    /// Marks a message assembled and records the ONE Lead it became.
    ///
    /// <para><paramref name="leadId"/> must be positive. Assembled is a claim that a specific
    /// Lead exists; "no Lead was produced" is a different outcome and belongs in
    /// <see cref="HoldForReviewAsync"/> or <see cref="MarkNoInquiryAsync"/>, never here.</para>
    /// </summary>
    Task MarkAssembledAsync(
        long businessUnitId, long assemblyId, long leadId, CancellationToken ct = default);

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
            .Include(x => x.Assembly)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                                       && x.AssemblyId == assemblyId
                                       && x.ComponentKey == componentKey, ct)!;

    public async Task RecordComponentQueuedAsync(
        long businessUnitId, long assemblyId, string componentKey, long extractionJobId,
        CancellationToken ct = default, string? evidenceUri = null,
        long? sourceDocumentOccurrenceId = null)
    {
        await ExecuteInTransactionAsync(async () =>
        {
            var component = await FindComponentAsync(businessUnitId, assemblyId, componentKey, ct);
            // A missing component is an ownership failure, not a no-op. Returning silently is how a
            // scheduled job ends up owned by nobody and its message waits at the barrier forever.
            if (component is null)
                throw new InvalidOperationException(
                    $"Component '{componentKey}' of assembly {assemblyId} was not found for "
                    + $"business unit {businessUnitId}; the job binding has no owner.");

            var automaticRecovery = component.Status == EmailInquiryComponentStatus.FailedRecoverable
                && component.ExtractionJobId == null;
            if (automaticRecovery && !EmailInquiryAssemblyStateMachine
                    .CanAutomaticSchedulingRecoveryTransition(component.Assembly.Status))
                throw new InvalidOperationException(
                    $"Component '{componentKey}' cannot automatically resume while assembly "
                    + $"{assemblyId} is {component.Assembly.Status}.");

            component.ExtractionJobId = extractionJobId;
            // Bind the exact immutable object and occurrence at the same moment as its job.
            // Without this, the component claims to carry field-level provenance while its
            // EvidenceUri remains null even after successful ingestion.
            if (!string.IsNullOrWhiteSpace(evidenceUri)) component.EvidenceUri = evidenceUri;
            if (sourceDocumentOccurrenceId.HasValue)
                component.SourceDocumentOccurrenceId = sourceDocumentOccurrenceId;
            // Pending -> Extracting only. A component that already reached a terminal state is left
            // alone: a replayed enqueue must not walk a finished part backwards and reopen a
            // barrier that has already been satisfied.
            if (component.Status == EmailInquiryComponentStatus.Pending)
                component.Status = EmailInquiryComponentStatus.Extracting;
            else if (automaticRecovery)
            {
                component.Status = EmailInquiryComponentStatus.Extracting;
                component.ReasonCode = null;
                component.ReasonDetail = null;
                component.Assembly.Status = EmailInquiryAssemblyStatus.Extracting;
                component.Assembly.StatusReason =
                    "A previously unscheduled component was durably bound to a processing job.";
                component.Assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            component.UpdatedAtUtc = DateTimeOffset.UtcNow;

            // The MESSAGE moves too, in the same transaction.
            //
            // This used to save the component and stop. The assembly therefore stayed at Captured
            // for its entire life, and when the barrier finally evaluated ReadyForAssembly the
            // transition Captured -> ReadyForAssembly was illegal, so the verdict was logged and
            // thrown away. Every component completed with a durable result and no message was ever
            // assembled — a total stall that looked, in the data, like a state machine working.
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
        // The component and the assembly are ONE unit of work, and the load belongs inside it
        // so a retry re-reads both from the database rather than replaying attempt one's
        // in-memory state (see ExecuteInTransactionAsync).
        await ExecuteInTransactionAsync(async () =>
        {
            var component = await FindComponentAsync(businessUnitId, assemblyId, componentKey, ct);
            // Fail visibly. Logging and returning meant an extraction outcome could evaporate with
            // the message left mid-flight and nothing anywhere recording that it had happened.
            if (component is null)
                throw new InvalidOperationException(
                    $"Component '{componentKey}' of assembly {assemblyId} was not found for "
                    + $"business unit {businessUnitId}; its outcome could not be recorded.");

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
            // They used to be two independent saves. The loser of a concurrency race committed
            // its component outcome and then threw out of the re-evaluation, leaving a message
            // with every component terminal but the assembly never recomputed — permanently one
            // step short of ReadyForAssembly, with nothing sweeping it.
            await _context.SaveChangesAsync(ct);
            await ReevaluateCoreAsync(component.AssemblyId, businessUnitId, ct);
        }, ct);
    }

    /// <summary>
    /// Runs a unit of work in a transaction, retrying once on a concurrency conflict with fresh
    /// state.
    ///
    /// <para><b>Every entity the work touches MUST be loaded INSIDE <paramref name="work"/>.</b>
    /// The retry clears the change tracker, so an entity captured from an enclosing scope is
    /// detached on the second attempt — and, worse, still carries attempt one's mutations, so a
    /// guard like <c>if (!component.IsTerminal)</c> reads the in-memory value it already set and
    /// skips the write entirely. The observable result is a durable result row with its component
    /// still Extracting and the message stalled at the barrier for good: exactly the stranded
    /// message the concurrency stamp exists to prevent, reintroduced by the mechanism meant to
    /// recover from it.</para>
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

        // JOINING a caller's transaction. Do the work and let the OWNER decide about retries.
        //
        // Retrying here was wrong in two ways. A 40001 has already ABORTED the PostgreSQL
        // transaction, and nothing here rolled it back or opened a new one because it is not
        // ours — so every statement of attempt two fails 25P02 and the real conflict is masked
        // by a "current transaction is aborted" error. And ChangeTracker.Clear() would detach
        // entities belonging to the caller's unit of work: PersistAndCompleteCoreAsync snapshots
        // its tracked Leads around this call, and a nested Clear silently empties that snapshot.
        if (_context.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        // WE own it — and a user-initiated transaction is illegal under the retrying execution
        // strategy production configures unless the whole unit runs inside ExecuteAsync. Without
        // this, every non-ambient call throws
        // "NpgsqlRetryingExecutionStrategy does not support user-initiated transactions"
        // before doing anything: the Lead would be created and the message never marked
        // Assembled — the stranded message this class exists to prevent, one layer up.
        //
        // The repository has paid for this before; GeneralLedgerService carries the same note
        // after it made every ledger write throw against PostgreSQL.
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                    await work();
                    await transaction.CommitAsync(ct);
                    return;
                }
                catch (Exception exception) when (IsConcurrencyConflict(exception) && attempt < MaxAttempts)
                {
                    // The transaction is rolled back by the using above, so the retry genuinely
                    // does start from the database. Clearing the tracker is what makes that true
                    // for the entities as well — every one of them is re-read inside work().
                    _context.ChangeTracker.Clear();
                    _logger.LogInformation(
                        "Concurrency conflict on attempt {Attempt} while recording an email "
                        + "inquiry outcome; reloading and retrying.", attempt);
                }
            }
        });
    }

    /// <summary>
    /// 40001 is the serialization failure; 23505 is the unique-violation two writers produce when
    /// both read "no result row yet" and both insert. The second is just as retriable — the retry
    /// re-reads and takes the update branch — and leaving it unclassified turned an ordinary race
    /// into a propagating exception that doomed the caller's transaction and dead-lettered a job
    /// for a condition this method believes it handles.
    /// </summary>
    private static bool IsConcurrencyConflict(Exception exception)
        => exception is DbUpdateConcurrencyException
           || exception is Npgsql.PostgresException { SqlState: "40001" or "23505" }
           || exception.InnerException is Npgsql.PostgresException { SqlState: "40001" or "23505" };

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

        EmailInquiryAssemblyEvaluation evaluation = default;
        await ExecuteInTransactionAsync(async () =>
        {
            // Loaded INSIDE the retried work. Hoisting this out left the entity detached on a
            // retry with Status already set to Completed in memory, so the guard below saw a
            // terminal component and skipped the write — committing the result while leaving the
            // component Extracting and the message stalled for good.
            var component = await _context.EmailInquiryComponents
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == componentId, ct)
                ?? throw new InvalidOperationException(
                    $"Email inquiry component {componentId} was not found for business unit "
                    + $"{businessUnitId}; its extraction result has no owner to attach to.");

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
            // The token is incremented by EmailInquiryConcurrencyStamp in SaveChanges, NOT here.
            // Its own rationale is that a call site which forgets is exactly how the token became
            // inert in the first place, and this table was the only one in the aggregate
            // incrementing at the call site.

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
        // Serialize the decision on the assembly row BEFORE reading the components.
        //
        // Two workers finishing the last two components each read a READ COMMITTED snapshot in
        // which the other's component is still Extracting, so both compute "not ready" and the
        // message is one step short with nothing sweeping it. Optimistic concurrency turned that
        // into a retry rather than a fix, which made correctness depend on the retry machinery
        // instead of on a lock. Taking the row lock first makes the second worker read the
        // first's committed component, and works identically whether or not we own the
        // transaction.
        await _context.Database.ExecuteSqlAsync(
            $"""
            SELECT 1 FROM public."EmailInquiryAssemblies"
            WHERE "BusinessUnitId" = {businessUnitId} AND "Id" = {assemblyId}
            FOR UPDATE
            """, ct);

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
        long businessUnitId, long assemblyId, long leadId, CancellationToken ct = default)
    {
        // Assembled MEANS "this message became that Lead". Without an id there is no such claim
        // to make, and a message was recorded Assembled with AssembledLeadId = 0 because this
        // method wrote whatever it was handed. Refused here as well as at the caller, because the
        // guarantee belongs to the state — any future caller inherits it, and a caller that hits
        // this has a defect rather than a message to hold. Checked before the transaction opens:
        // there is nothing to roll back and no reason to take a row lock to say no.
        if (leadId <= 0)
            throw new ArgumentOutOfRangeException(nameof(leadId), leadId,
                $"Email inquiry assembly {assemblyId} cannot be marked assembled without a lead. "
                + "A non-positive id means no lead was produced; hold the message for review "
                + "instead.");

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
            assembly.AssembledLeadId = leadId;
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

        // A triage-time NoInquiry happens immediately after capture, while every processable
        // component is still Pending. Leaving those rows non-terminal makes the stranded-work
        // sweep later treat an intentionally rejected message as a scheduling crash. Close only
        // components that have never been job-bound; completed extraction evidence remains
        // untouched when the assembler concludes that extracted content was non-commercial.
        var untouched = await _context.EmailInquiryComponents
            .Where(x => x.BusinessUnitId == assembly.BusinessUnitId
                        && x.AssemblyId == assembly.Id
                        && x.Status == EmailInquiryComponentStatus.Pending
                        && x.ExtractionJobId == null)
            .ToListAsync(ct);
        foreach (var component in untouched)
        {
            component.Status = EmailInquiryComponentStatus.Ignored;
            component.ReasonCode = "no_inquiry";
            component.ReasonDetail = Truncate(reason, 1000);
            component.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        assembly.Status = EmailInquiryAssemblyStatus.NoInquiry;
        assembly.StatusReason = Truncate(reason, 1000);
        assembly.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
