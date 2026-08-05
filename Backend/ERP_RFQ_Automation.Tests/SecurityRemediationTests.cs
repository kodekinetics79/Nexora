using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.RolePermission;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
// 'Module' collides with System.Reflection.Module, which `using System.Reflection` brings in.
using ModuleEntity = ERP_RFQ_Automation.Models.Module;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Regression cover for the C1/H2/H3/H7 authorization remediation. Every behavioural test
/// asserts the same shape: a NON-privileged caller is rejected AND the repository is never
/// reached, so a future refactor that drops a gate fails here rather than in production.
/// </summary>
public sealed class SecurityRemediationTests
{
    private const long Tenant = 4242;

    // ---------------------------------------------------------------- C1: RBAC self-grant

    [Theory]
    [InlineData(nameof(RolePermissionController.GetAll), PermissionAction.View)]
    [InlineData(nameof(RolePermissionController.GetById), PermissionAction.View)]
    [InlineData(nameof(RolePermissionController.Create), PermissionAction.Create)]
    [InlineData(nameof(RolePermissionController.Update), PermissionAction.Edit)]
    [InlineData(nameof(RolePermissionController.Delete), PermissionAction.Delete)]
    public void RolePermission_routes_are_gated_by_the_roles_and_permissions_module(
        string actionName, PermissionAction action)
    {
        var method = typeof(RolePermissionController).GetMethods().Single(x => x.Name == actionName);
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>(true));

