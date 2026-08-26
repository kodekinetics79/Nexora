using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Proves storage-governance mutations are fenced above the Admin rank that automatically
/// satisfies ordinary module permissions. This is a tenant Owner/SUPER_ADMIN boundary, not a
/// decorative UI permission.
/// </summary>
public sealed class TenantStorageGovernanceAuthorizationTests
{
    private const long TenantId = 71_201;

    [Theory]
    [InlineData(nameof(PlatformGovernanceController.UpdateEvidenceRetentionPolicy))]
    [InlineData(nameof(PlatformGovernanceController.RunEvidenceRetentionPurge))]
    [InlineData(nameof(PlatformGovernanceController.RunTenantDataCleanup))]
    public void Destructive_storage_actions_require_tenant_owner_not_users_edit(string actionName)
    {
        var method = Action(actionName);

        Assert.Single(method.GetCustomAttributes<RequireTenantOwnerRoleAttribute>(true));
        Assert.Empty(method.GetCustomAttributes<RequireModulePermissionAttribute>(true));
    }

    [Theory]
    [InlineData(nameof(PlatformGovernanceController.GetEvidenceRetention))]
    [InlineData(nameof(PlatformGovernanceController.GetTenantData))]
    public void Read_only_storage_usage_remains_available_through_users_view(string actionName)
    {
        var method = Action(actionName);
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>(true));

        Assert.Equal("Users", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
        Assert.Empty(method.GetCustomAttributes<RequireTenantOwnerRoleAttribute>(true));
    }

    [Theory]
    [InlineData(RoleRanks.Member, false)]
    [InlineData(RoleRanks.Manager, false)]
    [InlineData(RoleRanks.Admin, false)]
    [InlineData(RoleRanks.Owner, true)]
    public async Task Tenant_owner_gate_uses_explicit_rank_and_rejects_admin_bypass(
        short roleRank,
        bool expected)
    {
        Assert.Equal(expected, await AuthorizesAsync(
            new RankRoleGate(roleRank, 90, TenantId),
            Principal(90, TenantId)));
    }

    [Fact]
    public async Task Tenant_owner_gate_fails_closed_for_missing_or_cross_tenant_claims()
    {
        var foreignTenantOwner = new RankRoleGate(RoleRanks.Owner, 90, TenantId + 1);

        Assert.False(await AuthorizesAsync(foreignTenantOwner, Principal(null, TenantId)));
        Assert.False(await AuthorizesAsync(foreignTenantOwner, Principal(90, null)));
        Assert.False(await AuthorizesAsync(foreignTenantOwner, Principal(90, TenantId)));
    }

    [Fact]
    public async Task Policy_provider_builds_a_dedicated_tenant_owner_requirement()
    {
        var provider = new ModulePermissionPolicyProvider(Options.Create(new AuthorizationOptions()));

        var policy = await provider.GetPolicyAsync(RequireTenantOwnerRoleAttribute.PolicyName);

        Assert.NotNull(policy);
        Assert.Single(policy!.Requirements.OfType<TenantOwnerRoleRequirement>());
    }

    private static MethodInfo Action(string name) =>
        typeof(PlatformGovernanceController).GetMethod(name)
        ?? throw new InvalidOperationException($"Missing action {name}.");

    private static async Task<bool> AuthorizesAsync(
        IRoleGate roleGate,
        ClaimsPrincipal principal)
    {
        var handler = new TenantOwnerRoleHandler(
            roleGate,
            NullLogger<TenantOwnerRoleHandler>.Instance);
        var requirement = new TenantOwnerRoleRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(authorization);
        return authorization.HasSucceeded;
    }

    private static ClaimsPrincipal Principal(long? roleId, long? businessUnitId)
    {
        var claims = new List<Claim>();
        if (roleId.HasValue) claims.Add(new Claim("roleId", roleId.Value.ToString()));
        if (businessUnitId.HasValue)
            claims.Add(new Claim("businessUnitId", businessUnitId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private sealed class RankRoleGate(short rank, long allowedRoleId, long allowedBusinessUnitId)
        : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) =>
            Task.FromResult(roleId == allowedRoleId
                            && businessUnitId == allowedBusinessUnitId
                            && rank >= RoleRanks.Owner);

        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) =>
            Task.FromResult(roleId == allowedRoleId
                            && businessUnitId == allowedBusinessUnitId
                            && rank >= RoleRanks.Manager);

        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) =>
            Task.FromResult(roleId == allowedRoleId && businessUnitId == allowedBusinessUnitId
                ? rank
                : RoleRanks.Member);

        public Task<bool> CanManageRoleAsync(
            long callerRoleId,
            long? targetRoleId,
            long businessUnitId) => Task.FromResult(false);
    }
}
