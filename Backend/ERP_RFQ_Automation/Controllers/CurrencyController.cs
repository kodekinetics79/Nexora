using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CurrencyController : ControllerBase
    {
        private readonly ICurrencyRepository _repository;

        public CurrencyController(ICurrencyRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        public async Task<ActionResult<PaginatedCurrencyResponseDTO>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? code = null,
            [FromQuery] string? currencyName = null,
            [FromQuery] decimal? exchangeRate = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var currencies = await _repository.GetAllAsync(targetBUId);

                var filtered = currencies.AsQueryable();

                if (!string.IsNullOrWhiteSpace(code))
                    filtered = filtered.Where(c => c.Code.Contains(code, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(currencyName))
                    filtered = filtered.Where(c => c.CurrencyName.Contains(currencyName, StringComparison.OrdinalIgnoreCase));

                if (exchangeRate.HasValue)
                    filtered = filtered.Where(c => c.ExchangeRate == exchangeRate);

                if (isActive.HasValue)
                    filtered = filtered.Where(c => c.IsActive == isActive);

                var totalItems = filtered.Count();

                var items = filtered
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(MapToResponse)
                    .ToList();

                return Ok(new PaginatedCurrencyResponseDTO
                {
                    Items = items,
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error retrieving data: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<CurrencyResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var currency = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(currency));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpPost]
        public async Task<ActionResult<CurrencyResponseDTO>> Create([FromBody] CurrencyCreateRequestDTO request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId > 0) request.BusinessUnitID = claimBUId;

            if (request.BusinessUnitID <= 0) return BadRequest("Business Unit ID is required.");

            var currency = new Currency
            {
                Code = request.Code,
                CurrencyName = request.CurrencyName,
                Symbol = request.Symbol,
                ExchangeRate = request.ExchangeRate,
                IsBaseCurrency = request.IsBaseCurrency ?? false,
                BusinessUnitId = request.BusinessUnitID,
                IsActive = request.IsActive ?? true,
                CreatedBy = User.Identity?.Name ?? request.CreatedBy ?? "System",
                CreatedOn = DateTime.UtcNow
            };

            await _repository.AddAsync(currency);
            return CreatedAtAction(nameof(GetById), new { id = currency.Id, businessUnitId = currency.BusinessUnitId }, MapToResponse(currency));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(long id, [FromBody] CurrencyUpdateRequestDTO request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitID = claimBUId;

                if (request.BusinessUnitID <= 0) return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitID);

                existing.Code = request.Code;
                existing.CurrencyName = request.CurrencyName;
                existing.Symbol = request.Symbol;
                existing.ExchangeRate = request.ExchangeRate;
                existing.IsBaseCurrency = request.IsBaseCurrency ?? false;
                existing.BusinessUnitId = request.BusinessUnitID;
                existing.IsActive = request.IsActive ?? true;
                existing.ModifiedBy = User.Identity?.Name ?? request.ModifiedBy ?? "System";
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        private static CurrencyResponseDTO MapToResponse(Currency c) => new()
        {
            Id = c.Id,
            Code = c.Code,
            CurrencyName = c.CurrencyName,
            Symbol = c.Symbol,
            ExchangeRate = c.ExchangeRate,
            IsBaseCurrency = c.IsBaseCurrency,
            BusinessUnitID = c.BusinessUnitId,
            IsActive = c.IsActive,
            CreatedBy = c.CreatedBy,
            CreatedOn = c.CreatedOn,
            ModifiedBy = c.ModifiedBy,
            ModifiedOn = c.ModifiedOn
        };
    }
}