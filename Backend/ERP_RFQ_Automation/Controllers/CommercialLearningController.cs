using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-learning")]
public sealed class CommercialLearningController(CommercialLearningService service) : ControllerBase
{
    [HttpGet("products")]
    [RequireModulePermission("Products", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<IReadOnlyCollection<ProductCommercialMemory>> Products([FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) => service.GetProductsAsync(TenantId(), limit, cancellationToken);

    [HttpGet("products/{productId:long}")]
    [RequireModulePermission("Products", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<ProductCommercialMemory> Product(long productId, CancellationToken cancellationToken) =>
        service.GetProductAsync(TenantId(), productId, cancellationToken);

    [HttpGet("products/{productId:long}/inventory-demand")]
    [RequireModulePermission("Products", PermissionAction.View)]
    public Task<InventoryDemandMemory> InventoryDemand(long productId, CancellationToken cancellationToken) =>
        service.GetInventoryDemandAsync(TenantId(), productId, cancellationToken);

    [HttpGet("suppliers/{supplierId:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<SupplierCommercialEvaluation> Supplier(long supplierId, CancellationToken cancellationToken) =>
        service.GetSupplierAsync(TenantId(), supplierId, cancellationToken);

    [HttpGet("suppliers")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<IReadOnlyCollection<SupplierCommercialEvaluation>> Suppliers([FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) => service.GetSuppliersAsync(TenantId(), limit, cancellationToken);

    [HttpGet("customers/{customerId:long}")]
    [RequireModulePermission("Customers", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<CustomerCommercialMemory> Customer(long customerId, CancellationToken cancellationToken) =>
        service.GetCustomerAsync(TenantId(), customerId, cancellationToken);

    [HttpGet("customers")]
    [RequireModulePermission("Customers", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<IReadOnlyCollection<CustomerCommercialMemory>> Customers([FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) => service.GetCustomersAsync(TenantId(), limit, cancellationToken);

    [HttpGet("sales-reps/{userId:long}")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public Task<SalesRepCommercialMemory> SalesRep(long userId, CancellationToken cancellationToken) =>
        service.GetSalesRepAsync(TenantId(), userId, cancellationToken);

    [HttpGet("sales-reps")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public Task<IReadOnlyCollection<SalesRepCommercialMemory>> SalesReps([FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) => service.GetSalesRepsAsync(TenantId(), limit, cancellationToken);

    [HttpGet("rfq-items/{rfqItemId:long}/memory-card")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Products", PermissionAction.View)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<CommercialMemoryCard> MemoryCard(long rfqItemId, CancellationToken cancellationToken) =>
        service.GetLineCardAsync(TenantId(), rfqItemId, cancellationToken);

    [HttpGet("learning-studio")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public Task<LearningStudioSummary> LearningStudio(CancellationToken cancellationToken) =>
        service.GetStudioAsync(TenantId(), cancellationToken);

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenant) && tenant > 0
        ? tenant : throw new UnauthorizedAccessException("A valid authenticated tenant claim is required.");
}
