using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.SupplierQuotedItem;
using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SupplierQuotedItemController : ControllerBase
    {
        private readonly ISupplierQuotedItemRepository _repository;

        public SupplierQuotedItemController(ISupplierQuotedItemRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<SupplierQuotedItemResponseDTO>>> GetAll([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

                var items = await _repository.GetAllAsync(targetBUId);
                return Ok(items);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<SupplierQuotedItemResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

                var item = await _repository.GetByIdAsync(id, targetBUId);
                if (item == null)
                {
                    return NotFound();
                }
                return Ok(item);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("GetBySupplier/{supplierId}")]
        public async Task<ActionResult<IEnumerable<SupplierQuotedItemResponseDTO>>> GetBySupplier(long supplierId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

                var items = await _repository.GetBySupplierIdAsync(supplierId, targetBUId);
                return Ok(items);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<ActionResult<SupplierQuotedItemResponseDTO>> Create(SupplierQuotedItemCreateDTO dto, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (dto == null)
                {
                    return BadRequest("Item is null");
                }

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? dto.BusinessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");
                
                if (string.IsNullOrEmpty(dto.ItemName))
                {
                     return BadRequest("Item Name is required");
                }
                 if (dto.Quantity <= 0)
                {
                     return BadRequest("Quantity must be greater than 0");
                }

                var item = new SupplierQuotedItem
                {
                    SupplierId = dto.SupplierId,
                    ItemName = dto.ItemName,
                    Description = dto.Description,
                    UomId = dto.UomId,
                    Quantity = dto.Quantity,
                    UnitPrice = dto.UnitPrice,
                    CurrencyId = dto.CurrencyId,
                    QuoteReference = dto.QuoteReference,
                    QuoteDate = dto.QuoteDate,
                    ValidUntil = dto.ValidUntil,
                    TaxAmount = dto.TaxAmount,
                    DiscountAmount = dto.DiscountAmount,
                    CreatedBy = dto.CreatedBy,
                    CreatedDate = DateTime.UtcNow,
                    IsActive = dto.IsActive,
                    BusinessUnitId = targetBUId
                };

                var createdItem = await _repository.AddAsync(item);
                var response = await _repository.GetByIdAsync(createdItem.Id, targetBUId);
                return CreatedAtAction(nameof(GetById), new { id = createdItem.Id, businessUnitId = targetBUId }, response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(long id, SupplierQuotedItemUpdateDTO dto, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                if (id != dto.Id)
                {
                    return BadRequest("ID mismatch");
                }

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? dto.BusinessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

                var item = new SupplierQuotedItem
                {
                    Id = dto.Id,
                    SupplierId = dto.SupplierId,
                    ItemName = dto.ItemName,
                    Description = dto.Description,
                    UomId = dto.UomId,
                    Quantity = dto.Quantity,
                    UnitPrice = dto.UnitPrice,
                    CurrencyId = dto.CurrencyId,
                    QuoteReference = dto.QuoteReference,
                    QuoteDate = dto.QuoteDate,
                    ValidUntil = dto.ValidUntil,
                    TaxAmount = dto.TaxAmount,
                    DiscountAmount = dto.DiscountAmount,
                    ModifiedBy = dto.CreatedBy, // Using CreatedBy for ModifiedBy in update for simplicity here
                    ModifiedDate = DateTime.UtcNow,
                    IsActive = dto.IsActive,
                    BusinessUnitId = targetBUId
                };

                await _repository.UpdateAsync(item, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0) return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
}
