using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class CustomerContactAuthorizationTests
{
    [Fact]
    public void ContactActions_RequireCustomerModulePermissions()
    {
        var expected = new Dictionary<string, PermissionAction>
        {
            [nameof(ContactController.GetAll)] = PermissionAction.View,
            [nameof(ContactController.GetById)] = PermissionAction.View,
            [nameof(ContactController.Create)] = PermissionAction.Create,
            [nameof(ContactController.Update)] = PermissionAction.Edit,
            [nameof(ContactController.Delete)] = PermissionAction.Delete,
            [nameof(ContactController.GetCustomers)] = PermissionAction.View,
            [nameof(ContactController.GetSuppliers)] = PermissionAction.View
        };
        var actions = typeof(ContactController).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(true).Any())
            .ToArray();

        Assert.Equal(expected.Keys.Order(), actions.Select(action => action.Name).Order());
        foreach (var action in actions)
        {
            var permission = Assert.Single(action.GetCustomAttributes<RequireModulePermissionAttribute>(true));
            Assert.Equal("Customers", permission.ModuleName);
            Assert.Equal(expected[action.Name], permission.Action);
        }
    }

    [Fact]
    public void CustomerByEmail_RequiresViewPermissionAndDoesNotAcceptTenantInput()
    {
        var action = typeof(CustomerController).GetMethod(nameof(CustomerController.GetByEmail))!;
        var permission = Assert.Single(action.GetCustomAttributes<RequireModulePermissionAttribute>(true));

        Assert.Equal("Customers", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
        Assert.DoesNotContain(action.GetParameters(), parameter =>
            string.Equals(parameter.Name, "businessUnitId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CustomerCrudAndSpreadsheetActions_RequirePermissionsAndDoNotAcceptTenantInput()
    {
        var expected = new Dictionary<MethodInfo, PermissionAction[]>
        {
            [typeof(CustomerController).GetMethod(nameof(CustomerController.GetAll))!] = [PermissionAction.View],
            [typeof(CustomerController).GetMethod(nameof(CustomerController.GetById))!] = [PermissionAction.View],
            [typeof(CustomerController).GetMethod(nameof(CustomerController.Create))!] = [PermissionAction.Create],
            [typeof(CustomerController).GetMethod(nameof(CustomerController.Update))!] = [PermissionAction.Edit],
            [typeof(CustomerController).GetMethod(nameof(CustomerController.Delete))!] = [PermissionAction.Delete],
            [typeof(CustomerUploaderController).GetMethod(nameof(CustomerUploaderController.DownloadTemplate))!] = [PermissionAction.View],
            [typeof(CustomerUploaderController).GetMethod(nameof(CustomerUploaderController.UploadTemplate))!] = [PermissionAction.Create, PermissionAction.Edit],
            [typeof(CustomerUploaderController).GetMethod(nameof(CustomerUploaderController.ExportData))!] = [PermissionAction.View]
        };

        foreach (var (action, permissionActions) in expected)
        {
            var permissions = action.GetCustomAttributes<RequireModulePermissionAttribute>(true).ToArray();
            Assert.Equal(permissionActions.Order(), permissions.Select(x => x.Action).Order());
            Assert.All(permissions, permission => Assert.Equal("Customers", permission.ModuleName));
            Assert.DoesNotContain(action.GetParameters(), parameter =>
                string.Equals(parameter.Name, "businessUnitId", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    public async Task CustomerCrud_RejectsMissingOrInvalidAuthenticatedTenant(string? claimValue)
    {
        var repository = new CapturingCustomerRepository();
        var controller = CreateCustomerController(repository, claimValue);

        Assert.IsType<BadRequestObjectResult>((await controller.GetAll()).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetById(7)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.Create(new CustomerCreateRequestDTO
        {
            Name = "Forged customer", Buid = 999, CreatedBy = "attacker"
        })).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(7, new CustomerUpdateRequestDTO
        {
            Name = "Forged customer", Buid = 999, ModifiedBy = "attacker"
        }));
        Assert.IsType<BadRequestObjectResult>(await controller.Delete(7));
        Assert.False(repository.WasAccessed);
    }

    [Fact]
    public async Task CustomerCreate_OverridesForgedFormTenantWithAuthenticatedTenant()
    {
        var repository = new CapturingCustomerRepository();
        var controller = CreateCustomerController(repository, "41");

        var result = await controller.Create(new CustomerCreateRequestDTO
        {
            Name = "Authorized customer", Buid = 999, CreatedBy = "test-user"
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(41, repository.AddedCustomer?.Buid);
        Assert.Equal("test-user", repository.AddedCustomer?.CreatedBy);
    }

    [Fact]
    public async Task CustomerCreate_RejectsUninspectedProfileImageWithoutWritingCustomer()
    {
        var repository = new CapturingCustomerRepository();
        var controller = CreateCustomerController(repository, "41", new RejectingFileInspectionService());
        var image = new FormFile(new MemoryStream("<svg/>"u8.ToArray()), 0, 6, "image", "../profile.svg")
        {
            Headers = new HeaderDictionary(), ContentType = "image/svg+xml"
        };

        var result = await controller.Create(new CustomerCreateRequestDTO
        {
            Name = "Customer", Buid = 41, CreatedBy = "forged", ImageFile = image
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(repository.AddedCustomer);
    }

    [Fact]
    public async Task CustomerSpreadsheetImport_FailsClosedWhenFileInspectionRejectsContent()
    {
        const long tenant = 41;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        var inspection = new RejectingFileInspectionService();
        var controller = new CustomerUploaderController(
            new CustomerUploaderService(context, NullLogger<CustomerUploaderService>.Instance),
            inspection,
            NullLogger<CustomerUploaderController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };
        var file = new FormFile(new MemoryStream("not-an-xlsx"u8.ToArray()), 0, 11, "file", "customers.xlsx")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };

        var result = await controller.UploadTemplate(file, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(inspection.WasCalled);
        Assert.Empty(context.Customers);
    }

    [Fact]
    public void Evidence_and_lead_metadata_reads_require_leads_view_permission()
    {
        var methods = new[]
        {
            typeof(FileController).GetMethod(nameof(FileController.DownloadAttachment))!,
            typeof(LeadController).GetMethod(nameof(LeadController.GetEmailConfigurations))!,
            typeof(LeadController).GetMethod(nameof(LeadController.GetRejectionReasons))!
        };

        foreach (var method in methods)
        {
            var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>(true));
            Assert.Equal("Leads", permission.ModuleName);
            Assert.Equal(PermissionAction.View, permission.Action);
        }
    }

    [Fact]
    public async Task CustomerByEmail_UsesAuthenticatedTenantAndIgnoresForgedQueryTenant()
    {
        const long authenticatedTenant = 41;
        var repository = new CapturingCustomerRepository
        {
            Result = new Customer { Id = 7, Name = "Authorized customer", Buid = authenticatedTenant }
        };
        var controller = CreateCustomerController(repository, authenticatedTenant.ToString());
        controller.HttpContext.Request.QueryString = new QueryString("?email=buyer%40example.com&businessUnitId=999");

        var result = await controller.GetByEmail("buyer@example.com");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<CustomerResponseDTO>(ok.Value);
        Assert.Equal(authenticatedTenant, repository.CapturedBusinessUnitId);
        Assert.Equal("buyer@example.com", repository.CapturedEmail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    public async Task CustomerByEmail_RejectsMissingOrInvalidAuthenticatedTenant(string? claimValue)
    {
        var repository = new CapturingCustomerRepository();
        var controller = CreateCustomerController(repository, claimValue);

        var result = await controller.GetByEmail("buyer@example.com");

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(repository.CapturedBusinessUnitId);
    }

    [Fact]
    public async Task CustomerByEmail_DoesNotExposeRepositoryExceptionDetails()
    {
        var repository = new CapturingCustomerRepository
        {
            Exception = new InvalidOperationException("database host and secret diagnostics")
        };
        var controller = CreateCustomerController(repository, "41");

        var result = await controller.GetByEmail("buyer@example.com");

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, error.StatusCode);
        Assert.Equal("Unable to retrieve the customer.", error.Value);
    }

    private static CustomerController CreateCustomerController(
        ICustomerRepository repository,
        string? businessUnitClaim,
        IFileInspectionService? fileInspection = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user") };
        if (businessUnitClaim is not null)
            claims.Add(new Claim("businessUnitId", businessUnitClaim));

        return new CustomerController(repository, new TestWebHostEnvironment(), fileInspection ?? new ClearingFileInspectionService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static ClaimsPrincipal Principal(long tenant) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim("businessUnitId", tenant.ToString())],
        "Test"));

    private sealed class RejectingFileInspectionService : IFileInspectionService
    {
        public bool WasCalled { get; private set; }

        public Task<FileInspectionResult> InspectAsync(FileInspectionRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new FileInspectionResult(
                FileInspectionStatus.Rejected, null, request.DeclaredLength ?? 0,
                "Invalid spreadsheet signature.", "test", null));
        }
    }

    private sealed class ClearingFileInspectionService : IFileInspectionService
    {
        public Task<FileInspectionResult> InspectAsync(FileInspectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileInspectionResult(
                FileInspectionStatus.Cleared, "image/png", request.DeclaredLength ?? 0,
                "Cleared", "test", null));
    }

    private sealed class CapturingCustomerRepository : ICustomerRepository
    {
        public string? CapturedEmail { get; private set; }
        public long? CapturedBusinessUnitId { get; private set; }
        public Customer? Result { get; init; }
        public Exception? Exception { get; init; }
        public Customer? AddedCustomer { get; private set; }
        public bool WasAccessed { get; private set; }

        public Task<Customer?> GetByEmailAsync(string email, long businessUnitId)
        {
            WasAccessed = true;
            CapturedEmail = email;
            CapturedBusinessUnitId = businessUnitId;
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(Result);
        }

        public Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? name, string? contactEmail,
            bool? isActive, string? docId, long businessUnitId)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            return Task.FromResult<(IEnumerable<CustomerResponseDTO>, int)>(([], 0));
        }

        public Task<Customer> GetByIdAsync(long id, long businessUnitId)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            return Task.FromResult(Result ?? new Customer { Id = id, Name = "Customer", Buid = businessUnitId });
        }

        public Task AddAsync(Customer customer)
        {
            WasAccessed = true;
            AddedCustomer = customer;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Customer customer, long businessUnitId)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, long businessUnitId)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            return Task.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Nexora.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
