using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.CustomerResolution;

/// <summary>
/// Runs client resolution for leads created by an UPLOAD door.
///
/// <para><b>The gap this closes.</b> Only the extraction worker ever resolved a lead's client
/// (<c>ExtractionWorker.TryResolveCustomersAsync</c>). Manual upload, the watched folders and the
/// lead uploader all added leads straight to the context and returned, so every lead born that way
/// carried a NULL <c>CustomerMatchReasonCode</c> — not "no match found", but never evaluated. A
/// lead cannot be qualified or converted without a client, so those enquiries arrived already
/// stuck, and <c>GET /api/Lead/{id}/client-candidates</c> answered zero because nothing had ever
/// looked.</para>
///
/// <para><b>Why a shared helper rather than three copies.</b> Three services with three different
/// transaction shapes would otherwise each grow their own version of the same loop, and the next
/// intake door added would grow a fourth or forget entirely — which is precisely how this gap
/// appeared. One call site to read, one place for the reasoning.</para>
///
/// <para><b>Best effort, deliberately.</b> Resolution failing must never fail an upload: the
/// document is safely captured and the lead exists, and refusing the whole upload because a
/// name could not be matched would lose the work the user actually did. A failed lead stays
/// UNRESOLVED, which is a state the backfill and the manual link both already handle.
/// <c>ResolveAsync</c> is idempotent and never overwrites a human decision, so re-running is
/// always safe.</para>
/// </summary>
public static class UploadedLeadResolution
{
    /// <summary>
    /// Resolve each lead, one at a time, swallowing per-lead failures.
    /// </summary>
    /// <param name="resolution">
    /// Required at every call site. Callers inject it as a REQUIRED dependency, following the rule
    /// ManualUploadService states beside its identity collaborator: an optional dependency is
    /// always supplied in production and always absent in tests, so the step that must always run
    /// becomes the step nothing exercises. That is how this gap was created.
    /// </param>
    /// <param name="door">Which intake door created these leads, for the log line.</param>
    public static async Task ResolveAsync(
        ILeadCustomerResolutionService resolution,
        long businessUnitId,
        IEnumerable<long> leadIds,
        ILogger logger,
        string door,
        CancellationToken ct = default)
    {
        foreach (var leadId in leadIds)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var outcome = await resolution.ResolveAsync(businessUnitId, leadId, ct);
                logger.LogInformation(
                    "Client resolution for {Door} lead {LeadId}: {Status} ({Reason}, confidence {Confidence:F2}).",
                    door, leadId, outcome.Status, outcome.ReasonCode, outcome.Confidence);
            }
            catch (Exception ex)
            {
                // The lead and its document are already committed. Saying so is the whole value of
                // this line: an unresolved lead is recoverable and re-runnable, and a silent one is
                // indistinguishable from a lead nobody has got to yet.
                logger.LogError(ex,
                    "Client resolution failed for {Door} lead {LeadId}; the lead stays unresolved and can be re-run.",
                    door, leadId);
            }
        }
    }
}
