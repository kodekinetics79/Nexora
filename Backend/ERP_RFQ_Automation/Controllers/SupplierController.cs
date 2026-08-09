using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SupplierController : ControllerBase
    {
        private readonly ISupplierRepository _repository;

        public SupplierController(ISupplierRepository repository)
        {
            _repository = repository;
        }

        // GET: api/Supplier?pageNumber=1&pageSize=10&id=1&name=abc&contactEmail=abc@example.com&taxId=123&currencyId=1&isActive=true&businessUnitId=1
        [HttpGet]
        [RequireModulePermission("Suppliers", PermissionAction.View)]
        public async Task<ActionResult<DTOs.SupplierDTOs.PaginatedResponseDTO<SupplierResponseDTO>>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? id = null,
            [FromQuery] string? name = null,
            [FromQuery] string? contactEmail = null,
            [FromQuery] long? currencyId = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] string? docId = null)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                if (pageNumber < 1)
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid supplier query",
                        "Page number must be greater than or equal to 1."));

                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid supplier query",
                        "Page size must be between 1 and 1000."));

                var (suppliers, totalCount) = await _repository.GetAllAsync(pageNumber, pageSize, id, name, contactEmail, currencyId, isActive, docId, businessUnitId);

                var response = new DTOs.SupplierDTOs.PaginatedResponseDTO<SupplierResponseDTO>
                {
                    Items = suppliers,
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize
                };

                return Ok(response);
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Suppliers unavailable", "Unable to retrieve suppliers."));
            }
        }

        // GET: api/Supplier/5
        [HttpGet("{id}")]
        [RequireModulePermission("Suppliers", PermissionAction.View)]
        public async Task<ActionResult<SupplierResponseDTO>> GetById(long id)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var supplier = await _repository.GetByIdAsync(id, businessUnitId);
                return Ok(MapToResponse(supplier));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(Problem(StatusCodes.Status404NotFound, "Supplier not found", ex.Message));
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Supplier unavailable", "Unable to retrieve the supplier."));
            }
        }

        // POST: api/Supplier
        [HttpPost]
        [RequireModulePermission("Suppliers", PermissionAction.Create)]
        public async Task<ActionResult<SupplierResponseDTO>> Create([FromForm] SupplierCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var actor = GetAuthenticatedActor();

                var supplier = new Supplier
                {
                    Name = request.Name,
                    ContactEmail = NormalizeEmail(request.ContactEmail),
                    ImageUrl = string.Empty,
                    PaymentTerms = request.PaymentTerms,
                    AddressLine1 = request.AddressLine1,
                    AddressLine2 = request.AddressLine2,
                    CityId = request.CityId,
                    CountryId = request.CountryId,
                    PostalCode = request.PostalCode,
                    Tags = request.Tags,
                    Comments = request.Comments,
                    CurrencyId = request.CurrencyId,
                    Buid = businessUnitId,
                    IsActive = true,
                    CreatedBy = actor,
                    CreatedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(supplier);

                var response = MapToResponse(supplier);
                return CreatedAtAction(nameof(GetById), new { id = supplier.Id }, response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid supplier request", ex.Message));
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Supplier not created", "Unable to create the supplier."));
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("Suppliers", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromForm] SupplierUpdateRequestDTO request)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var existing = await _repository.GetByIdAsync(id, businessUnitId);
                if (existing.ConcurrencyToken.HasValue && request.ConcurrencyToken != existing.ConcurrencyToken)
                    return Conflict(Problem(StatusCodes.Status409Conflict, "Supplier conflict",
                        "The supplier changed since it was loaded. Refresh and retry."));

                var dispatchEmailChanged = !string.Equals(existing.ContactEmail?.Trim(),
                    request.ContactEmail?.Trim(), StringComparison.OrdinalIgnoreCase);
                existing.Name = request.Name;
                existing.ContactEmail = NormalizeEmail(request.ContactEmail);
                existing.PaymentTerms = request.PaymentTerms;
                existing.AddressLine1 = request.AddressLine1;
                existing.AddressLine2 = request.AddressLine2;
                existing.CityId = request.CityId;
                existing.CountryId = request.CountryId;
                existing.PostalCode = request.PostalCode;
                existing.Tags = request.Tags;
                existing.Comments = request.Comments;
                existing.CurrencyId = request.CurrencyId;
                // Activation is exclusively controlled by Supplier governance.
                existing.ModifiedBy = GetAuthenticatedActor();
                existing.ModifiedOn = DateTime.UtcNow;
                if (dispatchEmailChanged)
                {
                    existing.GovernanceStatus = SupplierGovernanceStatuses.ReviewRequired;
                    existing.VerificationStatus = SupplierVerificationStatuses.Pending;
                    existing.ReadinessStatus = SupplierReadinessStatuses.ReviewRequired;
                    existing.GovernanceReviewedBy = null;
                    existing.GovernanceReviewedOn = null;
                    existing.EffectiveFrom = existing.ModifiedOn;
                }

                await _repository.UpdateAsync(existing, businessUnitId);

                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(Problem(StatusCodes.Status404NotFound, "Supplier not found", ex.Message));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid supplier request", ex.Message));
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(Problem(StatusCodes.Status409Conflict, "Supplier conflict",
                        "The supplier changed since it was loaded. Refresh and retry."));
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Supplier not updated", "Unable to update the supplier."));
            }
        }

        // DELETE: api/Supplier/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Suppliers", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                await _repository.DeleteAsync(id, businessUnitId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(Problem(StatusCodes.Status404NotFound, "Supplier not found", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid supplier request", ex.Message));
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Supplier not deleted", "Unable to delete the supplier."));
            }
        }


        private SupplierResponseDTO MapToResponse(Supplier supplier)
        {
            return new SupplierResponseDTO
            {
                Id = supplier.Id,
                DocId = supplier.DocId,
                Name = supplier.Name,
                ContactEmail = supplier.ContactEmail,
                ImageUrl = supplier.ImageUrl,
                PaymentTerms = supplier.PaymentTerms,
                AddressLine1 = supplier.AddressLine1,
                AddressLine2 = supplier.AddressLine2,
                CityId = supplier.CityId,
                CityName = supplier.City != null ? supplier.City.CityName : null,
                CountryId = supplier.CountryId,
                CountryName = supplier.Country != null ? supplier.Country.CountryName : null,
                PostalCode = supplier.PostalCode,
                SuccessRate = supplier.SuccessRate,
                AvgResponseTime = supplier.AvgResponseTime,
                Tags = supplier.Tags,
                Comments = supplier.Comments,
                CurrencyId = supplier.CurrencyId,
                CurrencyName = supplier.Currency != null ? supplier.Currency.CurrencyName : null, 
                Buid = supplier.Buid,
                BusinessUnitName = supplier.Bu != null ? supplier.Bu.BusinessUnitName : null,
                IsActive = supplier.IsActive,
                CustomFields = supplier.CustomFieldsJson,
                GovernanceStatus = supplier.GovernanceStatus,
                VerificationStatus = supplier.VerificationStatus,
                ComplianceStatus = supplier.ComplianceStatus,
                RiskStatus = supplier.RiskStatus,
                ReadinessStatus = supplier.ReadinessStatus,
                EffectiveFrom = supplier.EffectiveFrom,
                GovernanceReviewedBy = supplier.GovernanceReviewedBy,
                GovernanceReviewedOn = supplier.GovernanceReviewedOn,
                ConcurrencyToken = supplier.ConcurrencyToken,
                CreatedBy = supplier.CreatedBy,
                CreatedOn = supplier.CreatedOn,
                ModifiedBy = supplier.ModifiedBy,
                ModifiedOn = supplier.ModifiedOn
            };
        }

        [HttpGet("search")]
        [RequireModulePermission("Suppliers", PermissionAction.View)]
        public async Task<ActionResult<List<SupplierSearchResultDTO>>> Search(
            [FromQuery] string? searchTerm,
            [FromQuery] string? productCategory)
        {
            try
            {
                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var suppliers = await _repository.SearchSuppliersAsync(searchTerm, productCategory, businessUnitId);
                return Ok(suppliers);
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Supplier search unavailable", "Unable to search suppliers."));
            }
        }

        [HttpGet("web-search")]
        [RequireModulePermission("Suppliers", PermissionAction.View)]
        public ActionResult<List<SupplierSearchResultDTO>> WebSearch([FromQuery] string query)
        {
            if (!TryGetAuthenticatedTenant(out _))
                return Forbid();
            if (string.IsNullOrWhiteSpace(query))
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid supplier query",
                    "Search query is required."));

            return StatusCode(StatusCodes.Status503ServiceUnavailable, Problem(
                StatusCodes.Status503ServiceUnavailable, "External supplier discovery disabled",
                "External supplier discovery is disabled until a governed, tenant-authorized provider is configured."));
        }

        // Compose Quote Email
        [HttpPost("compose-quote-email")]
        [RequireModulePermission("Suppliers", PermissionAction.Create)]
        public async Task<ActionResult<QuoteEmailTemplateDTO>> ComposeQuoteEmail([FromBody] BatchQuoteRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                if (!TryGetAuthenticatedTenant(out var businessUnitId))
                    return Forbid();

                var supplier = await _repository.GetByIdAsync(request.SupplierId, businessUnitId);
                var blockers = SupplierRfqBlockingReasons(supplier);
                if (blockers.Count > 0)
                    return Conflict(Problem(StatusCodes.Status409Conflict, "Supplier RFQ outreach blocked",
                        $"Supplier RFQ outreach is blocked: {string.Join("; ", blockers)}"));
                request.SupplierName = supplier.Name;
                request.SupplierEmail = supplier.ContactEmail ?? string.Empty;

                var template = GenerateQuoteEmailTemplate(request);
                return Ok(template);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(Problem(StatusCodes.Status404NotFound, "Supplier not found", "Supplier not found."));
            }
            catch (Exception)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, Problem(
                    StatusCodes.Status500InternalServerError, "Supplier RFQ email unavailable",
                    "Unable to compose the Supplier RFQ email."));
            }
        }

        private QuoteEmailTemplateDTO GenerateQuoteEmailTemplate(BatchQuoteRequestDTO request)
        {
            var subject = string.IsNullOrWhiteSpace(request.RfqNumber)
                ? $"Quote Request for {request.Items.Count} Item(s)"
                : $"Quote Request - RFQ #{request.RfqNumber}";

            var body = new System.Text.StringBuilder();
            body.AppendLine($"Dear {request.SupplierName},");
            body.AppendLine();
            body.AppendLine("We would like to request a quotation for the following items:");
            body.AppendLine();
            body.AppendLine("| # | Part Number | Manufacturer | Description | Quantity | UOM |");
            body.AppendLine("|---|-------------|--------------|-------------|----------|-----|");

            int index = 1;
            foreach (var item in request.Items)
            {
                body.AppendLine($"| {index++} | {item.PartNumber ?? "N/A"} | {item.Manufacturer ?? "N/A"} | {item.Description ?? "N/A"} | {item.Quantity} | {item.UnitOfMeasure ?? "EA"} |");
            }

            body.AppendLine();
            if (request.RequiredDate.HasValue)
            {
                body.AppendLine($"Required Date: {request.RequiredDate.Value:yyyy-MM-dd}");
                body.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(request.AdditionalNotes))
            {
                body.AppendLine("Additional Notes:");
                body.AppendLine(request.AdditionalNotes);
                body.AppendLine();
            }

            body.AppendLine("Please provide your best pricing and lead time for the items listed above.");
            body.AppendLine();    
            body.AppendLine("Thank you for your assistance.");
            body.AppendLine();
            body.AppendLine("Best regards");

            return new QuoteEmailTemplateDTO
            {
                Subject = subject,
                Body = body.ToString(),
                ToEmail = request.SupplierEmail,
                Items = request.Items
            };
        }

        private bool TryGetAuthenticatedTenant(out long businessUnitId)
        {
            return long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId)
                && businessUnitId > 0;
        }

        /// <summary>
        /// RFC 7807 body carrying the request's trace identifier, so a caller reporting a
        /// failure gives support an id that ties straight back to the server log entry.
        /// </summary>
        private ProblemDetails Problem(int status, string title, string detail)
        {
            var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            return problem;
        }

        private static string? NormalizeEmail(string? email)
            => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

        private static IReadOnlyCollection<string> SupplierRfqBlockingReasons(Supplier supplier)
        {
            var reasons = new List<string>();
            if (supplier.IsActive != true) reasons.Add("Supplier is inactive");
            if (string.IsNullOrWhiteSpace(supplier.ContactEmail)) reasons.Add("Dispatch email is missing");
            if (supplier.GovernanceStatus is not (SupplierGovernanceStatuses.Approved
                    or SupplierGovernanceStatuses.Preferred or SupplierGovernanceStatuses.Provisional))
                reasons.Add("Governance approval is required");
            if (supplier.VerificationStatus != SupplierVerificationStatuses.Verified)
                reasons.Add("Supplier identity is not verified");
            if (supplier.ComplianceStatus != SupplierComplianceStatuses.Cleared)
                reasons.Add("Compliance is not cleared");
            if (supplier.RiskStatus is SupplierRiskStatuses.High or SupplierRiskStatuses.Blocked)
                reasons.Add("Supplier risk blocks outreach");
            if (supplier.ReadinessStatus != SupplierReadinessStatuses.Ready)
                reasons.Add("Supplier is not READY for outreach");
            return reasons;
        }

        private string GetAuthenticatedActor()
        {
            return User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue(ClaimTypes.Email)
                ?? User.Identity?.Name
                ?? "authenticated-user";
        }
    }
}
