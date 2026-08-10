using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// Sec-A3: the one way a controller catch-all answers a caller.
///
/// <para><b>The failure.</b> Controllers across this product answered their broad
/// <c>catch (Exception ex)</c> with <c>$"Error retrieving data: {ex.Message}"</c>. When the
/// exception is an Npgsql or EF one — which is what a catch-all mostly catches — that message
/// names the schema, the table, the column, the constraint and often the offending value. On
/// <c>AuthController</c> that reached an UNAUTHENTICATED caller, so a malformed sign-in bought a
/// partial map of the database; everywhere else it reached any authenticated user of any tenant,
/// including one with no permissions at all. The global <c>UseExceptionHandler</c> already
/// generalises anything that escapes — these handlers were catching the exception before it could
/// get there and then printing it.</para>
///
/// <para><b>Why an extension rather than an injected logger.</b> None of the affected controllers
/// takes an <c>ILogger</c>, and adding one to each means editing nine constructors and every test
/// that builds them. Resolving the logger from the request's own service provider keeps the fix to
/// a one-line change per site, which is what makes sweeping all of them practical — and a fix that
/// is practical to apply everywhere is the difference between closing this class of leak and
/// closing four instances of it.</para>
///
/// <para>The detail is not discarded: it goes to the log at Error with the path and the caller's
/// business unit, where it is correlatable and access-controlled. Only the caller loses it.</para>
/// </summary>
public static class SafeErrorResponse
{
    /// <summary>
    /// Logs <paramref name="exception"/> server-side and returns a 500 carrying only
    /// <paramref name="clientMessage"/> — which must be a FIXED string, never interpolated with
    /// anything derived from the exception.
    /// </summary>
    public static ObjectResult ServerError(
        this ControllerBase controller, Exception exception, string clientMessage)
    {
        var logger = controller.HttpContext?.RequestServices
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger(controller.GetType().FullName ?? nameof(SafeErrorResponse));

        logger?.LogError(exception,
            "Unhandled error in {Controller}.{Action} for {Method} {Path} (businessUnitId={BusinessUnitId}).",
            controller.GetType().Name,
            controller.ControllerContext?.ActionDescriptor?.ActionName ?? "(unknown)",
            controller.Request?.Method,
            controller.Request?.Path.Value,
            controller.User?.FindFirst("businessUnitId")?.Value ?? "(none)");

        return new ObjectResult(new { error = clientMessage })
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
    }
}
