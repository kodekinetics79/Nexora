using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

public interface IEmailInquiryLeadAssembler
{
    /// <summary>
    /// Builds the ONE Lead for a message whose components have all finished.
    ///
    /// <para>Returns the Lead id if and ONLY if THIS call produced it. Null means this call
    /// assembled nothing — not ready, nothing to quote, held for review, already assembled by
    /// someone else, or another worker won the claim. A null is not a failure and never means
    /// "no Lead exists"; the caller re-reads the persisted status to learn which it was.</para>
    ///
    /// <para>The asymmetry matters more than it looks. Returning the winner's id to a caller
    /// that did nothing is true and useless: every counter downstream then records a recovery
    /// per caller rather than per Lead, "a Lead exists" becomes indistinguishable from "I built
    /// it", and the already-complete bucket becomes unreachable — so an operator watching the
    /// recovery rate sees a number that means nothing.</para>
    /// </summary>
    Task<long?> AssembleAsync(long businessUnitId, long assemblyId, CancellationToken ct = default);
}

/// <summary>
/// The barrier's payoff: every finished part of one message becomes one Lead.
///
/// <para><b>The defect this removes.</b> The legacy path enqueued one job per attachment and
/// each job created its own Lead. A buyer who sent a covering note, a valve schedule and a
/// gasket schedule became three Leads — three quotes, three follow-ups, and a very good chance
/// that whichever one a salesperson opened was priced from a third of the request. Nothing in
/// the data said the three belonged together.</para>
///
/// <para><b>Why it merges from the result store rather than from whatever finished last.</b>
/// Components complete at wildly different times: a CSV is deterministic and returns in
/// milliseconds, a scanned PDF goes through OCR and a model. Assembling from the in-flight
/// result would build the Lead from the fast parts. Every part's result is durable before this
/// runs, so the merge sees the whole message or it does not run at all.</para>
/// </summary>
public sealed class EmailInquiryLeadAssembler : IEmailInquiryLeadAssembler
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILeadPersister _persister;
    private readonly IEmailInquiryAssemblyCoordinator _coordinator;
    private readonly ILogger<EmailInquiryLeadAssembler> _log;

    public EmailInquiryLeadAssembler(
        ErpRfqAutomationContext context,
        ILeadPersister persister,
        IEmailInquiryAssemblyCoordinator coordinator,
        ILogger<EmailInquiryLeadAssembler> log)
    {
        _context = context;
        _persister = persister;
        _coordinator = coordinator;
        _log = log;
    }

    public Task<long?> AssembleAsync(
        long businessUnitId, long assemblyId, CancellationToken ct = default)
    {
        // A user-initiated transaction is illegal under the retrying execution strategy that
        // production configures unless the whole unit runs inside ExecuteAsync. Skipping this
        // made AssembleAsync throw on the first real message while passing every local test,
        // because the test host had not configured EnableRetryOnFailure.
        // THIS METHOD OWNS ITS TRANSACTION. Refusing an ambient one is the contract, not a
        // limitation: the claim path clears the change tracker, which on a shared scoped
        // DbContext would silently detach entities belonging to the caller's unit of work —
        // the coordinator documents the same hazard after PersistAndCompleteCoreAsync's
        // tracked-Lead snapshot was emptied by exactly that. It would also put the Lead build
        // and the enrichment inside the caller's lock. Loud beats latent.
        if (_context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "EmailInquiryLeadAssembler.AssembleAsync owns its own transaction and must not "
                + "be called inside one. Call it after the caller's transaction has committed.");

        return AssembleAndEnrichAsync(businessUnitId, assemblyId, ct);
    }

    private async Task<long?> AssembleAndEnrichAsync(
        long businessUnitId, long assemblyId, CancellationToken ct)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        var built = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);
            var outcome = await AssembleCoreAsync(businessUnitId, assemblyId, ct);
            // Committed on EVERY path, not only the success one: the hold-for-review writes are
            // the whole reason a refusal is visible rather than a silent stall, and disposing an
            // uncommitted transaction would roll each of them straight back.
            await transaction.CommitAsync(ct);
            return outcome;
        });

        // OUTSIDE the transaction, so the assembly's claim lock is released FIRST.
        //
        // Held across this, a live worker finishing the last component of the same message would
        // block on that lock while holding its queue lease — and a slow identity reconciliation
        // or routing call would cost the lease, an attempt, and a re-extraction. Best-effort by
        // contract: the Lead already exists and a failure here must not undo it.
        if (built.LeadId is { } leadId && built.AnchorJob is { } anchorJob)
        {
            try
            {
                await _persister.EnrichAssembledMessageAsync(anchorJob, leadId, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log.LogError(exception,
                    "Assembly {AssemblyId} produced lead {LeadId} but enrichment failed. The "
                    + "lead stands; routing and customer resolution can be replayed.",
                    assemblyId, leadId);
            }
        }

        return built.LeadId;
    }

    private sealed record AssembleOutcome(long? LeadId, ExtractionJob? AnchorJob);

    private async Task<AssembleOutcome> AssembleCoreAsync(
        long businessUnitId, long assemblyId, CancellationToken ct)
    {
        // The assembly row is LOCKED for the whole of this method.
        //
        //
        // Reading the status and acting on it were previously two separate steps, which is a
        // check-then-act race with real money on it: two workers finishing the last two
        // components both observe ReadyForAssembly, both merge, and both persist. Nothing in
        // this class stopped that — the only thing preventing two Lead rows lived three layers
        // away in the identity service's idempotency key, which is not guaranteed to be
        // configured and not guaranteed to agree between the racers.
        //
        // The lock makes the loser wait, and the transaction makes the Lead and the Assembled
        // transition commit together — so a crash between them cannot leave a Lead that no
        // message points at.
        // THE CLAIM. A compare-and-swap on the assembly row, not a check-then-act.
        //
        // Two earlier attempts at this were wrong in instructive ways. Reading the status and
        // then acting on it is a plain TOCTOU: two recovery instances both read
        // ReadyForAssembly and both build a Lead. Taking SELECT ... FOR UPDATE through EF looked
        // like the fix and was not — it did not serialize two concurrent sweeps in practice, and
        // an applied test showed both of them still producing a Lead.
        //
        // A conditional UPDATE has no such ambiguity. It takes the row's write lock, so the
        // loser BLOCKS until the winner commits and then re-evaluates its WHERE against the
        // committed row, where ConcurrencyVersion no longer matches and it claims nothing. The
        // lock is then held for the rest of this transaction, which is exactly as long as the
        // Lead takes to write.
        var claimed = await _context.EmailInquiryAssemblies
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId)
            .Select(x => new { x.Status, x.ConcurrencyVersion, x.AssembledLeadId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for business unit {businessUnitId}.");

        // Already done, by someone else, before this call began. That is not a failure and not
        // this call's recovery: null, and the caller re-reads the status to see Assembled.
        if (claimed.Status == EmailInquiryAssemblyStatus.Assembled)
        {
            _log.LogDebug(
                "Assembly {AssemblyId} is already assembled as lead {LeadId}; nothing to do.",
                assemblyId, claimed.AssembledLeadId);
            return new AssembleOutcome(null, null);
        }

        if (claimed.Status != EmailInquiryAssemblyStatus.ReadyForAssembly)
        {
            _log.LogDebug(
                "Assembly {AssemblyId} is {Status}; not assembling.", assemblyId, claimed.Status);
            return new AssembleOutcome(null, null);
        }

        // Bounded wait. The claim legitimately blocks on a conflicting holder, but an
        // unbounded block inherits the 60s command timeout, and the sweep is sequential across
        // assemblies AND tenants — one wedged row would stall every other tenant on the
        // platform for a minute at a time. A timeout here means "someone else has it", which is
        // the same disposition as losing the compare-and-swap.
        await _context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", ct);

        var seenVersion = claimed.ConcurrencyVersion;
        var readyForAssembly = nameof(EmailInquiryAssemblyStatus.ReadyForAssembly);
        int rows;
        try
        {
            rows = await _context.Database.ExecuteSqlAsync(
                $"""
                UPDATE public."EmailInquiryAssemblies"
                SET "ConcurrencyVersion" = "ConcurrencyVersion" + 1, "UpdatedAtUtc" = now()
                WHERE "Id" = {assemblyId}
                  AND "BusinessUnitId" = {businessUnitId}
                  AND "Status" = {readyForAssembly}
                  AND "AssembledLeadId" IS NULL
                  AND "ConcurrencyVersion" = {seenVersion}
                """, ct);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == "55P03")
        {
            // lock_not_available. Another holder has the row; the next sweep tries again.
            _log.LogInformation(
                "Assembly {AssemblyId} is locked by another worker; leaving it for the next pass.",
                assemblyId);
            return new AssembleOutcome(null, null);
        }

        if (rows == 0)
        {
            // Someone else won the claim, so THIS call assembled nothing and must say so.
            //
            // Returning the winner's lead id here would be true and useless: a caller counting
            // outcomes would record two recoveries for one Lead, and "a Lead exists" would
            // become indistinguishable from "I built it" in every metric and log downstream.
            // Null means "not by me"; the caller re-reads the persisted status to learn what
            // actually became of the message.
            var settled = await _context.EmailInquiryAssemblies
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId)
                .Select(x => x.Status)
                .FirstOrDefaultAsync(ct);

            // A contradiction, not a race: nobody claimed it, yet the compare-and-swap matched
            // nothing. The likeliest cause is that the row is invisible to this connection —
            // an unset tenant GUC, or a status spelling the enum no longer produces — and
            // logging "another worker took it" would be actively misleading while the message
            // is never recovered.
            if (settled == EmailInquiryAssemblyStatus.ReadyForAssembly)
                throw new InvalidOperationException(
                    $"Assembly {assemblyId} is still ReadyForAssembly but the claim matched no "
                    + "rows. The row is not visible to this connection, or its stored status "
                    + "does not match the current enum.");

            _log.LogInformation(
                "Assembly {AssemblyId} was claimed by another worker; it is now {Status}.",
                assemblyId, settled);
            return new AssembleOutcome(null, null);
        }

        // Cleared so nothing below is answered from state read before the claim.
        _context.ChangeTracker.Clear();

        var assembly = await _context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId, ct)
            ?? throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for business unit {businessUnitId}.");

        var results = await _context.Set<EmailInquiryComponentResult>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.AssemblyId == assemblyId)
            .ToListAsync(ct);

        // A payload written under a contract this build does not understand is REFUSED, never
        // coerced. Guessing at an older shape is how a quantity silently becomes a unit price.
        var unreadable = results
            .Where(r => r.PayloadContractVersion != EmailInquiryComponentResult.CurrentPayloadContractVersion)
            .ToList();
        if (unreadable.Count > 0)
        {
            _log.LogError(
                "Assembly {AssemblyId} has {Count} component result(s) written under an "
                + "unsupported payload contract version; the message is held for review.",
                assemblyId, unreadable.Count);
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId, EmailInquiryHoldReasons.ResultContractUnsupported,
                "This message was processed by an earlier version of the system and cannot be "
                + "combined automatically. It needs a look before it becomes an inquiry.", ct);
            return new AssembleOutcome(null, null);
        }

        // THE UNDER-QUOTE GUARD. Every Completed component must have contributed a result.
        //
        // The invariant holds today only because the result and the completion are written in
        // one transaction — but RecordComponentOutcomeAsync is a public method that accepts
        // Completed, and one such call would produce a Lead silently missing an attachment's
        // priced lines. That is the exact commercial defect this whole module exists to prevent,
        // so it is checked rather than assumed.
        var completedCount = assembly.Components
            .Count(c => c.Status == EmailInquiryComponentStatus.Completed);
        if (results.Count != completedCount)
        {
            _log.LogError(
                "Assembly {AssemblyId} has {Results} result(s) for {Completed} completed "
                + "component(s); refusing to build a Lead from an incomplete message.",
                assemblyId, results.Count, completedCount);
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId, EmailInquiryHoldReasons.ResultMissing,
                "Part of this message finished without recording what was read, so the inquiry "
                + "would be incomplete. It needs a look before it becomes an inquiry.", ct);
            return new AssembleOutcome(null, null);
        }

        // Ordinal order, so the body's header fields are considered before an attachment's and
        // the merged Lead reads the way the sender wrote it.
        var byComponent = assembly.Components.ToDictionary(c => c.Id);
        var ordered = results
            .OrderBy(r => byComponent.TryGetValue(r.ComponentId, out var c) ? c.Ordinal : int.MaxValue)
            .ToList();

        var merged = new List<LeadItemData>();
        var componentLines = new List<EmailInquiryCommercialConflictDetector.ComponentLines>();
        LeadExtractionResult? header = null;
        var expected = 0;
        var extracted = 0;
        var reviewReasons = new List<string>();
        // Whether EVERY part of this message was read to the end and named nothing. Accumulated
        // here rather than inferred afterwards from `expected`, because `expected` cannot answer
        // it — see the zero-line gate below.
        var readInFull = true;
        // Provenance is MERGED, not invented. Hardcoding a deterministic path made every
        // assembled Lead read as external-AI-derived downstream — which is what the identity
        // ledger and the Trust Center report to the customer — even when every component was a
        // deterministic CSV parse. External wins if ANY component used an external model,
        // because that is the claim that has to be defensible.
        var providerClasses = new HashSet<string>(StringComparer.Ordinal);
        var processingPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in ordered)
        {
            LeadExtractionResult? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<LeadExtractionResult>(result.PayloadJson);
            }
            catch (JsonException exception)
            {
                // Stored, versioned, and still unreadable. That is corruption, not a version
                // skew, and it must not be rounded down to "this part had no lines".
                _log.LogError(exception,
                    "Component result {ResultId} of assembly {AssemblyId} could not be read.",
                    result.Id, assemblyId);
                await _coordinator.HoldForReviewAsync(
                    businessUnitId, assemblyId, EmailInquiryHoldReasons.ResultUnreadable,
                    "One part of this message could not be read back, so the inquiry is not "
                    + "complete. It needs a look.", ct);
                return new AssembleOutcome(null, null);
            }

            if (parsed is null)
            {
                // Stored, versioned, deserialized to nothing. Its counts are not read below, so
                // it must not be allowed to look like a part that was read and found empty.
                readInFull = false;
                continue;
            }

            header ??= parsed;
            if (parsed.Items is { Count: > 0 })
            {
                merged.AddRange(parsed.Items);
                componentLines.Add(new EmailInquiryCommercialConflictDetector.ComponentLines(
                    result.ComponentId, parsed.Items));
            }
            expected += result.ExpectedItemCount;
            extracted += result.ExtractedItemCount;
            if (!string.IsNullOrWhiteSpace(result.AiProviderClass))
                providerClasses.Add(result.AiProviderClass!);
            processingPaths.Add(result.ProcessingPath);
            if (!string.IsNullOrWhiteSpace(result.ReviewReason))
                reviewReasons.Add(result.ReviewReason!);
            if (!IsCompleteAndEmptyRead(result)) readInFull = false;
        }

        if (header is null)
        {
            // Held, not silently abandoned. Returning null here left the message at
            // ReadyForAssembly with no Lead, no reason and nothing that would ever look at it
            // again — invisible to the customer and to the operator alike.
            _log.LogError(
                "Assembly {AssemblyId} has result rows but none of them parsed to a usable "
                + "extraction; the message is held for review.", assemblyId);
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId, EmailInquiryHoldReasons.ResultUnreadable,
                "This message was processed but nothing usable could be read back from it. "
                + "It needs a look.", ct);
            return new AssembleOutcome(null, null);
        }

        // THE ZERO-LINE GATE. A message that names nothing quotable is not a Lead.
        //
        // Every part of this message reached a terminal state, at least one of them parsed into
        // a usable extraction, and between them they name ZERO requestable lines. Until now the
        // merge simply did not look: `merged` stayed empty, the outcome below was built with a
        // hard-coded Ok, and the persister minted a Lead with no lines and no client. That is
        // where cold outreach lands — "Contact Us: Digital Marketing", "Communication on the
        // ... solicitation" — and the lead list, which is the sales pipeline, was carrying
        // agency mail as work.
        //
        // WHAT THIS DEFECT IS NOT — AND AN EARLIER DRAFT OF THIS COMMENT SAID OTHERWISE.
        // It claimed as fact that this is why 19 of 22 leads on the live tenant have no client.
        // It is not. The client backfill over that tenant reported examined=22, autoMatched=3,
        // unresolved=19, failed=0 — nothing errored; no customer record matched those senders.
        // Nor could an empty item list have caused it: resolution builds its evidence from the
        // sender address and the extraction header (LeadCustomerResolutionService
        // .BuildEvidenceAsync), and the only item-derived signal it reads is a customer account
        // reference printed on a line, which none of this mail carries either way. Empty leads
        // are the defect being fixed here; unresolved clients are a separate, ordinary state.
        //
        // WHY THE JUDGEMENT BELONGS HERE AND NOWHERE EARLIER. A header rule cannot see this
        // class: a human wrote it to a human, so there is no List-Unsubscribe, no
        // Auto-Submitted, no bulk Precedence. The obvious alternative — stop an Uncertain
        // message that carries no commercial vocabulary — is the gate DeterministicEmailTriage
        // was rewritten to remove, and for a reason that has not changed: "Do you carry
        // Schneider NSX250N MCCBs? We are building a plant in Dammam" has no quantity, no
        // request verb, no RFQ reference and no attachment, and it is a real deal. Absence of
        // keywords is not evidence. The extractor's verdict IS evidence, and it is already
        // computed — ConversationalExtractionService records "No requestable items found in
        // message body" and sets NeedsReview, and this method was throwing that away.
        //
        // WHY HELD AND NOT CLOSED. Zero lines does not prove nothing was asked. A BidNet Direct
        // or SAM.gov solicitation states the requirement in a portal behind a link and extracts
        // to zero lines every time; it is a bid opportunity, and it triages identically to the
        // marketing mail (Uncertain / no_signal), so no triage-side discriminator can separate
        // the two. NeedsReview costs a human a glance and can still become either.
        //
        // NoInquiry — the terminal "this carried nothing to quote" — is the disposition this
        // branch WANTS and cannot yet use, and the blocker is not the state machine. That door
        // is open (EmailInquiryAssemblyStateMachine allows ReadyForAssembly -> NoInquiry, and
        // CanGovernedTriageReopenTransition accepts NoInquiry). The blocker is that reopening it
        // does not work for a message that got this far: EmailTriageService.GovernedReopenAsync
        // only puts back components that are Ignored/no_inquiry or job-less FailedRecoverable,
        // and every part of a message that reached this method is Completed and job-bound — so
        // the reopen throws, and the Inbound Mail screen offers the button anyway
        // (describeReopenAbility returns canReopen for NoInquiry). Closing here would therefore
        // hand the customer a one-way door with a control on it that answers 422. Even with the
        // reopen widened, EmailIngestEnqueuer counts a component whose durable job still exists
        // as alreadyScheduled, so the re-opened part would sit Pending with nothing to claim it.
        // Reversible first, terminal second.
        //
        // A REAL FIRST-TIME BUYER IS UNREACHABLE FROM HERE. One extracted line anywhere in the
        // message — body or attachment — makes merged non-empty and this branch dead. That is
        // the property that matters most in this change and it holds by construction, not by
        // keyword tuning.
        //
        // WHAT THE OPERATOR IS TOLD SPLITS ON WHETHER THE MESSAGE WAS READ IN FULL, BECAUSE
        // ZERO LINES HAS TWO CAUSES AND ONLY ONE OF THEM IS ABOUT THE SENDER.
        //
        //   read in full — every part reached the end of its source and named nothing that even
        //     resembled a requestable line. That is the marketing case, and "read in full, asked
        //     for nothing" is a true sentence to put in front of an operator.
        //
        //   not read in full — there WAS content that could have carried the request and none of
        //     it survived into an inquiry. A scanned RFQ PDF whose OCR came back partial is the
        //     case that matters: ChunkedExtractionService emits NeedsReview with a non-null
        //     result and zero items, and the worker does not divert a NeedsReview outcome — it
        //     records it and this method runs. Telling that operator the sender asked for nothing
        //     is false, and false about a real customer RFQ: they would chase a buyer who did
        //     their part instead of re-reading the document.
        //
        // THIS USED TO SPLIT ON `expected > 0` AND THAT TEST IS WRONG, in the dangerous
        // direction, on a shape production really emits. `expected` means two different things
        // on the two document paths. When the parser finds line-item regions, it is the region
        // count and a partial-OCR scan does have a positive one. When it finds NONE — which is
        // the normal outcome for a scan whose OCR degraded — ChunkedExtractionService takes the
        // whole-document branch and sets ExpectedItemCount to the MODEL'S OWN ITEM COUNT, which
        // for a document it could not read is zero. So the exact message this split exists to
        // protect, a customer's scanned RFQ, landed on the marketing sentence.
        //
        // The replacement asks for POSITIVE evidence instead of inferring from a count: a part
        // counts as read in full only when its extractor said so. Anything else — an incomplete
        // OCR, a failed chunk, a truncated body, a confidence too low to stand on, a payload
        // that deserialized to nothing, or any review sentence this build does not recognise —
        // is NOT read in full. The default is the cautious sentence, so a future extractor that
        // invents a new reason degrades to "we could not recover it" rather than to "you asked
        // for nothing".
        //
        // The disposition is the same HOLD either way — zero lines still proves nothing about
        // intent — and only the sentence differs, because the sentence is the whole of what
        // decides which of those two things the operator does next. The extractors' own review
        // reasons go to the LOG rather than into the sentence: they are unbounded, and the
        // operator screen REJECTS a reason over 300 characters outright (EmailInquiryHoldReasons).
        if (merged.Count == 0)
        {
            _log.LogInformation(
                "Assembly {AssemblyId} for business unit {BusinessUnitId} was read "
                + "({Results} component result(s)), saw {Expected} candidate line(s), read in "
                + "full = {ReadInFull}, and names no requestable line; it is held for review as "
                + "{Reason} rather than becoming an empty lead. Extractor review reason(s): "
                + "{ReviewReasons}",
                assemblyId, businessUnitId, ordered.Count, expected, readInFull,
                readInFull
                    ? EmailInquiryHoldReasons.NoRequestableContent
                    : EmailInquiryHoldReasons.ContentNotRecovered,
                reviewReasons.Count > 0 ? string.Join("; ", reviewReasons) : "(none reported)");
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId,
                readInFull
                    ? EmailInquiryHoldReasons.NoRequestableContent
                    : EmailInquiryHoldReasons.ContentNotRecovered,
                readInFull
                    ? EmailInquiryHoldReasons.NoRequestableContentDetail
                    : EmailInquiryHoldReasons.ContentNotRecoveredDetail, ct);
            return new AssembleOutcome(null, null);
        }

        // Body and attachment are peer evidence. If both name the same stable item identity but
        // disagree on a quote-critical value, creating two ordinary quoteable lines hides the
        // contradiction and invites a double/incorrect quote. Do not guess, deduplicate, add or
        // choose precedence: retain the complete message and surface one governed review hold.
        var commercialConflictCount = EmailInquiryCommercialConflictDetector.Count(componentLines);
        if (commercialConflictCount > 0)
        {
            _log.LogWarning(
                "Assembly {AssemblyId} has {ConflictCount} cross-component commercial value "
                + "conflict(s); no Lead is created until the source contradiction is reviewed.",
                assemblyId, commercialConflictCount);
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId,
                EmailInquiryHoldReasons.CrossComponentCommercialConflict,
                EmailInquiryHoldReasons.CrossComponentCommercialConflictDetail, ct);
            return new AssembleOutcome(null, null);
        }

        // The representative job. The Lead needs a provenance anchor and every component of
        // this message shares one batch, so the lowest-ordinal component's job is a stable,
        // deterministic choice rather than "whichever finished last".
        var anchorJobId = ordered.Select(r => r.ExtractionJobId).FirstOrDefault();
        // AsNoTracking, deliberately. Tracked, any later edit anywhere in the persist path would
        // emit an UPDATE on ExtractionJobs from inside the assembly-lock transaction — closing an
        // ABBA cycle with the live worker, which locks the job row first and the assembly second.
        var anchorJob = await _context.Set<ExtractionJob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == anchorJobId, ct);
        if (anchorJob is null)
        {
            // Same reasoning as the header case: a dead end that changes no state is a message
            // that stalls forever with nothing recording why.
            _log.LogError(
                "Assembly {AssemblyId} names extraction job {JobId}, which no longer exists.",
                assemblyId, anchorJobId);
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId, EmailInquiryHoldReasons.ResultMissing,
                "The processing record for part of this message is no longer available, so the "
                + "inquiry cannot be completed automatically. It needs a look.", ct);
            return new AssembleOutcome(null, null);
        }

        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = header with { Items = merged },
            ExpectedItemCount = expected,
            ExtractedItemCount = extracted,
            ReviewReason = reviewReasons.Count > 0 ? string.Join("; ", reviewReasons) : null,
            AiProviderClass = MergedProviderClass(providerClasses),
            ProcessingPath = MergedProcessingPath(processingPaths)
        };

        var leadId = await _persister.PersistAssembledMessageAsync(anchorJob, outcome, ct);

        // A NON-POSITIVE LEAD ID IS "NO LEAD WAS PRODUCED", AND IT MUST NOT BE CALLED SUCCESS.
        //
        // This value used to be passed straight into MarkAssembledAsync, which wrote it verbatim.
        // A message therefore reached `Assembled` with `AssembledLeadId = 0` — a value that is not
        // an id and never was. Downstream, the message reads as finished: the operator is offered
        // "open lead" for a lead that cannot be opened, and every count of assembled messages is
        // one higher than the count of inquiries that exist.
        //
        // Zero is not a corruption; it is the persister truthfully saying it created nothing. The
        // one path that produces it in practice is identity reconciliation classifying the merged
        // inquiry as PossibleMatchReviewRequired: a match against an existing Lead that is not
        // certain enough to link automatically, so a human decision is raised and no second
        // commercial record is written. (A confident duplicate is a different outcome entirely —
        // reconciliation returns the EXISTING Lead's real id, which is positive, so the message
        // records that Lead and this branch is never taken.)
        //
        // So the honest disposition is the one the module already has for "read in full, cannot
        // be completed automatically": held for review, with a typed reason. The hold commits with
        // the rest of this transaction, the message stays visible and actionable, and — because it
        // is no longer ReadyForAssembly with a null lead — the recovery sweep correctly stops
        // treating it as stranded work to re-run.
        if (leadId <= 0)
        {
            _log.LogError(
                "Assembly {AssemblyId} for business unit {BusinessUnitId} merged {Results} "
                + "component result(s) into {Lines} line(s) but the persist path produced no lead "
                + "(returned {LeadId}); the message is held for review rather than marked "
                + "assembled.",
                assemblyId, businessUnitId, ordered.Count, merged.Count, leadId);
            await _coordinator.HoldForReviewAsync(
                businessUnitId, assemblyId, EmailInquiryHoldReasons.LeadNotProduced,
                EmailInquiryHoldReasons.LeadNotProducedDetail, ct);
            return new AssembleOutcome(null, null);
        }

        // Same transaction as the Lead. MarkAssembledAsync joins the ambient transaction rather
        // than opening its own, so either the message has a Lead and says so, or neither.
        await _coordinator.MarkAssembledAsync(businessUnitId, assemblyId, leadId, ct);

        _log.LogInformation(
            "Assembly {AssemblyId} became Lead {LeadId} for business unit {BusinessUnitId}: "
            + "{Components} component(s), {Lines} line(s) merged from {Results} result(s).",
            assemblyId, leadId, businessUnitId, assembly.Components.Count, merged.Count, ordered.Count);

        return new AssembleOutcome(leadId, anchorJob);
    }

    /// <summary>
    /// Did this part reach the END of its source and find nothing being asked for?
    ///
    /// <para>The question the zero-line gate has to answer, and the one that decides whether an
    /// operator is told the SENDER asked for nothing or told that WE could not recover what was
    /// there. Getting it backwards on a real customer RFQ sends a salesperson to chase a buyer
    /// who did their part.</para>
    ///
    /// <para><b>Positive evidence only.</b> Three facts must all hold, and the absence of any of
    /// them means "not read in full":</para>
    /// <list type="number">
    /// <item>No line survived (<c>ExtractedItemCount == 0</c>) — otherwise there is nothing
    /// empty about this part.</item>
    /// <item>No line was even seen (<c>ExpectedItemCount == 0</c>). A positive count is content
    /// that could have carried the request and did not survive; that is the OTHER sentence.</item>
    /// <item>The extractor's own verdict is one that can only be produced by a complete read.
    /// <c>null</c> is the deterministic and clean-document case: a CSV with nothing but a header,
    /// or a whole document the model read and found no request in, both come back Ok with no
    /// review reason at all. The conversational constant is the body path saying the same thing
    /// about a message it received in full — and it is a CONSTANT precisely so that
    /// <c>ConversationalExtractionService</c> and this decision cannot
    /// drift apart silently.</item>
    /// </list>
    ///
    /// <para><b>Everything else is a hold, including reasons this build has never seen.</b> An
    /// incomplete OCR, a failed chunk, a body clipped at the input ceiling and a confidence
    /// below the floor all name themselves in the review reason, and none of them is evidence
    /// that the sender asked for nothing. So does whatever a future extractor invents — and the
    /// unknown-reason default lands on "we could not recover it", which is the sentence that
    /// costs a glance rather than a deal.</para>
    ///
    /// <para>A FAILED extraction never reaches here at all: <c>ExtractionWorker</c> diverts on
    /// <c>Failed</c> or a null result before any component result is written, so there is no row
    /// for this predicate to see. That is the property that keeps a dead-lettered RFQ out of
    /// this branch entirely, and it is asserted rather than assumed
    /// (MarketingMailDoesNotBecomeLeadPostgreSqlTests).</para>
    /// </summary>
    private static bool IsCompleteAndEmptyRead(EmailInquiryComponentResult result)
        => result.ExtractedItemCount == 0
           && result.ExpectedItemCount == 0
           && (result.ReviewReason is null
               || string.Equals(
                   result.ReviewReason,
                   ERP_RFQ_Automation.Extraction.Conversational.ConversationalExtractionService
                       .NoRequestableItemsReviewReason,
                   StringComparison.Ordinal));

    /// <summary>External if ANY component used an external model; null when none did.</summary>
    private static AiProviderClass? MergedProviderClass(HashSet<string> classes)
    {
        if (classes.Count == 0) return null;
        if (classes.Contains(nameof(AiProviderClass.External))) return AiProviderClass.External;
        return Enum.TryParse<AiProviderClass>(classes.First(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Deterministic only when EVERY component was deterministic. One model-extracted part makes
    /// the merged answer a model-extracted answer, and claiming otherwise to a customer reading
    /// the Trust Center is the kind of untruth that is worse than the uncertainty it hides.
    /// </summary>
    private static ExtractionProcessingPath MergedProcessingPath(HashSet<string> paths)
    {
        if (paths.Count == 1 && Enum.TryParse<ExtractionProcessingPath>(paths.First(), out var only))
            return only;
        return paths.Contains(nameof(ExtractionProcessingPath.DeterministicRules)) && paths.Count == 1
            ? ExtractionProcessingPath.DeterministicRules
            : ExtractionProcessingPath.NativeParser;
    }
}
