using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// The invitee's two endpoints: read what the link is for, then set a password with it.
///
/// <para><b>Anonymous on purpose, and safe because of the token.</b> The whole point of the flow
/// is that the person holding this link has no account yet — they cannot authenticate, so
/// requiring authentication would make activation impossible. <c>[AllowAnonymous]</c> overrides
/// both the global <c>FallbackPolicy</c> and the blanket
/// <c>MapControllers().RequireAuthorization()</c> in Program.cs, the same way
/// <c>AuthController</c> and <c>PlatformAuthController</c> do. The 256-bit single-use token is
/// the credential; nothing else is accepted, and nothing here can be reached without one.</para>
///
/// <para><b>The contract is shared with a page that already exists.</b> Routes, the
/// <c>status</c> vocabulary and the status codes below are what
/// <c>Frontend/src/pages/Activation/activationApi.ts</c> reads — it prefers <c>status</c> from
/// the body and falls back to the code, so the two are kept deliberately consistent rather than
/// merely compatible.</para>
///
/// <para><b>What is and is not disclosed.</b> Naming expired / used / revoked separately from
/// invalid tells a guesser nothing — those three are unreachable without a token that was
/// genuinely issued — while telling a real recipient the one thing they need in order to act.
/// The privacy property that matters is elsewhere and absolute: nothing is ever looked up by
/// email address, a rejected token describes no account at all, and a valid one echoes the
/// address masked.</para>
/// </summary>
[ApiController]
[Route("api/tenant-activation")]
[AllowAnonymous]
public class TenantActivationController : ControllerBase
{
    /// <summary>
    /// Key namespace for the shared, database-backed lockout (SEC-H6). Distinct from
    /// <see cref="LoginPlane.Tenant"/> and <see cref="LoginPlane.Platform"/> so activation
    /// attempts can never lock a real login out, or be locked out by one.
    ///
    /// <para>Keyed by client IP rather than by token: the thing being defended is guessing
    /// UNKNOWN tokens, and a per-token counter would only let a caller lock out the very
    /// invitation they already hold. The cost is that a large NAT shares a counter — acceptable,
    /// because only an unrecognised token counts as a failure, and a legitimate invitee
    /// produces none.</para>
    /// </summary>
    public const string ActivationPlane = "activation-ip";

    private readonly ITenantAdminInvitationService _invitations;
    private readonly ILoginAttemptThrottle _throttle;
    private readonly ILogger<TenantActivationController> _logger;

    public TenantActivationController(
        ITenantAdminInvitationService invitations,
        ILoginAttemptThrottle throttle,
        ILogger<TenantActivationController> logger)
    {
        _invitations = invitations;
        _throttle = throttle;
        _logger = logger;
    }

    // GET /api/tenant-activation/{token}
    [HttpGet("{token}")]
    public async Task<IActionResult> Inspect(string token, CancellationToken ct)
    {
        if (await RejectIfLockedOutAsync(ct) is { } locked) return locked;

        var challenge = await _invitations.InspectAsync(token, ct);
        if (challenge.State != ActivationTokenState.Valid || challenge.Preview is null)
            return await RejectTokenAsync(challenge.State, ct);

        // The counter is NOT cleared here. Loading the page proves possession of a live token,
        // but the attempt that matters is the one that spends it, and clearing on a read would
        // let a caller holding any single valid link reset the guessing budget at will.
        var preview = challenge.Preview;
        return Ok(new TenantActivationChallengeResponse
        {
            Status = "valid",
            Email = preview.Email,
            TenantName = preview.CompanyName,
            ExpiresAtUtc = preview.ExpiresAtUtc,
            FirstName = preview.RecipientFirstName,
            MinimumPasswordLength = preview.MinimumPasswordLength
        });
    }

