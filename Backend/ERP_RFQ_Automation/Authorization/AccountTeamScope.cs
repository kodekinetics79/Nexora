using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Authorization;

/// <summary>
/// The three tiers FR-DSH-05 names, in ascending breadth. The middle one did not exist before this
/// gate — scope was a boolean, <c>tenant</c> or <c>assigned_to_me</c>, and a supervisor was
/// therefore either an executive or a single rep.
/// </summary>
public enum AccountScopeTier
{
    /// <summary>An account team member: their own accounts and their own work.</summary>
    AssignedAccounts = 0,

    /// <summary>A supervisor or manager: every account team they manage or belong to.</summary>
    ManagedScope = 10,

    /// <summary>An executive or administrator: the whole tenant.</summary>
    Tenant = 20
}

/// <summary>
/// Who one caller may read, expressed as sets a database predicate can consume.
///
/// <para><see cref="TeamIds"/> and <see cref="UserIds"/> are both present because the two questions
/// are genuinely different. A customer belongs to a TEAM (<see cref="Customer.AccountTeamId"/>); a
/// lead, quote or follow-up is assigned to a USER. A supervisor's scope is the union: the accounts
/// of the teams they run, and the work of the people in them.</para>
///
/// <para>Both collections are ordered and de-duplicated so a scope is comparable and a cache key
/// over it is stable.</para>
/// </summary>
public sealed record AccountTeamScope(
    AccountScopeTier Tier,
    long UserId,
    IReadOnlyList<long> TeamIds,
    IReadOnlyList<long> UserIds)
{
    public bool IsTenantWide => Tier == AccountScopeTier.Tenant;

    /// <summary>The wire name of the tier, carried on every scoped payload so a reader can see
    /// which scope produced the figure rather than guessing from its size.</summary>
    public string ScopeName => Tier switch
    {
        AccountScopeTier.Tenant => "tenant",
        AccountScopeTier.ManagedScope => "managed_scope",
        _ => "assigned_accounts"
    };

    /// <summary>
    /// A tenant-wide scope for a caller who holds the whole tenant plane. Kept as a factory so the
    /// two empty lists cannot be mistaken for "no teams and no users", which for any other tier
    /// would mean the caller sees nothing.
    /// </summary>
    public static AccountTeamScope TenantWide(long userId) =>
        new(AccountScopeTier.Tenant, userId, [], [userId]);
}

public interface IAccountTeamScopeResolver
{
    /// <summary>
    /// Resolves the caller's account scope in their own tenant.
    ///
    /// <para><paramref name="asOfUtc"/> dates the OWNERSHIP overlay that
    /// <c>AccountTeamReadFilter</c> applies (<c>CustomerOwnership</c> is effective-dated and is
    /// still read through both ends of its window). It no longer dates team membership: since the
    /// resolver reads <c>Users.TeamID</c>, which is current state rather than a history, "which
    /// team is this person on" has exactly one answer and it is today's. See
    /// <see cref="AccountTeamScopeResolver"/> for why that column is the authority.</para>
    /// </summary>
    Task<AccountTeamScope> ResolveAsync(
        long userId, long roleId, long businessUnitId, DateTime asOfUtc, CancellationToken ct = default);
}

/// <summary>
/// Resolves FR-CST-02's account-team scope, and with it the middle tier of FR-DSH-05.
///
/// <para><b>Why this had to exist before either could be built.</b> Before this gate nothing in the
/// model linked a CUSTOMER to a TEAM. <c>SalesTeamMembership</c> maps users to teams and
/// <c>CustomerOwnership</c> names a single primary and backup USER, so the only path from a customer
/// to a team ran customer → ownership → user → team — which is circular for routing (it uses the
/// ownership rows to choose between the ownership rows) and, for authorization, answers a different
/// question: "who is the named owner", not "whose book is this account in".
/// <see cref="Customer.AccountTeamId"/> supplies the missing edge, and every predicate below is one
/// join away from it.</para>
///
/// <para><b>Fail-closed.</b> A caller whose role does not resolve is <see cref="RoleRanks.Member"/>,
/// which is the narrowest tier — never the widest. A member on no team resolves to an EMPTY team
/// list, and the query predicate treats that as "no team-owned accounts", not as "no filter".</para>
///
/// <para><b>Why <c>Users.TeamID</c> is the one authority on team membership.</b> Two columns
/// answered "who is on this team" and they could never agree, because only one of them was ever
/// written. <c>SalesTeamMembership</c> — effective-dated, richer, and what this resolver used to
/// read — has a <c>DbSet</c>, a migration and an index, but NO writer anywhere in the product: no
/// controller, no service, no screen. Production carries zero rows in it. <c>Users.TeamID</c> is
/// written by <c>UserController</c> on create and update, is validated against a team in the
/// caller's own business unit (<c>UserRepository</c>), is what the Users screen's Team dropdown
/// sets, and is what <c>TeamRepository</c> consults before letting a team be deleted.
///
/// So the tier that was supposed to sit between a rep and an executive granted nothing: every
/// manager resolved to <c>teamIds=[], userIds=[self]</c> — byte-for-byte a rep's scope — while an
/// administrator filled in the Team field on the Users screen and reasonably believed they had
/// built a team. The visible authority was not the consulted one.
///
/// The fix is to consult the visible one. That is the cheaper direction for three reasons beyond
/// the obvious: the column is already populated, so existing users are in scope immediately rather
/// than only after somebody re-saves each of them; membership needs no effective-dating UI, because
/// moving a rep between teams is one dropdown and the product has no screen that asks "on which
/// date"; and it removes a table from the authorization path rather than adding a second writer to
/// it, which is what pairing the two would have required.
///
/// <c>SalesTeamMembership</c> is deliberately NOT deleted — <c>CommercialRoutingApplicationService
/// .GetOwnerOptionsAsync</c> still unions it into a candidate-owner list, where it answers a
/// different question ("who might be offered this lead") alongside three other sources and grants
/// no access to anything. It is simply no longer consulted for authorization, so it can no longer
/// disagree with the screen about who a manager manages.</para>
/// </summary>
public sealed class AccountTeamScopeResolver : IAccountTeamScopeResolver
{
    /// <summary>
    /// How far down a team tree a manager's scope is expanded. Teams nest through
    /// <c>Team.SubTeamId</c>, which points at the PARENT (<c>TeamRepository</c> reads a team's
    /// children as <c>Teams.Any(t =&gt; t.SubTeamId == id)</c>). The bound exists because the column
    /// is a nullable self-reference with no cycle constraint in the database: a row edited to point
    /// at its own descendant would otherwise loop forever inside an authorization decision. Ten
    /// levels is far deeper than any sales organisation and the expansion also de-duplicates, so the
    /// bound can only ever cost breadth, never correctness of what it did include.
    /// </summary>
    private const int MaxTeamDepth = 10;

