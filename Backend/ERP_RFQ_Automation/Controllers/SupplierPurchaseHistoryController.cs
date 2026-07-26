using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.SupplierPurchaseHistory;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SupplierPurchaseHistoryController(
    ISupplierPurchaseHistoryRepository repository,
    ILogger<SupplierPurchaseHistoryController> logger) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> GetAll([FromQuery] long? businessUnitId = null)
        => ExecuteReadAsync(async () => Ok(await repository.GetAllAsync(TenantId())));

    [HttpGet("product/{productId:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> GetByProductId(long productId, [FromQuery] long? businessUnitId = null)
        => ExecuteReadAsync(async () => Ok(await repository.GetByProductIdAsync(productId, TenantId())));

    [HttpGet("{id:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> GetById(long id, [FromQuery] long? businessUnitId = null)
        => ExecuteReadAsync(async () =>
        {
            var result = await repository.GetByIdAsync(id, TenantId());
            return result is null
                ? NotFound(Problem(StatusCodes.Status404NotFound, "Purchase history record not found",
                    "The requested purchase history record was not found."))
                : Ok(result);
        });

    [HttpPost]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public IActionResult Create([FromBody] SupplierPurchaseHistoryCreateDTO request) => MutationRetired();

    [HttpPost("batch")]
    [RequireModulePermission("Supplier History", PermissionAction.Create)]
    public IActionResult CreateBatch([FromBody] SupplierPurchaseHistoryBatchCreateDTO request) => MutationRetired();

    [HttpPut("{id:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.Edit)]
    public IActionResult Update(long id, [FromBody] SupplierPurchaseHistoryUpdateDTO request) => MutationRetired();

    [HttpDelete("{id:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.Delete)]
    public IActionResult Delete(long id, [FromQuery] long? businessUnitId = null) => MutationRetired();

    [HttpDelete("po/{poDocId}")]
    [RequireModulePermission("Supplier History", PermissionAction.Delete)]
    public IActionResult DeleteByPoNumber(string poDocId, [FromQuery] long? businessUnitId = null) => MutationRetired();

    private IActionResult MutationRetired()
        => StatusCode(StatusCodes.Status410Gone, Problem(
            StatusCodes.Status410Gone,
            "Purchase history mutation retired",
            "Purchase history is read-only. Use the governed procurement purchase-order and goods-receipt endpoints."));

    private async Task<IActionResult> ExecuteReadAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid purchase history request", exception.Message));
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Supplier purchase history read failed. CorrelationId={CorrelationId}", CorrelationId());
            return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                StatusCodes.Status500InternalServerError,
                "Purchase history unavailable",
                "The purchase history request could not be completed."));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };

    private long TenantId()
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
            ? value
            : throw new ArgumentException("A valid tenant claim is required.");

    private string CorrelationId()
        => Request.Headers.TryGetValue("X-Correlation-ID", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString().Trim()
            : HttpContext.TraceIdentifier;
}
