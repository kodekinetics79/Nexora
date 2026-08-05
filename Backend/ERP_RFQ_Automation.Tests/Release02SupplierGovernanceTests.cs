using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.Contact;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02SupplierGovernanceTests
{
    [Fact]
    public void Supplier_routes_require_supplier_permissions_and_never_accept_tenant_input()
    {
        var actions = typeof(SupplierController).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(true).Any())
            .ToArray();

        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            var permission = Assert.Single(action.GetCustomAttributes<RequireModulePermissionAttribute>(true));
            Assert.Equal("Suppliers", permission.ModuleName);
            Assert.DoesNotContain(action.GetParameters(), parameter =>
                string.Equals(parameter.Name, "businessUnitId", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    public async Task Supplier_search_fails_closed_without_valid_authenticated_tenant(string? claimValue)
    {
        var repository = new RecordingSupplierRepository();
        var controller = CreateSupplierController(repository, claimValue);

        var result = await controller.Search("bearing", null);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.SearchTenant);
    }

    [Fact]
    public async Task Supplier_search_uses_claim_tenant_even_when_query_contains_forged_tenant()
    {
        var repository = new RecordingSupplierRepository();
        var controller = CreateSupplierController(repository, "41");
        controller.HttpContext.Request.QueryString = new QueryString("?searchTerm=bearing&businessUnitId=999");

        var result = await controller.Search("bearing", null);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(41, repository.SearchTenant);
    }

    [Fact]
    public void External_supplier_discovery_is_explicitly_disabled_and_returns_no_fabricated_results()
    {
        var controller = CreateSupplierController(new RecordingSupplierRepository(), "41");

        var result = controller.WebSearch("industrial bearing");

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        // The refusal is an RFC 7807 payload carrying a traceId, not a bare string, so a
        // caller can quote one identifier that ties straight back to the server log entry.
        var problem = Assert.IsType<ProblemDetails>(unavailable.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
        Assert.Contains("disabled", problem.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
        Assert.DoesNotContain(typeof(ISupplierRepository).GetMethods(), method =>
            method.Name.Contains("Web", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Supplier_create_uses_server_tenant_actor_and_unknown_safe_governance_defaults()
    {
        var repository = new RecordingSupplierRepository();
        var controller = CreateSupplierController(repository, "41", "user-7");

        var result = await controller.Create(new SupplierCreateRequestDTO
        {
            Name = "Verified Parts Ltd"
        });

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var supplier = Assert.IsType<Supplier>(repository.Added);
        Assert.Equal(41, supplier.Buid);
        Assert.Equal("user-7", supplier.CreatedBy);
        Assert.Null(supplier.SuccessRate);
        Assert.Null(supplier.AvgResponseTime);
        Assert.Equal(SupplierGovernanceStatuses.Unverified, supplier.GovernanceStatus);
        Assert.Equal(SupplierGovernanceUnknown.Unknown, supplier.VerificationStatus);
        Assert.Equal(SupplierGovernanceUnknown.Unknown, supplier.ComplianceStatus);
        Assert.Equal(SupplierReadinessStatuses.ReviewRequired, supplier.ReadinessStatus);
    }

    [Fact]
    public void Ordinary_supplier_write_contracts_exclude_tenant_actor_metrics_and_governance()
    {
        var forbiddenProperties = new[]
        {
            "Buid", "BusinessUnitId", "CreatedBy", "ModifiedBy", "SuccessRate",
            "AvgResponseTime", "GovernanceStatus", "VerificationStatus",
            "ComplianceStatus", "RiskStatus", "ReadinessStatus", "IsActive", "ImageFile"
        };

        foreach (var dtoType in new[] { typeof(SupplierCreateRequestDTO), typeof(SupplierUpdateRequestDTO) })
        {
            var properties = dtoType.GetProperties().Select(property => property.Name).ToHashSet();
            Assert.Empty(forbiddenProperties.Where(properties.Contains));
        }
    }

    [Fact]
    public async Task Changing_dispatch_email_revokes_prior_governance_and_readiness()
    {
        var repository = new RecordingSupplierRepository();
        repository.CurrentSupplier = new Supplier
        {
            Id = 9, Buid = 41, Name = "Approved Parts", ContactEmail = "old@supplier.test",
            ImageUrl = string.Empty, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            GovernanceStatus = SupplierGovernanceStatuses.Approved,
            VerificationStatus = SupplierVerificationStatuses.Verified,
            ComplianceStatus = SupplierComplianceStatuses.Cleared,
            RiskStatus = SupplierRiskStatuses.Low,
            ReadinessStatus = SupplierReadinessStatuses.Ready,
            ConcurrencyToken = Guid.NewGuid(),
            GovernanceReviewedBy = "manager",
            GovernanceReviewedOn = DateTime.UtcNow
        };
        var controller = CreateSupplierController(repository, "41", "editor@supplier.test");

        var result = await controller.Update(9, new SupplierUpdateRequestDTO
        {
            Name = "Approved Parts", ContactEmail = "new@supplier.test",
            ConcurrencyToken = repository.CurrentSupplier.ConcurrencyToken
        });

        Assert.IsType<NoContentResult>(result);
        var updated = Assert.IsType<Supplier>(repository.Updated);
        Assert.Equal(SupplierGovernanceStatuses.ReviewRequired, updated.GovernanceStatus);
        Assert.Equal(SupplierVerificationStatuses.Pending, updated.VerificationStatus);
        Assert.Equal(SupplierReadinessStatuses.ReviewRequired, updated.ReadinessStatus);
        Assert.Null(updated.GovernanceReviewedBy);
        Assert.Null(updated.GovernanceReviewedOn);
    }

    [Theory]
    [InlineData(1, 1, "SU00000001")]
    [InlineData(99, 42, "SU00000099")]
    [InlineData(123456789, 42, "SU123456789")]
    public void Supplier_number_is_deterministic_from_persisted_identity(
        long supplierId,
        long tenantId,
        string expected)
    {
        var generator = new DeterministicSupplierNumberGenerator();

        Assert.Equal(expected, generator.Generate(supplierId, tenantId));
        Assert.Equal(expected, generator.Generate(supplierId, tenantId));
    }

    [Fact]
    public async Task Supplier_contact_lookup_fails_closed_without_tenant_claim()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new ErpRfqAutomationContext(options);
        var repository = new RecordingContactRepository();
        var controller = new ContactController(repository, context, new AllowAuthorizationService())
        {
            ControllerContext = ControllerContext(null, "user-7")
        };

        var result = await controller.GetSuppliers();

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.SupplierTenant);
    }

    [Fact]
    public async Task Supplier_repository_uses_persisted_identity_for_unique_numbers()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 41);
        await context.SaveChangesAsync();
        var repository = new SupplierRepository(context, new DeterministicSupplierNumberGenerator());
        var first = Supplier("First Parts", 41);
        var second = Supplier("Second Parts", 41);

        await repository.AddAsync(first);
        await repository.AddAsync(second);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal($"SU{first.Id:D8}", first.DocId);
        Assert.Equal($"SU{second.Id:D8}", second.DocId);
        Assert.NotEqual(first.DocId, second.DocId);
    }

    [Fact]
    public async Task Supplier_repository_rejects_cross_tenant_currency_and_physical_deletion()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 41);
        Seed.EnsureBusinessUnit(context, 42);
        context.Currencies.Add(new Currency
        {
            Id = 42_001,
            BusinessUnitId = 42,
            Code = "USD",
            CurrencyName = "Other tenant USD",
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        await context.SaveChangesAsync();
        var repository = new SupplierRepository(context, new DeterministicSupplierNumberGenerator());
        var supplier = Supplier("Tenant A Parts", 41);
        supplier.CurrencyId = 42_001;

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(supplier));
        supplier.CurrencyId = null;
        await repository.AddAsync(supplier);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(supplier.Id, 41));
        Assert.True(await context.Suppliers.AnyAsync(x => x.Id == supplier.Id));
    }

    [Fact]
    public async Task Supplier_contact_repository_rejects_parent_from_another_tenant()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 41);
        Seed.EnsureBusinessUnit(context, 42);
        var otherTenantSupplier = Supplier("Other Tenant Parts", 42);
        context.Suppliers.Add(otherTenantSupplier);
        await context.SaveChangesAsync();
        var repository = new ContactRepository(context);
        var contact = new Contact
        {
            BusinessUnitId = 41,
            SupplierId = otherTenantSupplier.Id,
            FirstName = "Cross",
            LastName = "Tenant",
            CreatedBy = "test-user",
            CreatedOn = DateTime.UtcNow
        };

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(contact, 7, "test-user"));
        Assert.Empty(context.Contacts);
    }

    private static Supplier Supplier(string name, long tenantId) => new()
    {
        Name = name,
        ImageUrl = string.Empty,
        Buid = tenantId,
        IsActive = true,
        CreatedBy = "test-user",
        CreatedOn = DateTime.UtcNow
    };

    private static SupplierController CreateSupplierController(
        ISupplierRepository repository,
        string? tenantClaim,
        string actor = "test-user")
    {
        return new SupplierController(repository)
        {
            ControllerContext = ControllerContext(tenantClaim, actor)
        };
    }

    private static ControllerContext ControllerContext(string? tenantClaim, string actor)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor) };
        if (tenantClaim is not null)
            claims.Add(new Claim("businessUnitId", tenantClaim));

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private sealed class RecordingSupplierRepository : ISupplierRepository
    {
        public long? SearchTenant { get; private set; }
        public Supplier? Added { get; private set; }
        public Supplier? CurrentSupplier { get; set; }
        public Supplier? Updated { get; private set; }

        public Task<List<SupplierSearchResultDTO>> SearchSuppliersAsync(
            string? searchTerm,
            string? productCategory,
            long businessUnitId)
        {
            SearchTenant = businessUnitId;
            return Task.FromResult(new List<SupplierSearchResultDTO>());
        }

        public Task AddAsync(Supplier supplier)
        {
            Added = supplier;
            supplier.Id = 1;
            supplier.DocId = "SU00000001";
            return Task.CompletedTask;
        }

        public Task<(IEnumerable<SupplierResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? name, string? contactEmail,
            long? currencyId, bool? isActive, string? docId, long businessUnitId) =>
            Task.FromResult<(IEnumerable<SupplierResponseDTO>, int)>(([], 0));

        public Task<Supplier> GetByIdAsync(long id, long businessUnitId) =>
            Task.FromResult(CurrentSupplier ?? new Supplier
            {
                Id = id,
                Buid = businessUnitId,
                Name = "Known supplier",
                ImageUrl = string.Empty,
                CreatedBy = "seed",
                CreatedOn = DateTime.UtcNow
            });

        public Task UpdateAsync(Supplier supplier, long businessUnitId)
        {
            Updated = supplier;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(long id, long businessUnitId) => Task.CompletedTask;
    }

    private sealed class RecordingContactRepository : IContactRepository
    {
        public long? SupplierTenant { get; private set; }

        public Task<IEnumerable<SupplierDropDown>> GetSuppliersAsync(long businessUnitId)
        {
            SupplierTenant = businessUnitId;
            return Task.FromResult<IEnumerable<SupplierDropDown>>([]);
        }

        public Task<(IEnumerable<ContactResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? firstName, string? lastName,
            string? email, long? customerId, long? supplierId, bool? isPrimary,
            bool? isActive, long businessUnitId) => throw new NotSupportedException();
        public Task<Contact> GetByIdAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task AddAsync(Contact contact, long businessUnitId, string actor) => throw new NotSupportedException();
        public Task UpdateAsync(Contact contact, long businessUnitId, string actor, Guid expectedConcurrencyToken) => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken) => throw new NotSupportedException();
        public Task<IEnumerable<CustomerDropdown>> GetCustomersAsync(long businessUnitId) => throw new NotSupportedException();
    }

    private sealed class AllowAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

}
