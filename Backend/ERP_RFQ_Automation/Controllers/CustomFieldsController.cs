using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CustomFields;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/custom-fields")]
public sealed class CustomFieldsController : ControllerBase
{
    private readonly ICustomFieldApplicationService _customFields;
    private readonly IAuthorizationService _authorization;
    private readonly IRoleGate _roleGate;

    public CustomFieldsController(
        ICustomFieldApplicationService customFields,
        IAuthorizationService authorization,
        IRoleGate roleGate)
    {
        _customFields = customFields;
        _authorization = authorization;
        _roleGate = roleGate;
    }

    [HttpGet("definitions")]
    [RequireManagerRole]
    public async Task<ActionResult<IReadOnlyList<CustomFieldDefinitionResponse>>> ListDefinitions(
        [FromQuery] string entityType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityType)) return BadRequest(new { error = "entityType is required." });
        if (!await HasModulePermissionAsync(entityType, PermissionAction.Edit)) return Forbid();
        return Ok(await _customFields.ListDefinitionsAsync(TenantId(), entityType, ct));
    }

    [HttpPost("definitions")]
    [RequireManagerRole]
    public async Task<ActionResult<CustomFieldDefinitionResponse>> CreateDefinition(
        [FromBody] CreateCustomFieldDefinitionCommand command, CancellationToken ct)
    {
        if (!await HasModulePermissionAsync(command.EntityType, PermissionAction.Edit)) return Forbid();
        return await Execute(() => _customFields.CreateDefinitionAsync(TenantId(), command, Actor(), ct));
    }

    [HttpPost("definitions/{definitionId:long}/versions")]
    [RequireManagerRole]
    public async Task<ActionResult<CustomFieldDefinitionResponse>> AddVersion(
        long definitionId, [FromBody] AddCustomFieldVersionCommand command, CancellationToken ct)
    {
        if (!await CanManageDefinitionAsync(definitionId, ct)) return Forbid();
        return await Execute(() => _customFields.AddVersionAsync(TenantId(), definitionId, command, Actor(), ct));
    }

    [HttpPost("definitions/{definitionId:long}/versions/{versionNumber:int}/activate")]
    [RequireManagerRole]
    public async Task<ActionResult<CustomFieldDefinitionResponse>> ActivateVersion(
        long definitionId, int versionNumber, CancellationToken ct)
    {
        if (!await CanManageDefinitionAsync(definitionId, ct)) return Forbid();
        return await Execute(() => _customFields.ActivateVersionAsync(TenantId(), definitionId, versionNumber, ct));
    }

    [HttpPost("definitions/{definitionId:long}/retire")]
    [RequireManagerRole]
    public async Task<ActionResult<CustomFieldDefinitionResponse>> RetireDefinition(
        long definitionId, [FromBody] RetireCustomFieldDefinitionCommand command, CancellationToken ct)
    {
        if (!await CanManageDefinitionAsync(definitionId, ct)) return Forbid();
        return await Execute(() => _customFields.RetireDefinitionAsync(TenantId(), definitionId, command, Actor(), ct));
    }

    [HttpGet("entities/{entityType}/{entityId:long}")]
    public async Task<ActionResult<CustomFieldEntitySchemaResponse>> GetEntitySchema(
        string entityType, long entityId, CancellationToken ct)
    {
        if (!await HasModulePermissionAsync(entityType, PermissionAction.View)) return Forbid();
        var managerOrAdmin = await IsManagerOrAdminAsync();
        return await Execute(() => _customFields.GetEntitySchemaAsync(
            TenantId(), entityType, entityId, managerOrAdmin, ct));
    }

    [HttpPut("entities/{entityType}/{entityId:long}/values/{stableKey}")]
    public async Task<ActionResult<CustomFieldValueResponse>> UpsertValue(
        string entityType, long entityId, string stableKey,
        [FromBody] UpsertCustomFieldValueCommand command, CancellationToken ct)
    {
        if (!await HasModulePermissionAsync(entityType, PermissionAction.Edit)) return Forbid();
        if (command.Value.ReferenceId.HasValue &&
            !await HasModulePermissionAsync(command.Value.ReferenceType ?? string.Empty, PermissionAction.View))
            return Forbid();
        var managerOrAdmin = await IsManagerOrAdminAsync();
        return await Execute(() => _customFields.UpsertValueAsync(
            TenantId(), entityType, entityId, stableKey, command, Actor(), managerOrAdmin, ct));
    }

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (CustomFieldNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (CustomFieldConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (CustomFieldDomainException ex) { return BadRequest(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private async Task<bool> HasModulePermissionAsync(string entityType, PermissionAction action)
    {
        string module;
        try { module = ModuleFor(CustomFieldApplicationService.CanonicalEntityType(entityType)); }
        catch (CustomFieldDomainException) { return false; }
        var result = await _authorization.AuthorizeAsync(
            User, null, $"{RequireModulePermissionAttribute.PolicyPrefix}{module}:{action}");
        return result.Succeeded;
    }

    private async Task<bool> CanManageDefinitionAsync(long definitionId, CancellationToken ct)
    {
        try
        {
            var entityType = await _customFields.GetDefinitionEntityTypeAsync(TenantId(), definitionId, ct);
            return await HasModulePermissionAsync(entityType, PermissionAction.Edit);
        }
        catch (CustomFieldNotFoundException)
        {
            return false;
        }
    }

    private async Task<bool> IsManagerOrAdminAsync()
    {
        if (!long.TryParse(User.FindFirst("roleId")?.Value, out var roleId)) return false;
        return await _roleGate.IsManagerOrAdminAsync(roleId);
    }

    private static string ModuleFor(string entityType) => entityType switch
    {
        "CommercialCase" or "Lead" => "Leads",
        "Rfq" => "RFQ Management",
        "Quote" => "Quotations",
        "Order" => "Orders",
        "Shipment" => "Shipments",
        "Customer" => "Customers",
        "Supplier" => "Suppliers",
        "Product" => "Products",
        _ => throw new CustomFieldDomainException("Unsupported custom-field entity type.")
    };

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0
        ? id
        : throw new CustomFieldConflictException("Business Unit ID is required.");

    private string Actor() =>
        User.FindFirst(ClaimTypes.Email)?.Value ??
        User.FindFirst(ClaimTypes.Name)?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        User.FindFirst("sub")?.Value ??
        throw new CustomFieldConflictException("Authenticated actor is required.");
}