    private readonly ErpRfqAutomationContext _db;
    private readonly IRoleGate _roleGate;

    public AccountTeamScopeResolver(ErpRfqAutomationContext db, IRoleGate roleGate)
    {
        _db = db;
        _roleGate = roleGate;
    }

    public async Task<AccountTeamScope> ResolveAsync(
        long userId, long roleId, long businessUnitId, DateTime asOfUtc, CancellationToken ct = default)
    {
        if (userId <= 0 || businessUnitId <= 0)
            throw new ArgumentException("An authenticated user and business unit are required.");

        var rank = await _roleGate.GetRoleRankAsync(roleId, businessUnitId);

        // Admin and above hold the tenant plane already (PermissionHandler satisfies every module
        // check by rank at exactly this threshold). Resolving them to anything narrower here would
        // put two different answers to "what may this caller read" in the codebase.
        if (rank >= RoleRanks.Admin) return AccountTeamScope.TenantWide(userId);

        var myTeams = await MembershipTeamsAsync(userId, businessUnitId, ct);

        if (rank < RoleRanks.Manager)
        {
            return new AccountTeamScope(
                AccountScopeTier.AssignedAccounts, userId, myTeams, [userId]);
        }

        // ── the middle tier ──────────────────────────────────────────────────
        // A supervisor's managed scope is the teams they MANAGE (Team.ManagerId), expanded down the
        // sub-team tree, plus the teams they are themselves a member of. Manager rank alone is NOT
        // treated as tenant-wide: that is precisely the collapse this tier exists to undo.
        var managedRoots = await _db.Teams.AsNoTracking()
            .Where(t => t.BusinessUnitId == businessUnitId && t.ManagerId == userId)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var teams = new HashSet<long>(myTeams);
        teams.UnionWith(await ExpandDescendantsAsync(managedRoots, businessUnitId, ct));

        var teamIds = teams.Order().ToArray();

        // Everyone whose work rolls up to this supervisor: the people on those teams, and the
        // supervisor themselves (a manager who is on no team still owns their own leads).
        //
        // Deactivated users are included on purpose. Their leads, quotes and follow-ups do not
        // disappear when their sign-in is revoked, and the manager who inherits that pipeline is
        // precisely the person who must still be able to see it.
        var userIds = new HashSet<long> { userId };
        if (teamIds.Length > 0)
        {
            userIds.UnionWith(await _db.Users.AsNoTracking()
                .Where(u => u.Buid == businessUnitId
                            && u.TeamId != null
                            && teamIds.Contains(u.TeamId.Value))
                .Select(u => u.Id)
                .ToListAsync(ct));
        }

        return new AccountTeamScope(
            AccountScopeTier.ManagedScope, userId, teamIds, userIds.Order().ToArray());
    }

    /// <summary>
    /// The team this caller is on, as the Users screen set it — at most one, because
    /// <c>Users.TeamID</c> is a single nullable column. The result stays an array so the tier
    /// arithmetic above (union with managed teams, ordering, de-duplication) is unchanged, and so
    /// that a later move to multiple teams is a change of this method rather than of its callers.
    ///
    /// <para>The business-unit predicate is not redundant with the tenant filter: it stops a user
    /// row that has been moved between business units from carrying its old team id into a scope
    /// decision in the new one.</para>
    /// </summary>
    private async Task<long[]> MembershipTeamsAsync(
        long userId, long businessUnitId, CancellationToken ct) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId
                        && u.Buid == businessUnitId
                        && u.TeamId != null)
            .Select(u => u.TeamId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArrayAsync(ct);

    /// <summary>
    /// The managed teams and everything beneath them, breadth-first and bounded. Each level is one
    /// query, so a two-level organisation costs two round trips rather than one per team.
    /// </summary>
    private async Task<IReadOnlyCollection<long>> ExpandDescendantsAsync(
        IReadOnlyCollection<long> roots, long businessUnitId, CancellationToken ct)
    {
        var all = new HashSet<long>(roots);
        var frontier = roots.ToArray();

        for (var depth = 0; depth < MaxTeamDepth && frontier.Length > 0; depth++)
        {
            var children = await _db.Teams.AsNoTracking()
                .Where(t => t.BusinessUnitId == businessUnitId
                            && t.SubTeamId != null
                            && frontier.Contains(t.SubTeamId.Value))
                .Select(t => t.Id)
                .ToArrayAsync(ct);

            frontier = children.Where(all.Add).ToArray();
        }

        return all;
    }
}
