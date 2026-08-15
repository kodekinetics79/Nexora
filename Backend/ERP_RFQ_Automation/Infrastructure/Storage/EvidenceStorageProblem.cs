using ERP_RFQ_Automation.Platform.Entitlements;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ERP_RFQ_Automation.Infrastructure.Storage;

/// <summary>
/// The ONE rendering of "we cannot durably store documents right now" for every intake door.
///
/// <para>It exists because the answer must be identical wherever a document enters: the
/// operator's next action is the same at all five doors, and a second hand-rolled payload is
/// how one of them would drift back into "try again" for a fault where trying again cannot
/// work — the 2026-08-12 defect.</para>
///
/// <para>The payload carries the exception's MESSAGE, which is operator-safe by construction
/// (see <see cref="EvidenceStorageUnavailableException"/>), and nothing else. The bucket,
/// endpoint, credentials, provider name and stack are in the caller's log line and stay
/// there.</para>
/// </summary>
public sealed class EvidenceStorageProblemFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not EvidenceStorageUnavailableException unavailable)
            return;

        // Safety net for any intake door that does not catch this itself: an unhandled
        // storage outage must never reach a caller as a bare 500, which says nothing at all.
        context.Result = ToResult(unavailable);
        context.ExceptionHandled = true;
    }

    /// <summary>The type-level summary. Stable across both faults, so it is never the sentence.</summary>
    public const string Title = "Uploads are paused — document storage is unavailable";

    /// <param name="extras">
    /// Door-specific facts to publish alongside the refusal — the accepted work a batch door
    /// already stored, a batch reference. Additive only: the keys above are written last, so a
    /// caller cannot redefine the refusal itself.
    /// </param>
    public static ObjectResult ToResult(
        EvidenceStorageUnavailableException exception,
        IReadOnlyDictionary<string, object?>? extras = null)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (extras is not null)
            foreach (var extra in extras)
                payload[extra.Key] = extra.Value;

        payload["type"] = NexoraProblems.DocumentStorageUnavailable;
        payload["title"] = Title;
        // RFC 7807 puts the sentence in `detail`, and so does every other problem these
        // controllers return. It went out as `title` alone once, and the doors that render
        // `detail` — the supplier-quote dialog, the shared error boundary — dropped it and fell
        // back to "try again shortly": the 2026-08-12 advice, restored by a field name.
        payload["detail"] = exception.OperatorDetail;
        payload["status"] = StatusCodes.Status503ServiceUnavailable;
        payload["errorCode"] = EvidenceStorageUnavailableException.ErrorCode;
        // False for a provider blip that may clear on its own; true when a human has to
        // edit configuration and no amount of retrying will help. The two demand
        // completely different next actions, so the client is told which one this is.
        payload["isConfigurationFault"] = exception.IsConfigurationFault;

        return new ObjectResult(payload)
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/problem+json" }
        };
    }
}
