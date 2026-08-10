using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.QuoteConfigurationDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QuoteConfigurationController : ControllerBase
    {
        private readonly IQuoteConfigurationRepository _repository;
        private readonly ISetupMasterRepository _setupRepository;
        private readonly ErpRfqAutomationContext _context;

        public QuoteConfigurationController(IQuoteConfigurationRepository repository, ISetupMasterRepository setupRepository, ErpRfqAutomationContext context)
        {
            _repository = repository;
            _setupRepository = setupRepository;
            _context = context;
        }

        // POST: api/QuoteConfiguration/migrate
        //
        // SECURITY REPAIR. This action iterates EVERY BusinessUnitId present in SetupMaster and
        // writes a configuration row for each, and it carried only [Authorize] — so any
        // authenticated user of any tenant could create configuration for every other tenant.
        // It is a one-off platform data migration, so it is restricted to the platform-admin
        // permission the other cross-tenant maintenance routes already use, rather than to the
        // per-tenant "Quote Configuration" module permission, which would still be the wrong
        // scope for an all-tenant write.
        [HttpPost("migrate")]
        [RequireModulePermission("Business Units", PermissionAction.Edit)]
        public async Task<ActionResult> MigrateFromSetupMaster()
        {
            try
            {
                var setupConfigs = await _context.SetupMasters
                    .Where(s => s.SetupType == "QuoteConfig")
                    .ToListAsync();

                if (!setupConfigs.Any())
                    return Ok("No QuoteConfig data found in SetupMaster to migrate.");

                var buIds = setupConfigs.Select(s => s.BusinessUnitId).Distinct().ToList();
                int migratedCount = 0;

                foreach (var buId in buIds)
                {
                    var existing = await _repository.GetByBusinessUnitIdAsync(buId);
                    if (existing != null) continue; // Already migrated or exists

                    var buConfigs = setupConfigs.Where(s => s.BusinessUnitId == buId).ToList();
                    
                    var newConfig = new QuoteConfiguration
                    {
                        BusinessUnitId = buId,
                        Logo = buConfigs.FirstOrDefault(s => s.SetupCode == "Logo")?.SetupValue,
                        PrimaryColor = buConfigs.FirstOrDefault(s => s.SetupCode == "PrimaryColor")?.SetupValue,
                        TermsAndConditions = buConfigs.FirstOrDefault(s => s.SetupCode == "Terms")?.Description,
                        CompanyAddress = buConfigs.FirstOrDefault(s => s.SetupCode == "CompanyAddress")?.SetupValue,
                        CompanyPhone = buConfigs.FirstOrDefault(s => s.SetupCode == "CompanyPhone")?.SetupValue,
                        CompanyEmail = buConfigs.FirstOrDefault(s => s.SetupCode == "CompanyEmail")?.SetupValue,
                        FooterText = buConfigs.FirstOrDefault(s => s.SetupCode == "FooterText")?.SetupValue,
                        ModifiedBy = "MigrationScript",
                        ModifiedOn = DateTime.UtcNow
                    };

                    await _repository.AddAsync(newConfig);
                    migratedCount++;
                }

                return Ok($"Successfully migrated configuration for {migratedCount} business units.");
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Migration failed.");
            }
        }

        // GET: api/QuoteConfiguration/5
        [HttpGet("{businessUnitId:long}")]
        public async Task<ActionResult<QuoteConfigurationResponseDTO>> GetByBusinessUnitId(long businessUnitId)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : businessUnitId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var config = await _repository.GetByBusinessUnitIdAsync(targetBUId);
                if (config == null)
                    return NotFound($"Configuration for Business Unit {targetBUId} not found.");

                return Ok(MapToResponse(config));
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error retrieving data.");
            }
        }

        // POST: api/QuoteConfiguration
        [HttpPost]
        [RequireModulePermission("Quote Configuration", PermissionAction.Edit)]
        public async Task<ActionResult<QuoteConfigurationResponseDTO>> CreateOrUpdate([FromBody] QuoteConfigurationCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // SECURITY REPAIR. This previously read the tenant claim and, when the claim was
                // absent or 0, silently FELL BACK to the BusinessUnitId supplied in the request
                // body — letting a caller without a tenant claim write another tenant's quote
                // branding. The claim is now the only accepted source; a request that carries no
                // usable claim is rejected rather than trusted.
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0)
                    return Forbid();
                request.BusinessUnitId = claimBUId;

                var existing = await _repository.GetByBusinessUnitIdAsync(request.BusinessUnitId);
                if (existing != null)
                {
                    // Update instead of create
                    existing.Logo = request.Logo;
                    existing.PrimaryColor = request.PrimaryColor;
                    existing.TermsAndConditions = request.TermsAndConditions;
                    existing.CompanyAddress = request.CompanyAddress;
                    existing.CompanyPhone = request.CompanyPhone;
                    existing.CompanyEmail = request.CompanyEmail;
                    existing.FooterText = request.FooterText;
                    // RC-7 / Sec-A1: server-derived. ModifiedBy is the ONLY record of who changed
                    // this tenant's quote branding, terms and footer — there is no IAM audit event
                    // behind it — so a client-supplied string was the whole audit trail.
                    existing.ModifiedBy = ActorContext.From(User).Stamp;
                    existing.ModifiedOn = DateTime.UtcNow;

                    await _repository.UpdateAsync(existing);
                    return Ok(MapToResponse(existing));
                }

                var config = new QuoteConfiguration
                {
                    BusinessUnitId = request.BusinessUnitId,
                    Logo = request.Logo,
                    PrimaryColor = request.PrimaryColor,
                    TermsAndConditions = request.TermsAndConditions,
                    CompanyAddress = request.CompanyAddress,
                    CompanyPhone = request.CompanyPhone,
                    CompanyEmail = request.CompanyEmail,
                    FooterText = request.FooterText,
                    ModifiedBy = ActorContext.From(User).Stamp,
                    ModifiedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(config);
                return CreatedAtAction(nameof(GetByBusinessUnitId), new { businessUnitId = config.BusinessUnitId }, MapToResponse(config));
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error saving data.");
            }
        }

        private QuoteConfigurationResponseDTO MapToResponse(QuoteConfiguration config)
        {
            return new QuoteConfigurationResponseDTO
            {
                Id = config.Id,
                BusinessUnitId = config.BusinessUnitId,
                Logo = config.Logo,
                PrimaryColor = config.PrimaryColor,
                TermsAndConditions = config.TermsAndConditions,
                CompanyAddress = config.CompanyAddress,
                CompanyPhone = config.CompanyPhone,
                CompanyEmail = config.CompanyEmail,
                FooterText = config.FooterText,
                ModifiedBy = config.ModifiedBy,
                ModifiedOn = config.ModifiedOn
            };
        }
    }
}
