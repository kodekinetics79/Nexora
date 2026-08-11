using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>What an operator must say to take an execution away from the attempt that holds it.</summary>
public sealed class RecoverProvisioningLeaseRequest
{
    /// <summary>
    /// Why. Required, and stored on the execution as well as in the audit log — the next person to
    /// open this execution is reading the execution, not the audit trail.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(1000, MinimumLength = 8)]
    public string Reason { get; set; } = null!;

    /// <summary>
    /// Leave null to release the execution back to the queue, which is what an operator almost
    /// always wants: any runner then claims it through the ordinary lease path, and the resume
    /// picks up at the first step that has not completed.
    ///
    /// <para>Set it only when a NAMED runner must finish the work. It is a machine identity, not a
    /// person, and handing ownership to a runner that is not actually running would leave the
    /// execution leased and idle until the lease lapses again.</para>
    /// </summary>
    [StringLength(200)]
    public string? TransferToOwner { get; set; }
}

/// <summary>The verdict, in the shape the console renders.</summary>
public sealed class ProvisioningLeaseAssessmentDto
{
    public long ExecutionId { get; set; }

    public string Slug { get; set; } = null!;

    /// <summary>One of <c>Live</c>, <c>Cooling</c>, <c>Abandoned</c>, <c>OwnershipResidue</c>,
    /// <c>Unowned</c>, <c>Terminal</c>.</summary>
    public string Staleness { get; set; } = null!;

    public bool IsRecoverable { get; set; }

    public long SilentForSeconds { get; set; }

    public long GraceSeconds { get; set; }

    public string State { get; set; } = null!;

    public string? LeaseOwner { get; set; }

    public DateTime? LeaseUntil { get; set; }

    public DateTime? LeaseHeartbeatAt { get; set; }

    public string? CurrentStep { get; set; }

    public string? FailedStep { get; set; }

    public int AttemptCount { get; set; }

    public int RecoveredAttemptCount { get; set; }

    /// <summary>Where a resume would restart. Null when every step has reached a terminal success.</summary>
    public string? ResumesAtStep { get; set; }

    public List<string> CompletedSteps { get; set; } = [];

    /// <summary>
    /// Everything that does not add up — a tenant that is Active over an unfinished provision, a
    /// step whose id says it committed while its status says it failed, a rival execution on the
    /// same address. Reported, never silently repaired: each has a correct answer that belongs to
    /// an authority other than this one.
    /// </summary>
    public List<string> Findings { get; set; } = [];
}

public sealed class ProvisioningLeaseRecoveryResponse
{
    public ProvisioningLeaseAssessmentDto Assessment { get; set; } = null!;

    /// <summary>Ownership as it stood before the transfer, so the response is itself evidence.</summary>
    public ProvisioningLeaseOwnershipDto Before { get; set; } = null!;

    public ProvisioningLeaseOwnershipDto After { get; set; } = null!;

    /// <summary>
    /// True when the execution kept its original idempotency key and request fingerprint — which
    /// it always does, because a recovery resumes an attempt rather than starting one. Returned
    /// rather than assumed so a caller can assert it.
    /// </summary>
    public bool IdempotencyPreserved { get; set; }
}

public sealed class ProvisioningLeaseOwnershipDto
{
    public string State { get; set; } = null!;