    // POST /api/tenant-activation/{token}
    [HttpPost("{token}")]
    public async Task<IActionResult> Activate(
        string token, [FromBody] SetActivationPasswordRequest request, CancellationToken ct)
    {
        if (await RejectIfLockedOutAsync(ct) is { } locked) return locked;
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var result = await _invitations.RedeemAsync(token, request.Password, ClientIp(), ct);

        switch (result.Status)
        {
            case TenantActivationStatus.Activated:
                // Clears this address's failure counter: the caller demonstrably held a real
                // invitation, so whatever mistyped attempts preceded it were not an attack.
                await _throttle.RegisterSuccessAsync(ActivationPlane, ClientIp(), ct);
                // The password is set either way; whether the DOOR is open is a different fact
                // with a different owner, and this endpoint used to assert the wrong one. See
                // TenantActivationResult.SignInAvailable — an invitation is issued by the last
                // step of provisioning, and the tenant does not become signable-into until an
                // operator takes the authoritative activation decision. Saying "you can now sign
                // in" to somebody the login will refuse is the one sentence that turns a governed
                // gate into a support ticket.
                return Ok(new
                {
                    status = "activated",
                    signInAvailable = result.SignInAvailable,
                    message = result.SignInAvailable
                        ? "Your password is set. You can now sign in."
                        : "Your password is set. Your workspace is still being activated, so sign-in "
                          + "is not open yet — whoever set it up will confirm when it is ready."
                });

            case TenantActivationStatus.PasswordRejected:
                // NOT a throttle failure and NOT a spent invitation. The token was valid; the
                // person simply chose a password the policy refuses, and they must be able to
                // try again without asking the operator for a new link.
                //
                // Carries no `status` key on purpose: the page's token-state switch must not
                // treat a password problem as a dead link. It renders `error` instead, which is
                // one sentence naming what to change — the client checklist is intentionally
                // stricter than this policy, so reaching here means a rule only the server knows
                // (an address inside the password, a passphrase past BCrypt's 72-byte limit).
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
    /// One place that decides what a rejected token looks like on the wire, so the GET and the
    /// POST can never drift into describing the same link two different ways.
    /// </summary>
    private async Task<IActionResult> RejectTokenAsync(ActivationTokenState state, CancellationToken ct)
    {
        // Only an unrecognised token feeds the lockout — and it is AWAITED, not fired and
        // forgotten: the counter writes through the request-scoped DbContext, which is disposed
        // the moment the response completes, so a detached task would drop the failure it was
        // meant to record and leave the guessing budget effectively infinite.
        //
        // Expired, used and revoked tokens do not count. They were all genuinely issued, so
        // counting them would punish customers for clicking an old email — exactly the
        // behaviour that sends people back to the operator asking for a password.
        if (state == ActivationTokenState.Invalid)
            await RegisterInvalidTokenAsync(ct);

        var body = new TenantActivationChallengeResponse { Status = state.ToString().ToLowerInvariant() };

        // Codes chosen to match what the page falls back to when a body is missing entirely
        // (a proxy error page, a truncated response): 404 unknown, 410 gone, 409 already spent,
        // 403 withdrawn.
        var statusCode = state switch
        {
            ActivationTokenState.Expired => StatusCodes.Status410Gone,
            ActivationTokenState.Used => StatusCodes.Status409Conflict,
            ActivationTokenState.Revoked => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status404NotFound
        };

        return StatusCode(statusCode, body);
    }

    private async Task<IActionResult?> RejectIfLockedOutAsync(CancellationToken ct)
    {
        var lockout = await _throttle.CheckAsync(ActivationPlane, ClientIp(), ct);
        if (!lockout.IsLockedOut) return null;

        Response.Headers.RetryAfter = ((int)Math.Ceiling(lockout.RetryAfter.TotalSeconds)).ToString();

        // No `status` key: the page maps this to "we could not check your link just now", which
        // is the truth. Telling a rate-limited caller their link is invalid would be a lie that
        // sends a legitimate customer to support.
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = "Too many activation attempts. Please try again in a few minutes."
        });
    }

    private async Task RegisterInvalidTokenAsync(CancellationToken ct)
    {
        await _throttle.RegisterFailureAsync(ActivationPlane, ClientIp(), ct);

        // No token, not even a prefix: a log aggregator is exactly the kind of place a live
        // activation link must never be recoverable from.
        _logger.LogWarning(
            "Rejected an activation attempt with an unrecognised token from {ClientIp}.",
            ClientIp() ?? "unknown");
    }

    private string? ClientIp() => HttpContext.Connection?.RemoteIpAddress?.ToString();
}
