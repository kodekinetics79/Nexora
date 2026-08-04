using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class RolePermissionTenantIsolationTests
{
    [Fact]
    public async Task GetAll_UsesAuthenticatedTenant_NotQueryTenant()
    {
        var repository = new CapturingRepository();
        var controller = Controller(repository, 80101);

        var result = await controller.GetAll(businessUnitId: 80102, roleId: 77);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(80101, repository.LastBusinessUnitId);
    }

    [Fact]
    public async Task GetById_FailsClosed_WhenTenantClaimIsMissing()
    {
        var repository = new CapturingRepository();
        var controller = new RolePermissionController(repository, new DenyAllRoleGate())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.GetById(12, businessUnitId: 80102);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(repository.LastBusinessUnitId);
    }

    private static RolePermissionController Controller(CapturingRepository repository, long tenantId)
    {
        var identity = new ClaimsIdentity([new Claim("businessUnitId", tenantId.ToString())], "test");
        return new RolePermissionController(repository, new DenyAllRoleGate())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    /// <summary>
    /// Fail-closed <see cref="IRoleGate"/> stub. These tests assert tenant isolation of the
    /// repository call, not role escalation, so every privilege question answers "no".
    /// </summary>
    private sealed class DenyAllRoleGate : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);

        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);

        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId)
            => Task.FromResult(false);
    }

    private sealed class CapturingRepository : IRolePermissionRepository
    {
        public long? LastBusinessUnitId { get; private set; }

        public Task<(IEnumerable<RolePermission>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, long? roleId, long? moduleId, long businessUnitId)
        {
            LastBusinessUnitId = businessUnitId;
            return Task.FromResult<(IEnumerable<RolePermission>, int)>(([], 0));
        }

        public Task<RolePermission> GetByIdAsync(long id, long businessUnitId)
        {
            LastBusinessUnitId = businessUnitId;
            return Task.FromResult(new RolePermission { Id = id, BusinessUnitId = businessUnitId, ModuleId = 1 });
        }

        public Task AddAsync(RolePermission rolePermission) => Task.CompletedTask;
        public Task UpdateAsync(RolePermission rolePermission) => Task.CompletedTask;
        public Task DeleteAsync(long id, long businessUnitId) => Task.CompletedTask;
        public Task<bool> CheckPermissionAsync(long roleId, string moduleName, string action, long businessUnitId) => Task.FromResult(false);
    }
}