    public string? LeaseOwner { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTime? LeaseUntil { get; set; }

    public DateTime? LeaseHeartbeatAt { get; set; }

    public string? CurrentStep { get; set; }

    public int AttemptCount { get; set; }

    public int RecoveredAttemptCount { get; set; }

    public long Version { get; set; }
}

/// <summary>
/// The governed way to unstick a provisioning execution whose runner died holding it.
///
/// <para><b>Why it is not a column on the retry endpoint.</b> Retry answers "the cause is fixed,
/// try again" and refuses outright while anything holds the execution — correctly, because it has
/// no way to tell a runner that is working from one that is dead. This endpoint answers the other
/// question: "is the thing holding it still alive, and if provably not, take it away." The two
/// have different evidence, different refusals and different audit records, so they are different
/// doors.</para>
///
/// <para><b>The assessment is a GET and the recovery is a POST, deliberately.</b> Whoever is on
/// call must be able to see whether an execution is recoverable from a read-only session, without
/// a reason prompt, before anything changes — otherwise the first action taken under pressure is
/// the one that mutates state.</para>
/// </summary>
[ApiController]
[Route("api/platform/provisioning")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class ProvisioningLeaseRecoveryController : ControllerBase
{
    private readonly IProvisioningLeaseRecovery _recovery;

    public ProvisioningLeaseRecoveryController(IProvisioningLeaseRecovery recovery)
        => _recovery = recovery;

    /// <summary>Whether this execution can be recovered, and if not, why not.</summary>
    [HttpGet("executions/{id:long}/lease")]
    public async Task<ActionResult<ProvisioningLeaseAssessmentDto>> Assess(
        long id, CancellationToken ct)
    {
        var assessment = await _recovery.AssessAsync(id, ct);
        return assessment is null ? NotFound() : Ok(ToDto(assessment));
    }

    /// <summary>Every execution an operator could recover right now, longest silence first.</summary>
    [HttpGet("executions/recoverable")]
    public async Task<ActionResult<IEnumerable<ProvisioningLeaseAssessmentDto>>> ListRecoverable(
        [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok((await _recovery.ListRecoverableAsync(take, ct)).Select(ToDto));

    /// <summary>
    /// Takes ownership from an abandoned attempt and returns the before/after evidence.
    ///
    /// <para>409 on a live lease is not a rate limit to retry through: it is the system refusing to
    /// put a second runner on a tenant the first one is still building.</para>
    /// </summary>
    [HttpPost("executions/{id:long}/lease/recover")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisioningLeaseRecoveryResponse>> Recover(
        long id, [FromBody] RecoverProvisioningLeaseRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _recovery.RecoverAsync(
            new ProvisioningRecoveryCommand(
                id, request.Reason, Actor(), ActorPlatformUserId(), request.TransferToOwner),
            ct);

        switch (result.Outcome)
        {
            case ProvisioningRecoveryOutcome.NotFound:
                return NotFound();

            case ProvisioningRecoveryOutcome.LeaseStillLive:
            case ProvisioningRecoveryOutcome.NotYetStale:
            case ProvisioningRecoveryOutcome.Terminal:
            case ProvisioningRecoveryOutcome.NothingToRecover:
            case ProvisioningRecoveryOutcome.VersionConflict:
                return Conflict(new
                {
                    error = result.Error,
                    outcome = result.Outcome.ToString(),
                    assessment = result.Assessment is null ? null : ToDto(result.Assessment)
                });
        }

        return Ok(new ProvisioningLeaseRecoveryResponse
        {
            Assessment = ToDto(result.Assessment!),
            Before = ToDto(result.Before!),
            After = ToDto(result.After!),
            IdempotencyPreserved =
                string.Equals(result.Before!.IdempotencyKey, result.After!.IdempotencyKey,
                    StringComparison.Ordinal)
                && string.Equals(result.Before.RequestFingerprint, result.After.RequestFingerprint,
                    StringComparison.Ordinal)
        });
    }

    private static ProvisioningLeaseAssessmentDto ToDto(ProvisioningRecoveryAssessment assessment)
        => new()
        {
            ExecutionId = assessment.ExecutionId,
            Slug = assessment.Slug,
            Staleness = assessment.Staleness.ToString(),
            IsRecoverable = assessment.IsRecoverable,
            SilentForSeconds = (long)assessment.SilentFor.TotalSeconds,
            GraceSeconds = (long)assessment.Grace.TotalSeconds,
            State = assessment.Ownership.State,
            LeaseOwner = assessment.Ownership.LeaseOwner,
            LeaseUntil = assessment.Ownership.LeaseUntil,
            LeaseHeartbeatAt = assessment.Ownership.LeaseHeartbeatAt,
            CurrentStep = assessment.Ownership.CurrentStep,
            FailedStep = assessment.Ownership.FailedStep,
            AttemptCount = assessment.Ownership.AttemptCount,
            RecoveredAttemptCount = assessment.Ownership.RecoveredAttemptCount,
            ResumesAtStep = assessment.FirstIncompleteStep,
            CompletedSteps = [.. assessment.CompletedSteps],
            Findings = [.. assessment.Findings]
        };

    private static ProvisioningLeaseOwnershipDto ToDto(ProvisioningOwnershipSnapshot snapshot)
        => new()
        {
            State = snapshot.State,
            LeaseOwner = snapshot.LeaseOwner,
            LeaseToken = snapshot.LeaseToken,
            LeaseUntil = snapshot.LeaseUntil,
            LeaseHeartbeatAt = snapshot.LeaseHeartbeatAt,
            CurrentStep = snapshot.CurrentStep,
            AttemptCount = snapshot.AttemptCount,
            RecoveredAttemptCount = snapshot.RecoveredAttemptCount,
            Version = snapshot.Version
        };

    private string Actor() => User.FindFirst("email")?.Value ?? "platform";

    private long? ActorPlatformUserId()
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(sub, out var parsed) && parsed > 0 ? parsed : null;
    }
}
