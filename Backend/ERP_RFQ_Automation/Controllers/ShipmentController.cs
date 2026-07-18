using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
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
    [Authorize] // SEC-04: was anonymous; BU resolved from the JWT claim (claim wins over dto.BusinessUnitId)
    public class ShipmentController : ControllerBase
    {
        private readonly IShipmentRepository _repository;
        private readonly ErpRfqAutomationContext _context;

        public ShipmentController(IShipmentRepository repository, ErpRfqAutomationContext context)
        {
            _repository = repository;
            _context = context;
        }

        [HttpGet]
        [RequireModulePermission("Shipments", PermissionAction.View)]
        public async Task<IActionResult> GetShipments([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var shipments = await _repository.GetAllShipmentsAsync(targetBUId);
                var dtos = shipments.Select(MapToDto).ToList();
                return Ok(dtos);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission("Shipments", PermissionAction.View)]
        public async Task<IActionResult> GetShipment(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                var shipment = await _repository.GetShipmentByIdAsync(id, targetBUId);
                if (shipment == null) return NotFound();

                return Ok(MapToDto(shipment));
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpPost]
        [RequireModulePermission("Shipments", PermissionAction.Create)]
        public async Task<IActionResult> CreateShipment([FromBody] CreateShipmentDto dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : dto.BusinessUnitId;

                var shipment = new Shipment
                {
                    ShipmentNo = await _repository.GetNextShipmentNumberAsync(targetBUId),
                    OrderId = dto.OrderId,
                    BusinessUnitId = targetBUId,
                    StatusId = dto.StatusId,
                    ShipmentDate = dto.ShipmentDate,
                    EstimatedDeliveryDate = dto.EstimatedDeliveryDate,
                    Carrier = dto.Carrier,
                    ServiceLevel = dto.ServiceLevel,
                    TrackingNumber = dto.TrackingNumber,
                    ExternalId = dto.ExternalId,
                    ShippingCost = dto.ShippingCost,
                    LabelUrl = dto.LabelUrl,
                    ShippingAddress = dto.ShippingAddress,
                    Notes = dto.Notes,
                    CreatedBy = User.Identity?.Name ?? "system",
                    CreatedOn = DateTime.Now,
                    IsActive = true
                };

                foreach (var itemDto in dto.Items)
                {
                    shipment.ShipmentItems.Add(new ShipmentItem
                    {
                        OrderItemId = itemDto.OrderItemId,
                        Quantity = itemDto.Quantity,
                        Notes = itemDto.Notes,
                        CreatedBy = User.Identity?.Name ?? "system",
                        CreatedOn = DateTime.Now,
                        IsActive = true
                    });
                }

                // Create initial Status History record
                shipment.ShipmentStatusHistories.Add(new ShipmentStatusHistory
                {
                    NewStatusId = dto.StatusId,
                    ChangedBy = User.Identity?.Name ?? "system",
                    ChangedOn = DateTime.Now,
                    Notes = "Initial shipment creation"
                });

                var created = await _repository.CreateShipmentAsync(shipment);

                // Automatically update Order Status to "SHIPPED"
                var order = await _context.Orders.FindAsync(dto.OrderId);
                if (order != null)
                {
                    var shippedStatus = await _context.SetupMasters.FirstOrDefaultAsync(s => s.SetupType == "OrderStatus" && (s.SetupCode == "SHIPPED" || s.SetupValue.ToUpper() == "SHIPPED"));
                    if (shippedStatus != null)
                    {
                        order.StatusId = shippedStatus.SetupId;
                        _context.Orders.Update(order);
                        await _context.SaveChangesAsync();
                    }
                }

                return CreatedAtAction(nameof(GetShipment), new { id = created.Id }, MapToDto(created));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("Shipments", PermissionAction.Edit)]
        public async Task<IActionResult> UpdateShipment(long id, [FromBody] UpdateShipmentDto dto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var shipment = await _repository.GetShipmentByIdAsync(id, claimBUId);
                if (shipment == null) return NotFound();

                if (shipment.StatusId != dto.StatusId)
                {
                    _context.ShipmentStatusHistories.Add(new ShipmentStatusHistory
                    {
                        ShipmentId = shipment.Id,
                        PreviousStatusId = shipment.StatusId,
                        NewStatusId = dto.StatusId,
                        ChangedBy = User.Identity?.Name ?? "system",
                        ChangedOn = DateTime.Now,
                        Notes = dto.Notes
                    });
                }

                shipment.StatusId = dto.StatusId;
                shipment.ActualDeliveryDate = dto.ActualDeliveryDate;
                shipment.TrackingNumber = dto.TrackingNumber ?? shipment.TrackingNumber;
                shipment.Notes = dto.Notes ?? shipment.Notes;
                shipment.ModifiedBy = User.Identity?.Name ?? "system";
                shipment.ModifiedOn = DateTime.Now;

                await _repository.UpdateShipmentAsync(shipment, claimBUId);
                return Ok(MapToDto(shipment));
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        [RequireModulePermission("Shipments", PermissionAction.Delete)]
        public async Task<IActionResult> DeleteShipment(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                await _repository.DeleteShipmentAsync(id, targetBUId);
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        [HttpGet("order/{orderId}")]
        [RequireModulePermission("Shipments", PermissionAction.View)]
        public async Task<IActionResult> GetShipmentsByOrder(long orderId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                var shipments = await _repository.GetShipmentsByOrderIdAsync(orderId, targetBUId);
                return Ok(shipments.Select(MapToDto).ToList());
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

        private ShipmentDto MapToDto(Shipment shipment)
        {
            return new ShipmentDto
            {
                Id = shipment.Id,
                ShipmentNo = shipment.ShipmentNo,
                OrderId = shipment.OrderId,
                OrderNo = shipment.Order?.OrderNo ?? "Unknown",
                StatusId = shipment.StatusId,
                Status = shipment.Status?.SetupValue ?? "Unknown",
                ShipmentDate = shipment.ShipmentDate,
                EstimatedDeliveryDate = shipment.EstimatedDeliveryDate,
                ActualDeliveryDate = shipment.ActualDeliveryDate,
                Carrier = shipment.Carrier,
                ServiceLevel = shipment.ServiceLevel,
                TrackingNumber = shipment.TrackingNumber,
                ExternalId = shipment.ExternalId,
                ShippingCost = shipment.ShippingCost,
                LabelUrl = shipment.LabelUrl,
                ShippingAddress = shipment.ShippingAddress,
                Notes = shipment.Notes,
                Items = shipment.ShipmentItems.Select(si => new ShipmentItemDto
                {
                    Id = si.Id,
                    OrderItemId = si.OrderItemId,
                    ProductName = si.OrderItem?.Product?.ProductName ?? "Unknown Product",
                    Quantity = si.Quantity,
                    Notes = si.Notes
                }).ToList(),
                StatusHistory = shipment.ShipmentStatusHistories?.OrderByDescending(h => h.ChangedOn).Select(h => new ShipmentStatusHistoryDto
                {
                    Id = h.Id,
                    PreviousStatusId = h.PreviousStatusId,
                    PreviousStatus = h.PreviousStatus?.SetupValue,
                    NewStatusId = h.NewStatusId,
                    NewStatus = h.NewStatus?.SetupValue ?? "Unknown",
                    ChangedBy = h.ChangedBy,
                    ChangedOn = h.ChangedOn,
                    Notes = h.Notes
                }).ToList() ?? new List<ShipmentStatusHistoryDto>()
            };
        }
    }
}
