using System.Text.Json;
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

    public async Task<long?> AssembleAsync(
        long businessUnitId, long assemblyId, CancellationToken ct = default)
    {
        var assembly = await _context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == assemblyId, ct);

        if (assembly is null)
            throw new InvalidOperationException(
                $"Email inquiry assembly {assemblyId} was not found for business unit {businessUnitId}.");

        // Only ReadyForAssembly may proceed. Re-entrancy matters here: two workers can finish
        // the last two components at the same moment and both see a ready message, and a Lead
        // built twice is the duplicate this class exists to prevent. Already-Assembled is a
        // no-op that returns the existing Lead rather than an error, because the second worker
        // did nothing wrong.
        if (assembly.Status == EmailInquiryAssemblyStatus.Assembled)
            return await ExistingLeadIdAsync(businessUnitId, assemblyId, ct);

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
            if (!string.IsNullOrWhiteSpace(result.ReviewReason))
                reviewReasons.Add(result.ReviewReason!);
        }

        if (header is null)
        {
            _log.LogInformation(
                "Assembly {AssemblyId} produced no readable extraction; nothing to quote.", assemblyId);
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
            _log.LogError(
                "Assembly {AssemblyId} names extraction job {JobId}, which no longer exists.",
                assemblyId, anchorJobId);
            return null;
        }

        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = header with { Items = merged },
            ExpectedItemCount = expected,
            ExtractedItemCount = extracted,
            ReviewReason = reviewReasons.Count > 0 ? string.Join("; ", reviewReasons) : null,
            ProcessingPath = ExtractionProcessingPath.NativeParser
        };

        var leadId = await _persister.PersistAssembledMessageAsync(anchorJob, outcome, ct);

        await _coordinator.MarkAssembledAsync(businessUnitId, assemblyId, ct);

        _log.LogInformation(
            "Assembly {AssemblyId} became Lead {LeadId} for business unit {BusinessUnitId}: "
            + "{Components} component(s), {Lines} line(s) merged from {Results} result(s).",
            assemblyId, leadId, businessUnitId, assembly.Components.Count, merged.Count, ordered.Count);

        return leadId;
    }

    private async Task<long?> ExistingLeadIdAsync(long businessUnitId, long assemblyId, CancellationToken ct)
    {
        var jobIds = await _context.Set<EmailInquiryComponentResult>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.AssemblyId == assemblyId)
            .Select(x => x.ExtractionJobId)
            .ToListAsync(ct);

        return await _context.Set<ExtractionJob>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && jobIds.Contains(x.Id) && x.ResultLeadId != null)
            .Select(x => x.ResultLeadId)
            .FirstOrDefaultAsync(ct);
    }
}
