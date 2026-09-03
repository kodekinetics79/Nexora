using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// The customer's own staff accounts, administered from the platform plane.
///
/// <para><b>This deliberately crosses a boundary the product otherwise defends.</b>
/// <see cref="ProvisionTenantRequest"/> states the intended journey — Platform Admin -&gt;
/// customer account -&gt; that customer's Super Admin -&gt; sub accounts — and provisioning creates
/// exactly one account, the founding administrator, precisely so the second step is the
/// customer's. A product decision was taken to let an operator add further accounts directly:
/// pilot customers repeatedly arrive with a second person who must be in the workspace before
/// the founding administrator has ever signed in, and the alternative on offer today is an
/// operator reading a password down a telephone. That path is now supported, narrow, and
/// audited — it is NOT the primary one. <c>Controllers/UserController</c> remains the way a
/// tenant staffs itself, and everything here honours the same invariants it does, because a
/// second door into the same table is only safe if it has the same locks:</para>
/// <list type="bullet">
///   <item><description>global email uniqueness — one address, one account, one tenant;</description></item>
///   <item><description>plan seat entitlement, checked before an invitation is promised;</description></item>
///   <item><description>a rank ceiling, so support cannot mint a second tenant owner;</description></item>
///   <item><description>the last-active-administrator lockout guard;</description></item>
///   <item><description>strict scoping to the tenant's primary business unit — the same predicate
///   <c>TenantAdminInvitationService.ReissueAsync</c> uses to refuse inviting somebody else's
///   account to an address of an operator's choosing.</description></item>
/// </list>
///
/// <para><b>Invite, do not type a password.</b> Creation issues an activation link by default and
/// the account stays dormant until the person redeems it, so no operator ever holds a working
/// credential for a customer's employee. The operator-set-password path exists for a customer
/// whose mail is blocked, is Owner-only, never generates a secret, and is recorded in the audit
/// trail as what it is.</para>
///
/// <para>Every mutation follows <c>PlatformUsersController</c>'s shape: the change and its audit
/// row commit in ONE transaction, inside the context's execution strategy, with a fresh change
/// tracker per attempt.</para>
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:long}")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class TenantUsersController : ControllerBase
{
    /// <summary>
    /// The highest tenant rank an operator who is not a platform Owner may hand out.
    ///
    /// <para>This is the platform-plane reading of <c>RoleGate.CanManageRoleAsync</c>'s rule that
    /// you may never grant a role you do not outrank. A platform operator holds no role inside the
    /// customer's tenant at all, so there is no rank of theirs to compare against; what stands in
    /// for it is their control-plane authority. <c>PermissionHandler</c> satisfies EVERY module
    /// requirement for a role at <see cref="RoleRanks.Admin"/> or above before it reads a single
    /// RolePermissions row — which is exactly why <c>TenantBaselineCatalog</c> seeds no starter
    /// role at that tier — so granting one hands over the whole workspace. A support administrator
    /// may add a customer's sales rep or sales manager; creating a second person who owns the
    /// tenant is an Owner's decision and is visible as one in the audit trail.</para>
    /// </summary>
    internal const short MaxRankGrantableWithoutPlatformOwner = RoleRanks.Manager;

    private readonly ErpRfqAutomationContext _context;
    private readonly IPlatformAuditService _audit;
    private readonly ITenantAdminInvitationService _invitations;
    private readonly IEntitlementService _entitlements;
    private readonly TenantOnboardingOptions _onboarding;
    private readonly ILogger<TenantUsersController> _logger;

    private readonly ERP_RFQ_Automation.Security.ITenantSessionCache? _sessions;

    /// <summary>
    /// Rotates the token-revocation handle and evicts the cached verdict: tokens the account
    /// already holds are refused on their next request (docs/design/token-revocation.md).
    /// </summary>
    private void RevokeIssuedTokens(User user)
    {
        user.SecurityStamp = ERP_RFQ_Automation.Security.SecurityStamps.NewStamp();
        _sessions?.Evict(user.Id);
    }

    public TenantUsersController(
        ErpRfqAutomationContext context,
        IPlatformAuditService audit,
        ITenantAdminInvitationService invitations,
        IEntitlementService entitlements,
        IOptions<TenantOnboardingOptions> onboarding,
        ILogger<TenantUsersController> logger,
        ERP_RFQ_Automation.Security.ITenantSessionCache? sessions = null)
    {
        _sessions = sessions;
        _context = context;
        _audit = audit;
        _invitations = invitations;
        _entitlements = entitlements;
        _onboarding = onboarding.Value;
        _logger = logger;
    }

    // ==== reads ==================================================================================

    // GET /api/platform/tenants/{tenantId}/users
    [HttpGet("users")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<IEnumerable<TenantUserDto>>> ListUsers(long tenantId, CancellationToken ct)
    {
        if (await ResolveTenantAsync(tenantId, ct) is not { } tenant) return NotFound();
        if (tenant.PrimaryBusinessUnitId is not long businessUnitId) return Ok(Array.Empty<TenantUserDto>());

        // Buid is the ONLY scope. Reading by tenant id and hoping the join is right is how one
        // customer's roster ends up on another customer's screen; the business unit is the column
        // the rows actually carry.
        var users = await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Buid == businessUnitId)
            .OrderBy(u => u.CreatedOn).ThenBy(u => u.Id)
            .ToListAsync(ct);

        var roles = await RoleRowsAsync(businessUnitId, ct);

        // Through the service rather than over the table, so "Pending / Expired / Revoked /
        // Redeemed" is decided in exactly one place. Two implementations of that rule would
        // disagree the first time expiry handling changed, and the console would then contradict
        // the reissue endpoint about whether a link is live.
        var invitations = await _invitations.ListAsync(tenantId, ct);
        var latestPerUser = invitations
            .GroupBy(i => i.UserId)
            .ToDictionary(group => group.Key, group => group.First());
        var everRedeemed = invitations
            .Where(i => i.RedeemedAtUtc is not null)
            .Select(i => i.UserId)
            .ToHashSet();

        return Ok(users.Select(user => ToDto(user, roles, latestPerUser, everRedeemed)));
    }

    // GET /api/platform/tenants/{tenantId}/roles
    [HttpGet("roles")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<IEnumerable<TenantRoleDto>>> ListRoles(long tenantId, CancellationToken ct)
    {
        if (await ResolveTenantAsync(tenantId, ct) is not { } tenant) return NotFound();
        if (tenant.PrimaryBusinessUnitId is not long businessUnitId) return Ok(Array.Empty<TenantRoleDto>());

        var roles = await RoleRowsAsync(businessUnitId, ct);
        var counts = await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Buid == businessUnitId && u.IsActive == true && u.RoleId != null)
            .GroupBy(u => u.RoleId!.Value)
            .Select(group => new { RoleId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.RoleId, row => row.Count, ct);

        var isPlatformOwner = IsPlatformOwner();
        return Ok(roles.Values
            // Inactive roles are excluded: this list exists to be assigned FROM, and offering a
            // role RoleGate resolves as Member-ranked (it ignores inactive rows) would let an
            // operator grant an authority the tenant plane will not honour.
            .Where(role => role.IsActive != false)
            .OrderByDescending(role => role.RoleRank).ThenBy(role => role.SetupValue)
            .Select(role => new TenantRoleDto
            {
                Id = role.SetupId,
                Code = role.SetupCode,
                Name = role.SetupValue,
                Description = role.Description,
                Rank = role.RoleRank,
                RankLabel = RoleRanks.Describe(role.RoleRank),
                ActiveUserCount = counts.TryGetValue(role.SetupId, out var count) ? count : 0,
                Grantable = isPlatformOwner || role.RoleRank <= MaxRankGrantableWithoutPlatformOwner,
                NotGrantableReason = isPlatformOwner || role.RoleRank <= MaxRankGrantableWithoutPlatformOwner
                    ? null
                    : RankRefusal(role.RoleRank)
            }));
    }

    // ==== create =================================================================================

    // POST /api/platform/tenants/{tenantId}/users
    [HttpPost("users")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<CreateTenantUserResponse>> CreateUser(
        long tenantId, [FromBody] CreateTenantUserRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (await ResolveTenantAsync(tenantId, ct) is not { } tenant) return NotFound();
        if (tenant.PrimaryBusinessUnitId is not long businessUnitId)
            return Conflict(new
            {
                error = "This tenant has no workspace yet, so it has nowhere to put an account. " +
                        "Finish provisioning it first."
            });
        if (tenant.Status == TenantStatus.Archived)
            return Conflict(new
            {
                error = "This tenant is archived. Restore it before adding accounts to it — an " +
                        "offboarded workspace must not gain new people while it waits to be purged."
            });

        // Users.Email is GLOBALLY unique: one address, one account, one tenant. Compared
        // case-insensitively rather than by the index's exact rule, because the answer an operator
        // needs is "this person already has an account somewhere", and Sara@ and sara@ are the same
        // person to everyone except a byte comparison.
        var email = request.Email.Trim();
        if (await _context.Users.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct))
            return Conflict(new
            {
                error = $"A user with email '{email}' already exists. One email address maps to one " +
                        "account on one tenant; use a different address for this person."
            });

        var roles = await RoleRowsAsync(businessUnitId, ct);
        if (RoleDenial(request.RoleId, roles) is { } roleDenial) return roleDenial;
        var role = roles[request.RoleId];

        // Seat entitlement, checked here even though an INVITED account is created dormant and
        // therefore consumes no seat yet. Redemption flips IsActive with no entitlement check of
        // its own — it is an anonymous request holding a token, not a metered operator action — so
        // this is the only moment the plan can be consulted at all. Promising a customer an
        // activation link that their plan can never honour is worse than refusing here, where the
        // operator can still upgrade them or free a seat.
        if (await SeatDenialAsync(businessUnitId, ct) is { } seatDenial) return seatDenial;

        var activation = (request.Activation ?? TenantUserActivationMethods.Invite).Trim().ToLowerInvariant();
        if (activation is not (TenantUserActivationMethods.Invite or TenantUserActivationMethods.Password))
            return BadRequest(new
            {
                error = $"activation '{request.Activation}' is not recognised; use " +
                        $"'{TenantUserActivationMethods.Invite}' or '{TenantUserActivationMethods.Password}'."
            });

        var invited = activation == TenantUserActivationMethods.Invite;
        if (!invited)
        {
            // An operator typing a credential for somebody else's employee is a liability, not a
            // convenience: nothing in Models/User marks a password as needing rotation, so whatever
            // is typed here is what that person signs in with indefinitely, and the operator knows
            // it. Owner-only, and never server-generated — a generated secret would have to be
            // returned in a response body and would then live in a chat window.
            if (!IsPlatformOwner())
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Setting a password on a customer's behalf is Owner-only. Invite the " +
                            "person instead: they receive a single-use link and choose their own."
                });

            var policy = ActivationPasswordPolicy.Validate(
                request.Password, email, _onboarding.EffectiveMinimumPasswordLength);
            if (!policy.IsAcceptable)
                return BadRequest(new { error = "The password was rejected.", failures = policy.Failures });
        }

        // On the invite path NOBODY holds a credential for this account — not the person, who has
        // not chosen one, and deliberately not the operator. The column is non-nullable, so it
        // takes the hash of a discarded random value: unusable by construction, and unbreakable
        // into something that logs in because no plaintext for it exists anywhere.
        //
        // Hashed OUTSIDE the transaction because BCrypt mints a fresh salt per call and the
        // execution strategy may run the delegate more than once; the stored hash must not differ
        // between attempts.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(
            invited ? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") : request.Password!);
        var actor = ActorEmail();
        var now = DateTime.UtcNow;

        User created = null!;
        IssuedTenantAdminInvitation? issued = null;
        await ExecuteAuditedAsync(async () =>
        {
            // Constructed inside the retriable delegate: an instance built outside it stays tracked
            // as Added after a transient failure, and the retry would re-add it against a change
            // tracker that already holds it.
            created = new User
            {
                FirstName = request.FirstName.Trim(),
                MiddleName = string.IsNullOrWhiteSpace(request.MiddleName) ? null : request.MiddleName.Trim(),
                LastName = request.LastName.Trim(),
                Email = email,
                PasswordHash = passwordHash,
                ImageUrl = string.Empty,
                RoleId = role.SetupId,
                Buid = businessUnitId,
                Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? null : request.Timezone.Trim(),
                IsActive = !invited,
                // Seats-meter reproducibility, the same rule UserRepository.AddAsync applies: an
                // account created already-inactive is stamped as deactivated at creation so the
                // billing meter never reads it as seat usage during the invitation's flight.
                DeactivatedAtUtc = invited ? now : null,
                CreatedBy = actor,
                CreatedOn = now
            };
            _context.Users.Add(created);
            await _context.SaveChangesAsync(ct);

            // IssueAsync takes the caller's unit of work precisely so it can be used here: the
            // invitation joins this transaction, so a create that rolls back cannot leave a live
            // activation link for an account that does not exist. The email is sent after the
            // commit — a sent email cannot be rolled back.
            issued = invited
                ? await _invitations.IssueAsync(_context, new TenantAdminInvitationRequest
                {
                    TenantId = tenantId,
                    UserId = created.Id,
                    Email = email,
                    RecipientName = created.FirstName,
                    TenantName = tenant.Name,
                    IssuedBy = actor
                }, ct)
                : null;

            // The address, the role and the operator's stated reason are recorded. The password is
            // not, typed or generated, and neither is the activation token or the URL carrying it.
            await _audit.WriteAsync(User, "tenant.user.create", nameof(User), created.Id.ToString(),
                new
                {
                    Email = email,
                    RoleId = role.SetupId,
                    RoleCode = role.SetupCode,
                    RoleRank = role.RoleRank,
                    Activation = activation,
                    PasswordSetByOperator = !invited,
                    InvitationIssued = issued is not null,
                    request.Reason
                },
                actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);
        }, ct);

        var dispatched = false;
        if (issued is not null)
        {
            dispatched = await _invitations.SendInvitationEmailAsync(issued, ct);
            if (!dispatched)
                _logger.LogWarning(
                    "The invitation {InvitationId} for the new user {UserId} on tenant {TenantId} was not " +
                    "accepted by the email provider. The link is valid; delivery needs attention.",
                    issued.InvitationId, created.Id, tenantId);
        }

        var summary = issued is null
            ? null
            : (await _invitations.ListAsync(tenantId, ct)).FirstOrDefault(i => i.Id == issued.InvitationId);

        return Created($"/api/platform/tenants/{tenantId}/users/{created.Id}", new CreateTenantUserResponse
        {
            User = ToDto(created, roles, summary is null
                ? new Dictionary<long, TenantAdminInvitationSummary>()
                : new Dictionary<long, TenantAdminInvitationSummary> { [created.Id] = summary },
                new HashSet<long>()),
            Invitation = summary,
            EmailDispatched = dispatched,
            // Shown once, to an Owner, and only when the provider did not transmit: the same rule
            // the resend endpoint applies, so a mail outage cannot strand a customer without
            // making activation links routinely readable by operators.
            ActivationUrl = issued is not null && !dispatched && IsPlatformOwner() ? issued.ActivationUrl : null
        });
    }

    // ==== lifecycle ==============================================================================

    // POST /api/platform/tenants/{tenantId}/users/{userId}/deactivate
    [HttpPost("users/{userId:long}/deactivate")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<TenantUserDto>> DeactivateUser(
        long tenantId, long userId, [FromBody] TenantUserStatusChangeRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var resolved = await ResolveUserAsync(tenantId, userId, ct);
        if (resolved.Failure is { } failure) return failure;
        var (tenant, businessUnitId, user) = (resolved.Tenant!, resolved.BusinessUnitId, resolved.User!);

        if (user.IsActive == true
            && await IsLastActiveAdministratorAsync(businessUnitId, user.Id, user.RoleId, ct))
            return Conflict(new { error = LastAdministratorRefusal });

        try
        {
            await ExecuteAuditedAsync(async () =>
            {
                var current = await _context.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId, ct);

                // Re-read under the transaction. The guard above answered a question about the
                // state of the roster, and between that read and this write another operator may
                // have taken the other administrator out of service.
                if (current.IsActive == true
                    && await IsLastActiveAdministratorAsync(businessUnitId, current.Id, current.RoleId, ct))
                    throw new LastAdministratorException(LastAdministratorRefusal);

                if (current.IsActive == true)
                {
                    current.IsActive = false;
                    current.DeactivatedAtUtc = DateTime.UtcNow;
                    current.ModifiedBy = ActorEmail();
                    current.ModifiedOn = DateTime.UtcNow;
                    // Deactivation is the case this whole mechanism exists for: without it the
                    // suspended account keeps every token it holds for up to an hour.
                    RevokeIssuedTokens(current);
                }
                await _context.SaveChangesAsync(ct);

                // Withdrawn in the SAME transaction, and this is not tidiness. Redemption sets
                // IsActive = true and clears DeactivatedAtUtc, so an outstanding link left alive
                // here lets whoever holds it switch a deactivated account back on — an operator's
                // suspension undone silently by the person it was aimed at.
                var withdrawn = await _invitations.RevokeOutstandingForUserAsync(
                    _context, tenantId, userId, ActorEmail(),
                    "The account was deactivated from the platform console.", ct);

                await _audit.WriteAsync(User, "tenant.user.deactivate", nameof(User), userId.ToString(),
                    new { current.Email, InvitationsWithdrawn = withdrawn, request.Reason },
                    actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);
                user = current;
            }, ct);
        }
        catch (LastAdministratorException exception)
        {
            return Conflict(new { error = exception.Message });
        }

        return Ok(await ProjectAsync(tenant, businessUnitId, user, ct));
    }

    // POST /api/platform/tenants/{tenantId}/users/{userId}/reactivate
    [HttpPost("users/{userId:long}/reactivate")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<TenantUserDto>> ReactivateUser(
        long tenantId, long userId, [FromBody] TenantUserStatusChangeRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var resolved = await ResolveUserAsync(tenantId, userId, ct);
        if (resolved.Failure is { } failure) return failure;
        var (tenant, businessUnitId, user) = (resolved.Tenant!, resolved.BusinessUnitId, resolved.User!);

        if (tenant.Status == TenantStatus.Archived)
            return Conflict(new { error = "This tenant is archived; its accounts cannot be returned to service." });

        // Reactivating consumes a seat exactly as creating an active user does — the meter reads
        // IsActive, and nothing else.
        if (user.IsActive != true && await SeatDenialAsync(businessUnitId, ct) is { } seatDenial)
            return seatDenial;

        if (user.IsActive != true)
        {
            await ExecuteAuditedAsync(async () =>
            {
                var current = await _context.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId, ct);
                current.IsActive = true;
                current.DeactivatedAtUtc = null;
                current.ModifiedBy = ActorEmail();
                current.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, "tenant.user.reactivate", nameof(User), userId.ToString(),
                    new { current.Email, request.Reason },
                    actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);
                user = current;
            }, ct);
        }

        return Ok(await ProjectAsync(tenant, businessUnitId, user, ct));
    }

    // PUT /api/platform/tenants/{tenantId}/users/{userId}/role
    //
    // Owner-gated where the rest of this controller is TenantAdmin. Changing somebody's role is a
    // privilege grant inside a customer's tenant, made by a person who is not in that tenant and
    // whom the customer never appointed; the console's other privilege-granting verbs sit at Owner
    // for the same reason.
    [HttpPut("users/{userId:long}/role")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<TenantUserDto>> ChangeUserRole(
        long tenantId, long userId, [FromBody] ChangeTenantUserRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var resolved = await ResolveUserAsync(tenantId, userId, ct);
        if (resolved.Failure is { } failure) return failure;
        var (tenant, businessUnitId, user) = (resolved.Tenant!, resolved.BusinessUnitId, resolved.User!);

        if (tenant.Status == TenantStatus.Archived)
            return Conflict(new { error = "This tenant is archived; its roles cannot be reassigned." });

        var roles = await RoleRowsAsync(businessUnitId, ct);
        if (RoleDenial(request.RoleId, roles) is { } roleDenial) return roleDenial;
        var target = roles[request.RoleId];
        if (user.RoleId == target.SetupId) return Ok(await ProjectAsync(tenant, businessUnitId, user, ct));

        // Demoting the last administrator locks the tenant out of its own RBAC screens just as
        // surely as deactivating them does, so the guard covers both verbs — the identical pairing
        // UserController.Update makes.
        if (user.IsActive == true
            && target.RoleRank < RoleRanks.Owner
            && await IsLastActiveAdministratorAsync(businessUnitId, user.Id, user.RoleId, ct))
            return Conflict(new { error = LastAdministratorRefusal });

        try
        {
            await ExecuteAuditedAsync(async () =>
            {
                var current = await _context.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId, ct);
                if (current.IsActive == true
                    && target.RoleRank < RoleRanks.Owner
                    && await IsLastActiveAdministratorAsync(businessUnitId, current.Id, current.RoleId, ct))
                    throw new LastAdministratorException(LastAdministratorRefusal);

                var previousRoleId = current.RoleId;
                current.RoleId = target.SetupId;
                current.ModifiedBy = ActorEmail();
                current.ModifiedOn = DateTime.UtcNow;
                // The roleId claim in every live token now names the OLD role.
                RevokeIssuedTokens(current);
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, "tenant.user.role.change", nameof(User), userId.ToString(),
                    new
                    {
                        current.Email,
                        FromRoleId = previousRoleId,
                        ToRoleId = target.SetupId,
                        ToRoleCode = target.SetupCode,
                        ToRoleRank = target.RoleRank,
                        request.Reason
                    },
                    actAsTenantId: tenantId, httpContext: HttpContext, ct: ct);
                user = current;
            }, ct);
        }
        catch (LastAdministratorException exception)
        {
            return Conflict(new { error = exception.Message });
        }

        return Ok(await ProjectAsync(tenant, businessUnitId, user, ct));
    }

    // ==== internals ==============================================================================

    private const string LastAdministratorRefusal =
        "This is the last active administrator in this tenant. Deactivating or demoting them would " +
        "leave the customer with nobody who can reach their own Roles & Permissions screen, and the " +
        "tenant plane has no way to recover from that on its own.";

    /// <summary>
    /// Sec3, as <c>PlatformUsersController</c> states it: every mutation and its audit record
    /// commit in ONE transaction — an audit-write failure rolls the mutation back, and a committed
    /// mutation can never be missing its trail. The body runs inside the context's execution
    /// strategy (EnableRetryOnFailure requires it) with a fresh change tracker per attempt.
    /// </summary>
    private async Task ExecuteAuditedAsync(Func<Task> mutateAndAudit, CancellationToken ct)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            await mutateAndAudit();
            await tx.CommitAsync(ct);
        });
    }

    private Task<Tenant?> ResolveTenantAsync(long tenantId, CancellationToken ct) =>
        _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

    /// <summary>
    /// Tenant, business unit and target account, or the single refusal that covers all three ways
    /// this can fail. The account is looked up BY BUSINESS UNIT as well as by id: without that, an
    /// operator holding one tenant id and a guessed user id could deactivate — or re-role —
    /// somebody else's employee. It is the same predicate <c>ReissueAsync</c> uses to refuse
    /// mailing an activation link for an account outside the tenant's primary business unit.
    /// </summary>
    private async Task<(Tenant? Tenant, long BusinessUnitId, User? User, ActionResult? Failure)>
        ResolveUserAsync(long tenantId, long userId, CancellationToken ct)
    {
        if (await ResolveTenantAsync(tenantId, ct) is not { } tenant)
            return (null, 0, null, NotFound());
        if (tenant.PrimaryBusinessUnitId is not long businessUnitId)
            return (null, 0, null, NotFound());

        var user = await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Buid == businessUnitId, ct);
        return user is null
            ? (null, 0, null, NotFound())
            : (tenant, businessUnitId, user, null);
    }

    private async Task<Dictionary<long, SetupMaster>> RoleRowsAsync(long businessUnitId, CancellationToken ct) =>
        await _context.SetupMasters.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.SetupType == "Role")
            .ToDictionaryAsync(s => s.SetupId, ct);

    /// <summary>
    /// The refusal for a role that cannot be assigned, or null when it can. Existence, tenancy,
    /// activity and the rank ceiling in one place, so the create and the role-change paths cannot
    /// drift apart.
    /// </summary>
    private ActionResult? RoleDenial(long roleId, IReadOnlyDictionary<long, SetupMaster> roles)
    {
        if (!roles.TryGetValue(roleId, out var role))
            return BadRequest(new { error = $"Role {roleId} does not belong to this tenant." });
        if (role.IsActive == false)
            return BadRequest(new
            {
                error = $"Role '{role.SetupValue}' is inactive in this tenant and grants nothing; " +
                        "reactivate it in the tenant's own Roles & Permissions screen first."
            });
        if (!IsPlatformOwner() && role.RoleRank > MaxRankGrantableWithoutPlatformOwner)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = RankRefusal(role.RoleRank) });
        return null;
    }

    private static string RankRefusal(short rank) =>
        $"A {RoleRanks.Describe(rank)}-ranked role satisfies every module permission check in the " +
        "tenant before a single grant is read, so assigning one hands over the whole workspace. " +
        "That is a platform Owner's decision.";

    /// <summary>
    /// True when this account is the ONLY active holder of an administrator-capable role in the
    /// business unit. "Administrator-capable" is <see cref="RoleRanks.Owner"/> and above —
    /// deliberately the identical threshold <c>IRoleGate.IsSuperAdminAsync</c> uses, so the
    /// platform plane and the tenant plane can never disagree about who the last one is.
    /// </summary>
    private async Task<bool> IsLastActiveAdministratorAsync(
        long businessUnitId, long userId, long? roleId, CancellationToken ct)
    {
        if (roleId is not long held) return false;

        var administratorRoleIds = await _context.SetupMasters.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId
                        && s.SetupType == "Role"
                        && s.RoleRank >= RoleRanks.Owner
                        && s.IsActive != false)
            .Select(s => s.SetupId)
            .ToListAsync(ct);
        if (!administratorRoleIds.Contains(held)) return false;

        return !await _context.Users.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(u => u.Buid == businessUnitId
                           && u.Id != userId
                           && u.IsActive == true
                           && u.RoleId != null
                           && administratorRoleIds.Contains(u.RoleId.Value), ct);
    }

    /// <summary>
    /// The 403 problem+json seat denial, or null when a seat is free. Rendered by the single
    /// canonical <see cref="EntitlementProblemFilter"/> shape rather than a hand-rolled payload, so
    /// the console reads one denial format wherever it hits the plan's ceiling.
    /// </summary>
    private async Task<ActionResult?> SeatDenialAsync(long businessUnitId, CancellationToken ct)
    {
        var seats = await _entitlements.CheckSeatAvailabilityAsync(businessUnitId, ct);
        return seats.Allowed
            ? null
            : EntitlementProblemFilter.ToResult(new SeatLimitExceededException(businessUnitId, seats));
    }

    private async Task<TenantUserDto> ProjectAsync(
        Tenant tenant, long businessUnitId, User user, CancellationToken ct)
    {
        var roles = await RoleRowsAsync(businessUnitId, ct);
        var invitations = await _invitations.ListAsync(tenant.Id, ct);
        var latest = invitations.Where(i => i.UserId == user.Id).ToList();
        var latestPerUser = latest.Count == 0
            ? new Dictionary<long, TenantAdminInvitationSummary>()
            : new Dictionary<long, TenantAdminInvitationSummary> { [user.Id] = latest[0] };
        var everRedeemed = latest.Any(i => i.RedeemedAtUtc is not null)
            ? new HashSet<long> { user.Id }
            : new HashSet<long>();
        return ToDto(user, roles, latestPerUser, everRedeemed);
    }

    private static TenantUserDto ToDto(
        User user,
        IReadOnlyDictionary<long, SetupMaster> roles,
        IReadOnlyDictionary<long, TenantAdminInvitationSummary> latestInvitations,
        IReadOnlySet<long> everRedeemed)
    {
        var role = user.RoleId is long roleId && roles.TryGetValue(roleId, out var found) ? found : null;
        latestInvitations.TryGetValue(user.Id, out var invitation);
        return new TenantUserDto
        {
            Id = user.Id,
            FirstName = user.FirstName,
            MiddleName = user.MiddleName,
            LastName = user.LastName,
            Email = user.Email,
            RoleId = user.RoleId,
            RoleCode = role?.SetupCode,
            RoleName = role?.SetupValue,
            RoleRank = role?.RoleRank,
            IsActive = user.IsActive == true,
            DeactivatedAtUtc = user.DeactivatedAtUtc,
            LastLogin = user.LastLogin,
            CreatedOn = user.CreatedOn,
            Invitation = invitation,
            // An account that was invited and has never redeemed holds no credential anybody
            // knows. Returning the account to service does not give it one; only a fresh
            // invitation does, and the console says so rather than offering the wrong repair.
            AwaitingActivation = invitation is not null && !everRedeemed.Contains(user.Id)
        };
    }

    private bool IsPlatformOwner() =>
        User.HasClaim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner));

    private string ActorEmail() => User.FindFirst("email")?.Value ?? "platform";

    private sealed class LastAdministratorException(string message) : Exception(message);
}