        Assert.Equal("Roles & Permissions", permission.ModuleName);
        Assert.Equal(action, permission.Action);
    }

    [Fact]
    public async Task RolePermission_create_rejects_a_caller_granting_its_own_role()
    {
        var repository = new RecordingRolePermissionRepository();
        var controller = RolePermissions(repository, callerRoleId: 7);

        // The caller points the new grant at its own roleId - the exact self-escalation
        // that used to give any authenticated user full CRUD on every module.
        var result = await controller.Create(new RolePermissionCreateRequestDTO
        {
            RoleId = 7, ModuleId = 1, CanCreate = true, CanEdit = true, CanDelete = true
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task RolePermission_create_rejects_a_caller_granting_a_higher_role()
    {
        var repository = new RecordingRolePermissionRepository();
        // Role 1 is a super admin; the caller (role 7) is not, so it outranks the caller.
        var controller = RolePermissions(repository, callerRoleId: 7,
            gate: new StubRoleGate { SuperAdminRoleIds = { 1 } });

        var result = await controller.Create(new RolePermissionCreateRequestDTO
        {
            RoleId = 1, ModuleId = 1, CanDelete = true
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task RolePermission_create_rejects_granting_a_permission_the_caller_lacks()
    {
        // The caller may manage role 9 and holds View+Edit on module 1, but not Delete.
        var repository = new RecordingRolePermissionRepository
        {
            CallerGrant = new RolePermission
            {
                RoleId = 7, ModuleId = 1, BusinessUnitId = Tenant, CanEdit = true
            }
        };
        var controller = RolePermissions(repository, callerRoleId: 7,
            gate: new StubRoleGate { DefaultCanManageRole = true });

        var result = await controller.Create(new RolePermissionCreateRequestDTO
        {
            RoleId = 9, ModuleId = 1, CanDelete = true
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task RolePermission_delete_rejects_revoking_a_role_the_caller_cannot_manage()
    {
        var repository = new RecordingRolePermissionRepository
        {
            Existing = new RolePermission { Id = 3, RoleId = 1, ModuleId = 1, BusinessUnitId = Tenant }
        };
        var controller = RolePermissions(repository, callerRoleId: 7,
            gate: new StubRoleGate { SuperAdminRoleIds = { 1 } });

        var result = await controller.Delete(3);

        Assert.IsType<ForbidResult>(result);
        Assert.False(repository.Deleted);
    }

    [Fact]
    public async Task RolePermission_create_fails_closed_without_a_role_claim()
    {
        var repository = new RecordingRolePermissionRepository();
        var controller = RolePermissions(repository, callerRoleId: null,
            gate: new StubRoleGate { DefaultCanManageRole = true });

        var result = await controller.Create(new RolePermissionCreateRequestDTO
        {
            RoleId = 9, ModuleId = 1
        });

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task RolePermission_create_allows_a_super_admin_managing_another_role()
    {
        var repository = new RecordingRolePermissionRepository();
        var controller = RolePermissions(repository, callerRoleId: 1,
            gate: new StubRoleGate { SuperAdminRoleIds = { 1 } });

        var result = await controller.Create(new RolePermissionCreateRequestDTO
        {
            RoleId = 9, ModuleId = 1, CanCreate = true
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(9, repository.Added?.RoleId);
        Assert.Equal(Tenant, repository.Added?.BusinessUnitId);
    }

    // ------------------------------------------------- H3: cross-tenant BU enumeration

    [Fact]
    public async Task BusinessUnit_dropdown_is_scoped_to_the_callers_tenant()
    {
        var repository = new RecordingBusinessUnitRepository();
        var controller = BusinessUnits(repository, Tenant);

        var result = await controller.GetDropdown();

        Assert.IsType<OkObjectResult>(result.Result);
        // Before the fix this was GetAllAsync(1, 1000, null, null) - no tenant predicate.
        Assert.Equal(Tenant, repository.LastId);
    }

    [Fact]
    public async Task BusinessUnit_dropdown_fails_closed_without_a_tenant_claim()
    {
        var repository = new RecordingBusinessUnitRepository();
        var controller = BusinessUnits(repository, tenantId: null);

        var result = await controller.GetDropdown();

        Assert.IsType<ForbidResult>(result.Result);
        Assert.False(repository.WasQueried);
    }

    // -------------------------------------------- H2: ungated global reference tables

    [Theory]
    [InlineData(nameof(ModuleController.GetAll), PermissionAction.View)]
    [InlineData(nameof(ModuleController.GetById), PermissionAction.View)]
    [InlineData(nameof(ModuleController.Create), PermissionAction.Create)]
    [InlineData(nameof(ModuleController.Update), PermissionAction.Edit)]
    [InlineData(nameof(ModuleController.Delete), PermissionAction.Delete)]
    public void Module_routes_are_gated_by_the_roles_and_permissions_module(
        string actionName, PermissionAction action)
    {
        var method = typeof(ModuleController).GetMethods().Single(x => x.Name == actionName);
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>(true));

        Assert.Equal("Roles & Permissions", permission.ModuleName);
        Assert.Equal(action, permission.Action);
    }

    [Fact]
    public async Task Module_mutations_reject_a_non_super_admin()
    {
        var repository = new RecordingModuleRepository();
        // Manager, not super admin: the platform-global Modules table is still off limits.
        var controller = Modules(repository, callerRoleId: 7,
            gate: new StubRoleGate { ManagerRoleIds = { 7 } });

        Assert.IsType<ForbidResult>((await controller.Create(new DTOs.ModuleDTOs.ModuleCreateRequestDTO
        {
            ModuleName = "Forged module"
        })).Result);
        Assert.IsType<ForbidResult>(await controller.Update(1, new DTOs.ModuleDTOs.ModuleUpdateRequestDTO
        {
            ModuleName = "Renamed module"
        }));
        Assert.IsType<ForbidResult>(await controller.Delete(1));

        Assert.False(repository.WasMutated);
    }

    [Fact]
    public async Task Module_delete_allows_a_super_admin()
    {
        var repository = new RecordingModuleRepository();
        var controller = Modules(repository, callerRoleId: 1,
            gate: new StubRoleGate { SuperAdminRoleIds = { 1 } });

        Assert.IsType<NoContentResult>(await controller.Delete(1));
        Assert.True(repository.WasMutated);
    }

    [Theory]
    [InlineData(typeof(CountryController))]
    [InlineData(typeof(StateController))]
    [InlineData(typeof(CityController))]
    [InlineData(typeof(UomController))]
    public void Reference_data_mutations_require_an_elevated_role(Type controller)
    {
        foreach (var actionName in new[] { "Create", "Update", "Delete" })
        {
            var method = controller.GetMethods().Single(x => x.Name == actionName);
            Assert.True(
                method.GetCustomAttributes<RequireManagerRoleAttribute>(true).Any(),
                $"{controller.Name}.{actionName} must carry [RequireManagerRole].");
        }
    }

    // ------------------------------------------- H7: ungated bulk import / export

    public static TheoryData<Type, string, string, PermissionAction> UploaderRoutes() => new()
    {
        { typeof(ProductUploaderController), "DownloadTemplate", "Products", PermissionAction.View },
        { typeof(ProductUploaderController), "UploadTemplate", "Products", PermissionAction.Create },
        { typeof(ProductUploaderController), "ExportProducts", "Products", PermissionAction.View },

        { typeof(SupplierUploaderController), "DownloadTemplate", "Suppliers", PermissionAction.View },
        { typeof(SupplierUploaderController), "UploadTemplate", "Suppliers", PermissionAction.Create },
        { typeof(SupplierUploaderController), "ExportData", "Suppliers", PermissionAction.View },

        { typeof(RfqUploaderController), "DownloadTemplate", "RFQ Management", PermissionAction.View },
        { typeof(RfqUploaderController), "UploadTemplate", "RFQ Management", PermissionAction.Create },

        { typeof(QuotationUploaderController), "DownloadTemplate", "Quotations", PermissionAction.View },
        { typeof(QuotationUploaderController), "UploadTemplate", "Quotations", PermissionAction.Create },

        { typeof(ProductCategoryUploaderController), "DownloadCategoryTemplate", "Product Categories", PermissionAction.View },
        { typeof(ProductCategoryUploaderController), "UploadCategoryTemplate", "Product Categories", PermissionAction.Create },
        { typeof(ProductCategoryUploaderController), "ExportCategoryData", "Product Categories", PermissionAction.View },
        { typeof(ProductCategoryUploaderController), "DownloadSubCategoryTemplate", "Product Categories", PermissionAction.View },
        { typeof(ProductCategoryUploaderController), "UploadSubCategoryTemplate", "Product Categories", PermissionAction.Create },
        { typeof(ProductCategoryUploaderController), "ExportSubCategoryData", "Product Categories", PermissionAction.View }
    };

    [Theory]
    [MemberData(nameof(UploaderRoutes))]
    public void Bulk_import_and_export_routes_carry_the_correct_module_permission(
        Type controller, string actionName, string module, PermissionAction action)
    {
        var method = controller.GetMethods().Single(x => x.Name == actionName);
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>(true));

        Assert.Equal(module, permission.ModuleName);
        Assert.Equal(action, permission.Action);
    }

    [Fact]
    public void No_uploader_controller_action_is_left_authorize_only()
    {
        var uploaders = typeof(CustomerUploaderController).Assembly.GetTypes()
            .Where(x => x.Name.EndsWith("UploaderController", StringComparison.Ordinal) && !x.IsAbstract)
            .ToArray();

        Assert.NotEmpty(uploaders);

        var ungated = uploaders
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>(true).Any())
            .Where(method => !method.GetCustomAttributes<RequireModulePermissionAttribute>(true).Any())
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToArray();

        Assert.Empty(ungated);
    }

    // ------------------------------------------------------------------- helpers

    private static RolePermissionController RolePermissions(
        IRolePermissionRepository repository, long? callerRoleId, IRoleGate? gate = null)
        => new(repository, gate ?? new StubRoleGate())
        {
            ControllerContext = Context(Tenant, callerRoleId)
        };

    private static BusinessUnitController BusinessUnits(IBusinessUnitRepository repository, long? tenantId)
        => new(repository) { ControllerContext = Context(tenantId, null) };

    private static ModuleController Modules(IModuleRepository repository, long? callerRoleId, IRoleGate gate)
        => new(repository, gate) { ControllerContext = Context(Tenant, callerRoleId) };

    private static ControllerContext Context(long? tenantId, long? roleId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user") };
        if (tenantId.HasValue) claims.Add(new Claim("businessUnitId", tenantId.Value.ToString()));
        if (roleId.HasValue) claims.Add(new Claim("roleId", roleId.Value.ToString()));

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
    }

    private sealed class RecordingRolePermissionRepository : IRolePermissionRepository
    {
        /// <summary>The caller's own grant on the requested module, if any.</summary>
        public RolePermission? CallerGrant { get; init; }

        public RolePermission? Existing { get; init; }

        public RolePermission? Added { get; private set; }
        public bool Deleted { get; private set; }
        public bool Updated { get; private set; }

        public Task<(IEnumerable<RolePermission>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, long? roleId, long? moduleId, long businessUnitId)
        {
            IEnumerable<RolePermission> rows = CallerGrant is not null
                && CallerGrant.RoleId == roleId
                && CallerGrant.ModuleId == moduleId
                    ? [CallerGrant]
                    : [];
            return Task.FromResult((rows, rows.Count()));
        }

        public Task<RolePermission> GetByIdAsync(long id, long businessUnitId) =>
            Task.FromResult(Existing ?? new RolePermission
            {
                Id = id, BusinessUnitId = businessUnitId, ModuleId = 1, RoleId = 1
            });

        public Task AddAsync(RolePermission rolePermission)
        {
            Added = rolePermission;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(RolePermission rolePermission)
        {
            Updated = true;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, long businessUnitId)
        {
            Deleted = true;
            return Task.CompletedTask;
        }

        public Task<bool> CheckPermissionAsync(long roleId, string moduleName, string action, long businessUnitId)
            => Task.FromResult(false);
    }

    private sealed class RecordingBusinessUnitRepository : IBusinessUnitRepository
    {
        public long? LastId { get; private set; }
        public bool WasQueried { get; private set; }

        public Task<(IEnumerable<BusinessUnit>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? businessUnitName)
        {
            WasQueried = true;
            LastId = id;
            return Task.FromResult<(IEnumerable<BusinessUnit>, int)>(([], 0));
        }

        public Task<BusinessUnit> GetByIdAsync(long id)
        {
            WasQueried = true;
            return Task.FromResult(new BusinessUnit { Id = id });
        }

        public Task AddAsync(BusinessUnit businessUnit) => Task.CompletedTask;
        public Task UpdateAsync(BusinessUnit businessUnit) => Task.CompletedTask;
        public Task DeleteAsync(long id) => Task.CompletedTask;
    }

    private sealed class RecordingModuleRepository : IModuleRepository
    {
        public bool WasMutated { get; private set; }

        public Task<(IEnumerable<ModuleEntity>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? moduleName, bool? isActive)
            => Task.FromResult<(IEnumerable<ModuleEntity>, int)>(([], 0));

        public Task<ModuleEntity> GetByIdAsync(long id) =>
            Task.FromResult(new ModuleEntity { Id = id, ModuleName = "Leads", CreatedBy = "test" });

        public Task AddAsync(ModuleEntity module)
        {
            WasMutated = true;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ModuleEntity module)
        {
            WasMutated = true;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id)
        {
            WasMutated = true;
            return Task.CompletedTask;
        }
    }
}
