using ERP_RFQ_Automation.CommercialIntelligence.Sales;
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
    /// Resolves the caller's account scope in their own tenant, as at <paramref name="asOfUtc"/>
    /// (team membership is effective-dated, so "which teams am I on" has no answer without a date).
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

        var myTeams = await EffectiveMembershipTeamsAsync(userId, businessUnitId, asOfUtc, ct);

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

        // Everyone whose work rolls up to this supervisor: the members of those teams, and the
        // supervisor themselves (a manager who is on no team still owns their own leads).
        var userIds = new HashSet<long> { userId };
        if (teamIds.Length > 0)
        {
            userIds.UnionWith(await _db.SalesTeamMemberships.AsNoTracking()
                .Where(m => m.BusinessUnitId == businessUnitId
                            && teamIds.Contains(m.TeamId)
                            && m.EffectiveFromUtc <= asOfUtc
                            && (m.EffectiveToUtc == null || m.EffectiveToUtc > asOfUtc))
                .Select(m => m.UserId)
                .ToListAsync(ct));
        }

        return new AccountTeamScope(
            AccountScopeTier.ManagedScope, userId, teamIds, userIds.Order().ToArray());
    }

    private async Task<long[]> EffectiveMembershipTeamsAsync(
        long userId, long businessUnitId, DateTime asOfUtc, CancellationToken ct) =>
        await _db.SalesTeamMemberships.AsNoTracking()
            .Where(m => m.BusinessUnitId == businessUnitId
                        && m.UserId == userId
                        && m.EffectiveFromUtc <= asOfUtc
                        && (m.EffectiveToUtc == null || m.EffectiveToUtc > asOfUtc))
            .Select(m => m.TeamId)
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
