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
    /// Builds the ONE Lead for a message whose components have all finished. Returns the Lead
    /// id, or null when the message produced nothing to quote or is not ready.
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
        if (_context.Database.CurrentTransaction is not null)
            return AssembleCoreAsync(businessUnitId, assemblyId, ct);

        var strategy = _context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            // A retried attempt must not inherit the previous one's tracked state.
            _context.ChangeTracker.Clear();
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);
            var leadId = await AssembleCoreAsync(businessUnitId, assemblyId, ct);
            // Committed on EVERY path, not only the success one: the hold-for-review writes are
            // the whole reason a refusal is visible rather than a silent stall, and disposing an
            // uncommitted transaction would roll each of them straight back.
            await transaction.CommitAsync(ct);
            return leadId;
        });
    }

    private async Task<long?> AssembleCoreAsync(
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
        var locked = await _context.EmailInquiryAssemblies
            .FromSql($"""
                SELECT * FROM "EmailInquiryAssemblies"
                WHERE "Id" = {assemblyId} AND "BusinessUnitId" = {businessUnitId}
                FOR UPDATE
                """)
            .ToListAsync(ct);

        if (locked.Count == 0)
            throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for business unit {businessUnitId}.");

        var assembly = await _context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstAsync(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId, ct);

        // Already done. The second worker did nothing wrong, so this returns the Lead the first
        // one created rather than an error — and it can now actually FIND it.
        if (assembly.Status == EmailInquiryAssemblyStatus.Assembled)
            return assembly.AssembledLeadId;

        if (assembly.Status != EmailInquiryAssemblyStatus.ReadyForAssembly)
        {
            _log.LogDebug(
                "Assembly {AssemblyId} is {Status}; not assembling.", assemblyId, assembly.Status);
            return null;
        }

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
            return null;
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
            return null;
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
                return null;
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
            return null;
        }

        // The representative job. The Lead needs a provenance anchor and every component of
        // this message shares one batch, so the lowest-ordinal component's job is a stable,
        // deterministic choice rather than "whichever finished last".
        var anchorJobId = ordered.Select(r => r.ExtractionJobId).FirstOrDefault();
        var anchorJob = await _context.Set<ExtractionJob>()
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
            return null;
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

        // Same transaction as the Lead. MarkAssembledAsync joins the ambient transaction rather
        // than opening its own, so either the message has a Lead and says so, or neither.
        await _coordinator.MarkAssembledAsync(businessUnitId, assemblyId, leadId, ct);

        _log.LogInformation(
            "Assembly {AssemblyId} became Lead {LeadId} for business unit {BusinessUnitId}: "
            + "{Components} component(s), {Lines} line(s) merged from {Results} result(s).",
            assemblyId, leadId, businessUnitId, assembly.Components.Count, merged.Count, ordered.Count);

        return leadId;
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
