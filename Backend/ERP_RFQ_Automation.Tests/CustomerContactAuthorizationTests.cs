using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.FileProviders;

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
        string? businessUnitClaim)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user") };
        if (businessUnitClaim is not null)
            claims.Add(new Claim("businessUnitId", businessUnitClaim));

        return new CustomerController(repository, new TestWebHostEnvironment())
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

    private sealed class CapturingCustomerRepository : ICustomerRepository
    {
        public string? CapturedEmail { get; private set; }
        public long? CapturedBusinessUnitId { get; private set; }
        public Customer? Result { get; init; }
        public Exception? Exception { get; init; }

        public Task<Customer?> GetByEmailAsync(string email, long businessUnitId)
        {
            CapturedEmail = email;
            CapturedBusinessUnitId = businessUnitId;
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(Result);
        }

        public Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? name, string? contactEmail,
            bool? isActive, string? docId, long businessUnitId) => throw new NotSupportedException();

        public Task<Customer> GetByIdAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task AddAsync(Customer customer) => throw new NotSupportedException();
        public Task UpdateAsync(Customer customer, long businessUnitId) => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new NotSupportedException();
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
