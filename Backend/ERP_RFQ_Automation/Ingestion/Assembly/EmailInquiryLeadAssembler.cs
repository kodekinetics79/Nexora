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
        LeadExtractionResult? header = null;
        var expected = 0;
        var extracted = 0;
        var reviewReasons = new List<string>();
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

            if (parsed is null) continue;

            header ??= parsed;
            if (parsed.Items is { Count: > 0 })
                merged.AddRange(parsed.Items);
            expected += result.ExpectedItemCount;
            extracted += result.ExtractedItemCount;
            if (!string.IsNullOrWhiteSpace(result.AiProviderClass))
                providerClasses.Add(result.AiProviderClass!);
            processingPaths.Add(result.ProcessingPath);
            if (!string.IsNullOrWhiteSpace(result.ReviewReason))
                reviewReasons.Add(result.ReviewReason!);
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

        // THE UNREAD PARTS TRAVEL WITH THE LEAD.
        //
        // A message reaches this method with unread parts only because the state machine now
        // lets it — see rule (2) there. That is only half the fix: a Lead built from three of a
        // buyer's four attachments, with nothing saying so, is exactly the "price quoted against
        // a document nobody opened" that withholding the Lead was trying to prevent. Naming the
        // files on the Lead's review reason is what makes the trade honest, and it is why the
        // blockade could be dropped safely.
        var unreadParts = assembly.Components
            .Where(c => c.Status == EmailInquiryComponentStatus.Skipped)
            .OrderBy(c => c.Ordinal)
            .ToList();

        if (unreadParts.Count > 0)
        {
            var names = string.Join(", ", unreadParts.Select(c => c.FileName ?? $"part {c.Ordinal}"));
            reviewReasons.Insert(0,
                $"{unreadParts.Count} attached part(s) could not be read and are NOT reflected in "
                + $"these lines: {names}. Price with that in mind.");

            _log.LogInformation(
                "Assembly {AssemblyId} carries {Count} unread part(s) onto its lead: {Names}",
                assemblyId, unreadParts.Count, names);
        }

        var outcome = new ChunkedExtractionOutcome
        {
            // NeedsReview when a part went unread, and that is the whole trade: the Lead is
            // still CREATED — which is the fix — but it is not auto-verified, and the persist
            // path stamps "[NEEDS REVIEW] …" onto its remarks, so the missing evidence follows
            // the inquiry to whoever prices it. Ok here would create a Lead that looks complete
            // and quietly is not, which is the outcome withholding it was trying to avoid.
            Status = unreadParts.Count > 0
                ? ExtractionOutcomeStatus.NeedsReview
                : ExtractionOutcomeStatus.Ok,
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
