using ERP_RFQ_Automation.Platform.Hardening;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// The three endpoints a locked-out tenant user touches: ask for a link, read what the link is
/// for, then set a password with it.
///
/// <para><b>Anonymous on purpose, and safe because of the token.</b> Everyone who reaches these
/// endpoints is by definition unable to authenticate — that is the situation the flow exists for
/// — so requiring authentication would make recovery impossible. <c>[AllowAnonymous]</c>
/// overrides both the global <c>FallbackPolicy</c> and the blanket
/// <c>MapControllers().RequireAuthorization()</c> in Program.cs, the same way
/// <c>AuthController</c> and <c>TenantActivationController</c> do. The 256-bit single-use token
/// is the credential for the second and third endpoints; the first accepts no credential at all,
/// which is why the rest of this comment is about what it must not say.</para>
///
/// <para><b>THE ENUMERATION RULE — the one thing about this file that must never be relaxed.</b>
/// <see cref="RequestReset"/> returns the identical status code and the identical body for every
/// input it is given. Not "similar". Identical, byte for byte:</para>
/// <list type="bullet">
///   <item>an address that belongs to nobody;</item>
///   <item>an address that belongs to a deactivated account;</item>
///   <item>an address that belongs to a live account whose tenant is suspended;</item>
///   <item>an address whose account is fine but whose email the provider refused;</item>
///   <item>a string that is not an email address at all.</item>
/// </list>
/// <para>Any difference — a status code, a wording, an extra field, a validation error shape —
/// turns a public form into an oracle that confirms, one request at a time, which of a leaked
/// address list are Nexora customers. That is a directory of a customer's staff, handed to anyone
/// who asks. <b>The codebase had no precedent for this rule</b> before this file: the activation
/// flow sidesteps it entirely by never looking anything up by email, so nothing here could be
/// copied and the rule is written out instead.</para>
///
/// <para>It is enforced structurally rather than by discipline.
/// <c>IPasswordResetService.RequestResetAsync</c> returns <c>Task</c>, so this action has nothing
/// to branch on even if a future edit wanted to. The one caveat we cannot close from here is
/// TIMING: composing and dispatching a real message takes longer than finding no account, so a
/// determined prober with a stable network can still distinguish the two by latency. Closing it
/// properly needs the send moved onto a queue the request does not wait for — the module is
/// deliberately shaped so that change touches only the service. A detached <c>Task.Run</c> is NOT
/// that fix and must not be attempted here: the send writes through the request-scoped
/// DbContext, which is disposed the moment this response completes.</para>
///
/// <para><b>The token endpoints' contract is shared with a page that already exists.</b> Routes,
/// the <c>status</c> vocabulary and the status codes below are what
/// <c>Frontend/src/pages/PasswordReset/passwordResetApi.ts</c> reads — it prefers <c>status</c>
/// from the body and falls back to the code, so the two are kept deliberately consistent rather
/// than merely compatible. They are the same words <c>TenantActivationController</c> uses, so a
/// customer gets one explanation for one situation whichever link brought them here.</para>
/// </summary>
[ApiController]
[Route("api/password-reset")]
[AllowAnonymous]
public class PasswordResetController : ControllerBase
{
    /// <summary>
    /// Key namespace for the shared, database-backed lockout (SEC-H6) on TOKEN GUESSING. Distinct
    /// from <see cref="LoginPlane.Tenant"/>, <see cref="LoginPlane.Platform"/> and the activation
    /// plane, so guessing here can never lock a real sign-in out, or be locked out by one.
    ///
    /// <para>Keyed by client IP rather than by token, exactly as activation is: the thing being
    /// defended is guessing UNKNOWN tokens, and a per-token counter would only let a caller lock
    /// out the very link they already hold. The cost is that a large NAT shares a counter —
    /// acceptable, because only an unrecognised token counts as a failure and a legitimate
    /// recipient produces none.</para>
    /// </summary>
    public const string ResetTokenPlane = "password-reset-ip";

    /// <summary>
    /// A SECOND, separate plane for the request endpoint, and the separation is the point.
    ///
    /// <para>The two endpoints are abused differently and must not share a budget. Someone
    /// flooding the request form is spending our mail reputation and filling a stranger's inbox;
    /// someone hammering the token endpoints is guessing a credential. Sharing one counter would
    /// mean a flood of harmless mistyped addresses could exhaust the budget that stops guessing —
    /// or, worse, that a guesser could get the reset form locked for a whole office.</para>
    ///
    /// <para>Every request counts against this, whether or not the address matched anything. That
    /// is not laziness: a counter that advanced only on unknown addresses would be a side channel
    /// with the same shape as the response we refuse to vary — a prober could read account
    /// existence off how quickly they got locked out.</para>
    /// </summary>
    public const string ResetRequestPlane = "password-reset-request-ip";

