using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-learning")]
public sealed class CommercialLearningController(CommercialLearningService service,
    LearningGovernanceService governance,
    IRoleGate roleGate) : ControllerBase
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
    public async Task<ActionResult<SalesRepCommercialMemory>> SalesRep(long userId,
        CancellationToken cancellationToken)
    {
        var tenantId = TenantId();
        if (!await CanReadRepAsync(tenantId, userId)) return Forbid();
        return Ok(await service.GetSalesRepAsync(tenantId, userId, cancellationToken));
    }

    [HttpGet("sales-reps")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public async Task<ActionResult<IReadOnlyCollection<SalesRepCommercialMemory>>> SalesReps(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId();
        var actorUserId = ActorUserId();
        if (await IsManagerAsync(tenantId))
            return Ok(await service.GetSalesRepsAsync(tenantId, limit, cancellationToken));

        IReadOnlyCollection<SalesRepCommercialMemory> ownMemory =
            [await service.GetSalesRepAsync(tenantId, actorUserId, cancellationToken)];
        return Ok(ownMemory);
    }

    [HttpGet("rfq-items/{rfqItemId:long}/memory-card")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Products", PermissionAction.View)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<CommercialMemoryCard> MemoryCard(long rfqItemId, CancellationToken cancellationToken) =>
        service.GetLineCardAsync(TenantId(), rfqItemId, cancellationToken);

    [HttpGet("rfqs/{rfqId:long}/intelligence")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Products", PermissionAction.View)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    public Task<RfqCommercialIntelligence> RfqIntelligence(long rfqId, CancellationToken cancellationToken) =>
        service.GetRfqIntelligenceAsync(TenantId(), rfqId, cancellationToken);

    [HttpGet("learning-studio")]
    [RequireModulePermission("Dashboard", PermissionAction.View)]
    public Task<LearningStudioSummary> LearningStudio(CancellationToken cancellationToken) =>
        service.GetStudioAsync(TenantId(), cancellationToken);

    [HttpPost("learning-studio/{signalId}/approve")]
    [RequireModulePermission("Dashboard", PermissionAction.Edit)]
    public Task<ActionResult<LearningGovernanceResult>> ApproveLearningSignal(string signalId,
        [FromBody] LearningGovernanceCommand command, CancellationToken cancellationToken) =>
        Govern(signalId, LearningGovernanceActions.Approved, command, cancellationToken);

    [HttpPost("learning-studio/{signalId}/disable")]
    [RequireModulePermission("Dashboard", PermissionAction.Edit)]
    public Task<ActionResult<LearningGovernanceResult>> DisableLearningSignal(string signalId,
        [FromBody] LearningGovernanceCommand command, CancellationToken cancellationToken) =>
        Govern(signalId, LearningGovernanceActions.Disabled, command, cancellationToken);

    [HttpPost("learning-studio/{signalId}/rollback")]
    [RequireModulePermission("Dashboard", PermissionAction.Edit)]
    public Task<ActionResult<LearningGovernanceResult>> RollbackLearningSignal(string signalId,
        [FromBody] LearningGovernanceCommand command, CancellationToken cancellationToken) =>
        Govern(signalId, LearningGovernanceActions.RolledBack, command, cancellationToken);

    private async Task<ActionResult<LearningGovernanceResult>> Govern(string signalId, string action,
        LearningGovernanceCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await governance.GovernAsync(TenantId(), signalId, action, command,
                ActorUserId(), IdempotencyKey(), cancellationToken));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid authentication context",
                Detail = "A valid authenticated tenant and actor are required." });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new ProblemDetails { Status = StatusCodes.Status404NotFound,
                Title = "Learning signal not found", Detail = exception.Message });
        }
        catch (LearningGovernanceConflictException exception)
        {
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict,
                Title = "Learning governance conflict", Detail = exception.Message });
        }
        catch (LearningGovernanceValidationException exception)
        {
            return BadRequest(new ProblemDetails { Status = StatusCodes.Status400BadRequest,
                Title = "Invalid learning governance request", Detail = exception.Message });
        }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenant) && tenant > 0
        ? tenant : throw new UnauthorizedAccessException("A valid authenticated tenant claim is required.");
    private long ActorUserId() => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            User.FindFirst("sub")?.Value, out var actor) && actor > 0
        ? actor : throw new UnauthorizedAccessException("A valid authenticated actor claim is required.");
    private long RoleId() => long.TryParse(User.FindFirst("roleId")?.Value, out var role) && role > 0
        ? role : throw new UnauthorizedAccessException("A valid authenticated role claim is required.");
    private Task<bool> IsManagerAsync(long tenantId) =>
        roleGate.IsManagerOrAdminAsync(RoleId(), tenantId);
    private async Task<bool> CanReadRepAsync(long tenantId, long requestedUserId) =>
        requestedUserId == ActorUserId() || await IsManagerAsync(tenantId);
    private string IdempotencyKey()
    {
        var value = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))
            throw new LearningGovernanceValidationException(
                "Idempotency-Key is required and must not exceed 160 printable characters.");
        return value;
    }
}
