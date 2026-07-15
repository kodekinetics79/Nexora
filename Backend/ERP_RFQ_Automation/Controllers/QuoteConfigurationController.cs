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
        [HttpPost("migrate")]
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Migration failed: {ex.Message}");
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        // POST: api/QuoteConfiguration
        [HttpPost]
        public async Task<ActionResult<QuoteConfigurationResponseDTO>> CreateOrUpdate([FromBody] QuoteConfigurationCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

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
                    existing.ModifiedBy = request.CreatedBy;
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
                    ModifiedBy = request.CreatedBy,
                    ModifiedOn = DateTime.UtcNow
                };

                await _repository.AddAsync(config);
                return CreatedAtAction(nameof(GetByBusinessUnitId), new { businessUnitId = config.BusinessUnitId }, MapToResponse(config));
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error saving data: {ex.Message}");
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