    /// <summary>
    /// The one thing every caller of <see cref="RequestReset"/> is told, whoever they are.
    /// Deliberately phrased as a conditional so it is TRUE in every case rather than a
    /// reassuring fiction in some of them.
    /// </summary>
    private const string RequestAcceptedMessage =
        "If that address belongs to a Nexora account, we have sent it a link to reset the password. " +
        "The link expires shortly — check the spam folder if it does not arrive.";

    private readonly IPasswordResetService _resets;
    private readonly ILoginAttemptThrottle _throttle;
    private readonly ILogger<PasswordResetController> _logger;

    public PasswordResetController(
        IPasswordResetService resets,
        ILoginAttemptThrottle throttle,
        ILogger<PasswordResetController> logger)
    {
        _resets = resets;
        _throttle = throttle;
        _logger = logger;
    }

    // POST /api/password-reset/requests
    //
    // A literal segment, so it can never be confused with the {token} routes below whatever a
    // future edit does to route precedence. "requests" rather than "forgot-password" because the
    // resource is the reset request, and POSTing to a collection is what creating one is.
    /// <summary>
    /// Ask for a reset link. Read the ENUMERATION RULE on this class before changing anything
    /// about what this returns.
    /// </summary>
    [HttpPost("requests")]
    // Nothing else in the product limits how many emails an anonymous caller can cause to be
    // sent. The durable per-IP plane below bounds a persistent abuser across restarts; this
    // in-process limiter (10 per 60s, shared with SmtpController) bounds the burst that arrives
    // before the counter has finished writing.
    [EnableRateLimiting(RateLimitingExtensions.SmtpPolicy)]
    public async Task<IActionResult> RequestReset(
        [FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        if (await RejectIfLockedOutAsync(ResetRequestPlane, ct) is { } locked) return locked;

        // Counted BEFORE the work, and counted unconditionally. See ResetRequestPlane: making the
        // counter depend on the outcome would rebuild the oracle the response body refuses to be.
        await _throttle.RegisterFailureAsync(ResetRequestPlane, ClientIp(), ct);

        if (!ModelState.IsValid)
        {
            // Even a malformed body gets the standard answer. A 400 here would be a second
            // response shape from an endpoint whose entire contract is that it has one — and
            // "Email is required" tells an honest user nothing the form did not already say.
            _logger.LogInformation(
                "Discarded a malformed password-reset request from {ClientIp}.", ClientIp() ?? "unknown");
            return SameAnswerForEverybody();
        }

        // 202 rather than 200 because 202 is the honest code: we accepted the request and are
        // declining to report what came of it. Both exits go through the one helper below, so they
        // cannot drift apart.
        await _resets.RequestResetAsync(request.Email, ClientIp(), ct);
        return SameAnswerForEverybody();
    }

    // GET /api/password-reset/{token}
    [HttpGet("{token}")]
    public async Task<IActionResult> Inspect(string token, CancellationToken ct)
    {
        if (await RejectIfLockedOutAsync(ResetTokenPlane, ct) is { } locked) return locked;

        var challenge = await _resets.InspectAsync(token, ct);
        if (challenge.State != PasswordResetTokenState.Valid || challenge.Preview is null)
            return await RejectTokenAsync(challenge.State, ct);

        // The counter is NOT cleared here. Loading the page proves possession of a live token,
        // but the attempt that matters is the one that spends it, and clearing on a read would
        // let a caller holding any single valid link reset the guessing budget at will.
        var preview = challenge.Preview;
        return Ok(new PasswordResetChallengeResponse
        {
            Status = "valid",
            Email = preview.Email,
            ExpiresAtUtc = preview.ExpiresAtUtc,
            FirstName = preview.RecipientFirstName,
            MinimumPasswordLength = preview.MinimumPasswordLength
        });
    }

    // POST /api/password-reset/{token}
    [HttpPost("{token}")]
    public async Task<IActionResult> Complete(
        string token, [FromBody] SetNewPasswordRequest request, CancellationToken ct)
    {
        if (await RejectIfLockedOutAsync(ResetTokenPlane, ct) is { } locked) return locked;
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var result = await _resets.CompleteAsync(token, request.Password, ClientIp(), ct);

        switch (result.Status)
        {
            case PasswordResetStatus.Completed:
                // Clears this CLIENT IP's token-guessing counter: the caller demonstrably held a
                // real link, so whatever mistyped attempts preceded it were not an attack. (The
                // account's own sign-in lockout is cleared inside the service, where the email
                // address is known — see CompleteAsync.)
                await _throttle.RegisterSuccessAsync(ResetTokenPlane, ClientIp(), ct);
                return Ok(new
                {
                    status = "reset",
                    message = "Your password has been changed. Sign in with your new password."
                });

            case PasswordResetStatus.PasswordRejected:
                // NOT a throttle failure and NOT a spent token. The link was valid; the person
                // simply chose a password the policy refuses, and they must be able to try again
                // without starting the whole flow over.
                //
                // Carries no `status` key on purpose: the page's token-state switch must not treat
                // a password problem as a dead link. It renders `error` instead, which is one
                // sentence naming what to change — the client checklist is intentionally stricter
                // than this policy, so reaching here means a rule only the server knows (the
                // address inside the password, a passphrase past BCrypt's 72-byte limit).
                return BadRequest(new
                {
                    error = string.Join(" ", result.PasswordFailures),
                    requirements = result.PasswordFailures
                });

            default:
                return await RejectTokenAsync(result.TokenState, ct);
        }
    }

    /// <summary>
    /// The single exit for the request endpoint. Everything it can possibly answer goes through
    /// here, so "identical response whatever happened" is a property of one line of code rather
    /// than of two call sites staying in agreement.
    /// </summary>
    private IActionResult SameAnswerForEverybody() =>
        StatusCode(StatusCodes.Status202Accepted, new { message = RequestAcceptedMessage });

    /// <summary>
    /// One place that decides what a rejected token looks like on the wire, so the GET and the
    /// POST can never drift into describing the same link two different ways.
    /// </summary>
    private async Task<IActionResult> RejectTokenAsync(PasswordResetTokenState state, CancellationToken ct)
    {
        // Only an unrecognised token feeds the lockout — and it is AWAITED, not fired and
        // forgotten: the counter writes through the request-scoped DbContext, which is disposed
        // the moment the response completes, so a detached task would drop the failure it was
        // meant to record and leave the guessing budget effectively infinite.
        //
        // Expired, used and superseded tokens do not count. They were all genuinely issued, so
        // counting them would punish people for clicking an old email — which here is the
        // COMMON case, because requesting a second link is what the page tells you to do and it
        // supersedes the first.
        if (state == PasswordResetTokenState.Invalid)
        {
            await _throttle.RegisterFailureAsync(ResetTokenPlane, ClientIp(), ct);

            // No token, not even a prefix: a log aggregator is exactly the kind of place a live
            // reset link must never be recoverable from.
            _logger.LogWarning(
                "Rejected a password-reset attempt with an unrecognised token from {ClientIp}.",
                ClientIp() ?? "unknown");
        }

        var body = new PasswordResetChallengeResponse { Status = state.ToString().ToLowerInvariant() };

        // Codes chosen to match what the page falls back to when a body is missing entirely
        // (a proxy error page, a truncated response), and identical to activation's: 404 unknown,
        // 410 gone, 409 already spent, 403 withdrawn.
        var statusCode = state switch
        {
            PasswordResetTokenState.Expired => StatusCodes.Status410Gone,
            PasswordResetTokenState.Used => StatusCodes.Status409Conflict,
            PasswordResetTokenState.Revoked => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status404NotFound
        };

        return StatusCode(statusCode, body);
    }

    private async Task<IActionResult?> RejectIfLockedOutAsync(string plane, CancellationToken ct)
    {
        var lockout = await _throttle.CheckAsync(plane, ClientIp(), ct);
        if (!lockout.IsLockedOut) return null;

        Response.Headers.RetryAfter = ((int)Math.Ceiling(lockout.RetryAfter.TotalSeconds)).ToString();

        // No `status` key: the page maps this to "we could not do that just now", which is the
        // truth. Telling a rate-limited caller their link is invalid would be a lie that sends a
        // legitimate customer to support.
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = "Too many password-reset attempts. Please try again in a few minutes."
        });
    }

    private string? ClientIp() => HttpContext.Connection?.RemoteIpAddress?.ToString();
}
