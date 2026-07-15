using ERP_RFQ_Automation.DTOs.SupplierPurchaseHistory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SupplierPurchaseHistoryController : ControllerBase
    {
        private readonly ISupplierPurchaseHistoryRepository _repository;
        private readonly ILogger<SupplierPurchaseHistoryController> _logger;

        public SupplierPurchaseHistoryController(ISupplierPurchaseHistoryRepository repository, ILogger<SupplierPurchaseHistoryController> logger)
        {
            _repository = repository;
            _logger = logger;
        }


        [HttpGet]
        public async Task<ActionResult<IEnumerable<SupplierPurchaseHistoryResponseDTO>>> GetAll([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var result = await _repository.GetAllAsync(targetBUId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpGet("product/{productId}")]
        public async Task<ActionResult<IEnumerable<SupplierPurchaseHistoryResponseDTO>>> GetByProductId(long productId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var result = await _repository.GetByProductIdAsync(productId, targetBUId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<SupplierPurchaseHistoryResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var result = await _repository.GetByIdAsync(id, targetBUId);
                if (result == null) return NotFound();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<ActionResult<SupplierPurchaseHistoryResponseDTO>> Create([FromBody] SupplierPurchaseHistoryCreateDTO request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var history = new SupplierPurchaseHistory
            {
                ProductId = request.ProductId,
                SupplierId = request.SupplierId,
                PurchaseDate = request.PurchaseDate,
                Quantity = request.Quantity,
                UnitPrice = request.UnitPrice,
                Currency = request.Currency,
                BatchNo = request.BatchNo,
                ExpiryDate = string.IsNullOrEmpty(request.ExpiryDate) ? null : DateOnly.Parse(request.ExpiryDate),
                CreatedBy = request.CreatedBy,
                CreatedOn = DateTime.UtcNow
            };

            await _repository.AddAsync(history);
            return Ok(history);
        }

        [HttpPost("batch")]
        public async Task<ActionResult<string>> CreateBatch([FromBody] SupplierPurchaseHistoryBatchCreateDTO request)
        {
            _logger.LogInformation("Creating batch purchase history. Item count: {Count}", request.Items?.Count ?? 0);
            if (!ModelState.IsValid) return BadRequest(ModelState);

            if (request.Items == null || !request.Items.Any()) return BadRequest("No items provided.");

            var histories = request.Items.Select(item => new SupplierPurchaseHistory
            {
                ProductId = item.ProductId,
                SupplierId = item.SupplierId,
                PurchaseDate = item.PurchaseDate,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Currency = item.Currency,
                BatchNo = item.BatchNo,
                ExpiryDate = string.IsNullOrEmpty(item.ExpiryDate) ? null : DateOnly.Parse(item.ExpiryDate),
                CreatedBy = item.CreatedBy,
                CreatedOn = DateTime.UtcNow
            }).ToList();

            string poId = await _repository.AddBatchAsync(histories);
            return Ok(new { PoDocId = poId });
        }


        [HttpPut("{id}")]
        public async Task<IActionResult> Update(long id, [FromBody] SupplierPurchaseHistoryUpdateDTO request)
        {
            if (id != request.Id) return BadRequest("ID mismatch");
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var existing = await _repository.GetByIdAsync(id, 0);
            if (existing == null) return NotFound();

            var history = new SupplierPurchaseHistory
            {
                Id = request.Id,
                ProductId = request.ProductId,
                SupplierId = request.SupplierId,
                PurchaseDate = request.PurchaseDate,
                Quantity = request.Quantity,
                UnitPrice = request.UnitPrice,
                Currency = request.Currency,
                BatchNo = request.BatchNo,
                ExpiryDate = request.ExpiryDate,
                // Preserve CreatedBy/CreatedOn if repo/context allows
            };

            await _repository.UpdateAsync(history);
            return NoContent();
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
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpDelete("po/{poDocId}")]
        public async Task<IActionResult> DeleteByPoNumber(string poDocId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteByPoDocIdAsync(poDocId, targetBUId);
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }
    }
}

