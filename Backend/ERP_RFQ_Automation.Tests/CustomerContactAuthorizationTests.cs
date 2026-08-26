using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.Contact;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class CustomerContactAuthorizationTests
{
    [Fact]
    public void ContactActions_UseParentAwarePermissionsWhileDropdownsRemainStatic()
    {
        var parentAware = new[]
        {
            nameof(ContactController.GetAll), nameof(ContactController.GetById),
            nameof(ContactController.Create), nameof(ContactController.Update),
            nameof(ContactController.Delete)
        };
        foreach (var actionName in parentAware)
            Assert.Empty(typeof(ContactController).GetMethod(actionName)!
                .GetCustomAttributes<RequireModulePermissionAttribute>(true));

        var customers = Assert.Single(typeof(ContactController).GetMethod(nameof(ContactController.GetCustomers))!
            .GetCustomAttributes<RequireModulePermissionAttribute>(true));
        Assert.Equal("Customers", customers.ModuleName);
        Assert.Equal(PermissionAction.View, customers.Action);

        var suppliers = Assert.Single(typeof(ContactController).GetMethod(nameof(ContactController.GetSuppliers))!
            .GetCustomAttributes<RequireModulePermissionAttribute>(true));
        Assert.Equal("Suppliers", suppliers.ModuleName);
        Assert.Equal(PermissionAction.View, suppliers.Action);
    }

    [Fact]
    public async Task SupplierContactRead_UsesSupplierPermissionAndCustomerContactUsesCustomerPermission()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(41);
        var repository = new CapturingContactRepository();
        var authorization = new RecordingAuthorizationService(policy =>
            policy.Contains("Suppliers:View", StringComparison.Ordinal));
        var controller = CreateContactController(repository, context, authorization, 41);

        repository.Result = new Contact { Id = 7, SupplierId = 11, FirstName = "Supply", LastName = "Owner" };
        Assert.IsType<OkObjectResult>((await controller.GetById(7)).Result);
        Assert.Contains(authorization.Policies, policy => policy.Contains("Suppliers:View", StringComparison.Ordinal));

        repository.Result = new Contact { Id = 8, CustomerId = 12, FirstName = "Buyer", LastName = "Owner" };
        Assert.IsType<ForbidResult>((await controller.GetById(8)).Result);
        Assert.Contains(authorization.Policies, policy => policy.Contains("Customers:View", StringComparison.Ordinal));
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
            Name = "Forged customer"
        })).Result);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(7, new CustomerUpdateRequestDTO
        {
            Name = "Forged customer", ConcurrencyToken = Guid.NewGuid()
        }));
        Assert.IsType<BadRequestObjectResult>(await controller.Delete(7, Guid.NewGuid()));
        Assert.False(repository.WasAccessed);
    }

    [Fact]
    public async Task CustomerCreate_UsesAuthenticatedTenantAndActor()
    {
        var repository = new CapturingCustomerRepository();
        var controller = CreateCustomerController(repository, "41");

        var result = await controller.Create(new CustomerCreateRequestDTO
        {
            Name = "Authorized customer"
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(41, repository.AddedCustomer?.Buid);
        // The actor is the token's NameIdentifier. It is "77" rather than the old literal
        // "test-user" only because this fixture now issues a NUMERIC user id, which FR-CST-02's
        // scope resolution requires and which every real token already carries. The assertion is
        // unchanged in substance: the actor comes from the claim, never from the request body.
        Assert.Equal("77", repository.AddedCustomer?.CreatedBy);
    }

    [Fact]
    public async Task CustomerCreate_MapsDatabase_identity_race_to_safe_conflict()
    {
        var repository = new CapturingCustomerRepository
        {
            Exception = new Microsoft.EntityFrameworkCore.DbUpdateException("provider details")
        };
        var controller = CreateCustomerController(repository, "41");

        var result = await controller.Create(new CustomerCreateRequestDTO { Name = "Racing customer" });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.DoesNotContain("provider", conflict.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
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
            Name = "Customer", ImageFile = image
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
        // A numeric user id and a role id, because FR-CST-02 resolves the caller's account scope
        // from both and refuses a request it cannot scope. The identifier used to be the literal
        // "test-user", which no token in production carries.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "77"),
            new("roleId", "9")
        };
        if (businessUnitClaim is not null)
            claims.Add(new Claim("businessUnitId", businessUnitClaim));

        return new CustomerController(
            repository, new TestWebHostEnvironment(), fileInspection ?? new ClearingFileInspectionService(),
            new StubMasterDataChangeHistoryReader(), new TenantWideScopeResolver())
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

    private static ContactController CreateContactController(
        IContactRepository repository,
        ErpRfqAutomationContext context,
        IAuthorizationService authorization,
        long tenant)
    {
        return new ContactController(repository, context, authorization)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
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

    /// <summary>
    /// These tests are about the TENANT boundary and the permission attributes, not about the
    /// account-team tier, so the scope resolver is stubbed tenant-wide here. The account-team
    /// narrowing is certified separately by Gate8AccountTeamScopeTests against real rows.
    /// </summary>
    private sealed class TenantWideScopeResolver : IAccountTeamScopeResolver
    {
        public Task<AccountTeamScope> ResolveAsync(
            long userId, long roleId, long businessUnitId, DateTime asOfUtc, CancellationToken ct = default)
            => Task.FromResult(AccountTeamScope.TenantWide(userId));
    }

    private sealed class CapturingCustomerRepository : ICustomerRepository
    {
        public string? CapturedEmail { get; private set; }
        public long? CapturedBusinessUnitId { get; private set; }
        public Customer? Result { get; init; }
        public Exception? Exception { get; init; }
        public Customer? AddedCustomer { get; private set; }
        public bool WasAccessed { get; private set; }

        public Task<Customer?> GetByEmailAsync(string email, long businessUnitId, AccountTeamScope scope)
        {
            CapturedScope = scope;
            WasAccessed = true;
            CapturedEmail = email;
            CapturedBusinessUnitId = businessUnitId;
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(Result);
        }

        public Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? name, string? contactEmail,
            bool? isActive, string? docId, long businessUnitId, AccountTeamScope scope)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            CapturedScope = scope;
            return Task.FromResult<(IEnumerable<CustomerResponseDTO>, int)>(([], 0));
        }

        /// <summary>FR-CST-02: the scope the controller resolved and handed down. Captured so a
        /// test can assert the controller does not read customers unscoped.</summary>
        public AccountTeamScope? CapturedScope { get; private set; }

        public Task<Customer> GetByIdAsync(long id, long businessUnitId, AccountTeamScope scope)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            CapturedScope = scope;
            return Task.FromResult(Result ?? new Customer { Id = id, Name = "Customer", Buid = businessUnitId });
        }

        public Task AddAsync(Customer customer, long businessUnitId, string actor)
        {
            WasAccessed = true;
            if (Exception is not null)
                throw Exception;
            customer.Buid = businessUnitId;
            customer.CreatedBy = actor;
            AddedCustomer = customer;
            return Task.CompletedTask;
        }

        public Task AddOwnedAsync(Customer customer, long businessUnitId, string actor, long ownerUserId) =>
            AddAsync(customer, businessUnitId, actor);

        public Task UpdateAsync(Customer customer, long businessUnitId, string actor, Guid expectedConcurrencyToken)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken)
        {
            WasAccessed = true;
            CapturedBusinessUnitId = businessUnitId;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingContactRepository : IContactRepository
    {
        public Contact Result { get; set; } = null!;

        public Task<Contact> GetByIdAsync(long id, long businessUnitId) => Task.FromResult(Result);
        public Task<(IEnumerable<ContactResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? firstName, string? lastName,
            string? email, long? customerId, long? supplierId, bool? isPrimary,
            bool? isActive, long businessUnitId) => throw new NotSupportedException();
        public Task AddAsync(Contact contact, long businessUnitId, string actor) => throw new NotSupportedException();
        public Task UpdateAsync(Contact contact, long businessUnitId, string actor, Guid expectedConcurrencyToken) => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken) => throw new NotSupportedException();
        public Task<IEnumerable<CustomerDropdown>> GetCustomersAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<IEnumerable<SupplierDropDown>> GetSuppliersAsync(long businessUnitId) => throw new NotSupportedException();
    }

    private sealed class RecordingAuthorizationService(Func<string, bool> authorize) : IAuthorizationService
    {
        public List<string> Policies { get; } = [];

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName)
        {
            Policies.Add(policyName);
            return Task.FromResult(authorize(policyName)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());
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
