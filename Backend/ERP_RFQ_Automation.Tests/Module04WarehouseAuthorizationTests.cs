using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.Warehouse;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class Module04WarehouseAuthorizationTests
{
    [Theory]
    [InlineData(nameof(WarehouseController.GetAll), PermissionAction.View)]
    [InlineData(nameof(WarehouseController.GetById), PermissionAction.View)]
    [InlineData(nameof(WarehouseController.Create), PermissionAction.Create)]
    [InlineData(nameof(WarehouseController.Update), PermissionAction.Edit)]
    [InlineData(nameof(WarehouseController.Delete), PermissionAction.Delete)]
    public void Routes_require_matching_product_permission(string action, PermissionAction permission)
    {
        var attribute = Assert.Single(typeof(WarehouseController).GetMethods().Single(x => x.Name == action)
            .GetCustomAttributes<RequireModulePermissionAttribute>());

        Assert.Equal("Products", attribute.ModuleName);
        Assert.Equal(permission, attribute.Action);
    }

    [Fact]
    public async Task Caller_tenant_cannot_replace_missing_authenticated_tenant_claim()
    {
        var repository = new RecordingWarehouseRepository();
        var controller = Controller(repository);

        Assert.IsType<BadRequestObjectResult>((await controller.GetAll(businessUnitId: 999)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetById(1, businessUnitId: 999)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.Create(CreateRequest(999))).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(1, UpdateRequest(999)));
        Assert.IsType<BadRequestObjectResult>(await controller.Delete(1, businessUnitId: 999));
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task Authenticated_tenant_is_authoritative_for_every_repository_operation()
    {
        const long tenantId = 41;
        var repository = new RecordingWarehouseRepository
        {
            Warehouse = Warehouse(1, tenantId)
        };
        var controller = Controller(repository, tenantId);

        await controller.GetAll(businessUnitId: 999);
        await controller.GetById(1, businessUnitId: 999);
        await controller.Create(CreateRequest(999));
        await controller.Update(1, UpdateRequest(999));
        await controller.Delete(1, businessUnitId: 999);

        Assert.Equal([tenantId, tenantId, tenantId, tenantId, tenantId, tenantId], repository.ObservedTenantIds);
        Assert.Equal(tenantId, repository.Added!.BusinessUnitId);
        Assert.Equal(tenantId, repository.Updated!.BusinessUnitId);
    }

    [Fact]
    public async Task Cross_tenant_record_is_not_disclosed()
    {
        var repository = new RecordingWarehouseRepository
        {
            GetByIdException = new KeyNotFoundException("Warehouse 7 belongs to tenant 99.")
        };
        var controller = Controller(repository, 41);

        var result = await controller.GetById(7, businessUnitId: 99);

        var notFound = Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal([41L], repository.ObservedTenantIds);
    }

    [Fact]
    public async Task Validation_and_lifecycle_failures_return_safe_statuses()
    {
        var invalidRepository = new RecordingWarehouseRepository
        {
            AddException = new ArgumentException("private validation detail")
        };
        var invalid = await Controller(invalidRepository, 41).Create(CreateRequest(41));
        var badRequest = Assert.IsType<BadRequestObjectResult>(invalid.Result);
        Assert.DoesNotContain("private", Assert.IsType<string>(badRequest.Value), StringComparison.OrdinalIgnoreCase);

        var conflictRepository = new RecordingWarehouseRepository
        {
            DeleteException = new InvalidOperationException("private dependency detail")
        };
        var conflict = await Controller(conflictRepository, 41).Delete(1);
        var conflictResult = Assert.IsType<ConflictObjectResult>(conflict);
        Assert.DoesNotContain("private", Assert.IsType<string>(conflictResult.Value), StringComparison.OrdinalIgnoreCase);
    }

    private static WarehouseController Controller(RecordingWarehouseRepository repository, long? tenantId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "warehouse-test-user") };
        if (tenantId.HasValue) claims.Add(new Claim("businessUnitId", tenantId.Value.ToString()));

        return new WarehouseController(repository)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static WarehouseCreateRequestDTO CreateRequest(long tenantId) => new()
    {
        WarehouseCode = "WH-01",
        WarehouseName = "Primary",
        BusinessUnitId = tenantId
    };

    private static WarehouseUpdateRequestDTO UpdateRequest(long tenantId) => new()
    {
        WarehouseCode = "WH-01",
        WarehouseName = "Primary updated",
        BusinessUnitId = tenantId,
        IsActive = true
    };

    private static Warehouse Warehouse(long id, long tenantId) => new()
    {
        Id = id,
        WarehouseCode = "WH-01",
        WarehouseName = "Primary",
        BusinessUnitId = tenantId,
        IsActive = true,
        CreatedBy = "warehouse-test-user",
        CreatedOn = DateTime.UtcNow
    };

    private sealed class RecordingWarehouseRepository : IWarehouseRepository
    {
        public int CallCount { get; private set; }
        public List<long> ObservedTenantIds { get; } = [];
        public Warehouse? Warehouse { get; init; }
        public Warehouse? Added { get; private set; }
        public Warehouse? Updated { get; private set; }
        public Exception? GetByIdException { get; init; }
        public Exception? AddException { get; init; }
        public Exception? DeleteException { get; init; }

        public Task<IEnumerable<Warehouse>> GetAllAsync(long businessUnitId)
        {
            Record(businessUnitId);
            return Task.FromResult<IEnumerable<Warehouse>>(Warehouse == null ? [] : [Warehouse]);
        }

        public Task<Warehouse> GetByIdAsync(long id, long businessUnitId)
        {
            Record(businessUnitId);
            if (GetByIdException != null) return Task.FromException<Warehouse>(GetByIdException);
            return Task.FromResult(Warehouse ?? Module04WarehouseAuthorizationTests.Warehouse(id, businessUnitId));
        }

        public Task AddAsync(Warehouse warehouse)
        {
            Record(warehouse.BusinessUnitId);
            if (AddException != null) return Task.FromException(AddException);
            Added = warehouse;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Warehouse warehouse)
        {
            Record(warehouse.BusinessUnitId);
            Updated = warehouse;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, long businessUnitId)
        {
            Record(businessUnitId);
            return DeleteException == null ? Task.CompletedTask : Task.FromException(DeleteException);
        }

        private void Record(long businessUnitId)
        {
            CallCount++;
            ObservedTenantIds.Add(businessUnitId);
        }
    }
}
