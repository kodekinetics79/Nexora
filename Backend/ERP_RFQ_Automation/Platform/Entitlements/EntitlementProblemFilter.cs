using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Global MVC safety net: any <see cref="EntitlementDeniedException"/> that escapes an
/// action (e.g. the supplier-quote document intake path, whose controller rethrows
/// unknown exceptions) is rendered as its clean problem+json denial (403/429) instead
/// of the generic 500 from the outer exception handler. Registered by
/// <c>AddPlatformEntitlements()</c>. P2-A10: this is the single rendering point for
/// typed entitlement denials — the payload shape and the
/// <see cref="NexoraProblems.Base"/> type URI live here and nowhere else.
/// </summary>
public sealed class EntitlementProblemFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not EntitlementDeniedException denial)
            return;

        context.Result = ToResult(denial);
        context.ExceptionHandled = true;
    }

    /// <summary>
    /// The canonical problem+json result for a typed denial. Public so controllers
    /// with broad catch-alls (which would otherwise swallow the exception into a 500)
    /// can render the identical shape without re-rolling it.
    /// </summary>
    public static ObjectResult ToResult(EntitlementDeniedException denial)
    {
        object payload = denial switch
        {
            // Seat/quota denials keep their machine-readable usage numbers.
            SeatLimitExceededException seat => new
            {
                type = seat.ProblemType,
                title = seat.Message,
                status = seat.SuggestedStatusCode,
                limit = seat.Decision.Limit,
                activeUsers = seat.Decision.Current
            },
            DocumentQuotaExceededException quota => new
            {
                type = quota.ProblemType,
                title = quota.Message,
                status = quota.SuggestedStatusCode,
                limit = quota.Decision.Limit,
                current = quota.Decision.Current
            },
            _ => new
            {
                type = denial.ProblemType,
                title = denial.Message,
                status = denial.SuggestedStatusCode
            }
        };

        return new ObjectResult(payload)
        {
            StatusCode = denial.SuggestedStatusCode,
            ContentTypes = { "application/problem+json" }
        };
    }
}
