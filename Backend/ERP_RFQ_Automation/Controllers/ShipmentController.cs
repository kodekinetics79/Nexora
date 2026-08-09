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
        private readonly ERP_RFQ_Automation.Inventory.IOrderStockReservationService _stock;

        public ShipmentController(
            IShipmentRepository repository,
            ErpRfqAutomationContext context,
            ERP_RFQ_Automation.Inventory.IOrderStockReservationService stock)
        {
            _repository = repository;
            _context = context;
            _stock = stock;
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
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : dto.BusinessUnitId;

            if (targetBUId <= 0)
                return BadRequest(new { message = "Business Unit ID is required." });

            // TENANT ISOLATION: the order used to be resolved with `_context.Orders.FindAsync(id)`,
            // which carries no business-unit predicate. A caller could therefore create a shipment
            // against another tenant's order — and now that a shipment issues stock, that would
            // have decremented the victim tenant's on-hand as well. The order is proven to belong
            // to the caller BEFORE anything is written.
            var orderExists = await _context.Orders.AsNoTracking()
                .AnyAsync(o => o.Id == dto.OrderId && o.BusinessUnitId == targetBUId);
            if (!orderExists)
                return NotFound(new { message = $"Order {dto.OrderId} was not found in this business unit." });

            // The declared lines drive a goods issue, so they are validated BEFORE anything is
            // written. OrderItem ids are global (the table carries no BusinessUnitId — isolation
            // is parent-derived), so a caller could otherwise name another order's — or another
            // tenant's — line and have it recorded on this shipment.
            var orderLineIds = await _context.Set<OrderItem>().AsNoTracking()
                .Where(i => i.OrderId == dto.OrderId)
                .Select(i => i.Id)
                .ToListAsync();
            var unknownLines = dto.Items.Select(i => i.OrderItemId).Distinct()
                .Except(orderLineIds).OrderBy(id => id).ToList();
            if (unknownLines.Count > 0)
                return NotFound(new
                {
                    message = $"Order line(s) {string.Join(", ", unknownLines)} do not belong to order {dto.OrderId}."
                });
            if (dto.Items.Any(i => i.Quantity <= 0))
                return BadRequest(new { message = "Every shipment line must declare a positive quantity." });

            try
            {
                // ATOMICITY: the shipment row, the order status transition and the goods issue are
                // one physical event. They used to be three independent SaveChanges calls wrapped
                // in `catch (Exception) => BadRequest`, so a stock failure after the shipment row
                // had already committed reported "bad request" while leaving shipped goods with
                // undecremented stock and no trace. One transaction now covers all three.
                var strategy = _context.Database.CreateExecutionStrategy();
                var created = await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    // Read inside the transaction, and inside the tenant predicate, so the master
                    // reference stamped on the despatch note is the one the order held when the
                    // goods were issued — and can only ever be this tenant's.
                    var order = await _context.Orders.AsNoTracking()
                        .SingleAsync(o => o.Id == dto.OrderId && o.BusinessUnitId == targetBUId);

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

                    // FR-DLM-01: the delivery note carries the case rather than re-deriving it.
                    // Set before the row is written, so a shipment cannot exist without it.
                    shipment.InheritCommercialIdentity(order);

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

                    var row = await _repository.CreateShipmentAsync(shipment);

                    // The goods issue consumes exactly what THIS shipment declares.
                    await IssueOrderStockAsync(targetBUId, dto.OrderId, dto.Items);

                    // Order status follows the goods, not the existence of a shipment: SHIPPED is
                    // only reached once every open line has been shipped in full. It used to be
                    // set unconditionally, so a 10-of-100 shipment closed the order.
                    await MarkOrderShippedIfCompleteAsync(targetBUId, dto.OrderId);

                    await transaction.CommitAsync();
                    return row;
                });

                return CreatedAtAction(nameof(GetShipment), new { id = created.Id }, MapToDto(created));
            }
            catch (ERP_RFQ_Automation.Inventory.InsufficientStockException ex)
            {
                // The books say the goods are not there. Surfacing this as 409 (rather than
                // swallowing it into a 200) is the whole point: nothing was written.
                return Conflict(new { message = ex.Message });
            }
            catch (ERP_RFQ_Automation.Inventory.StockLedgerException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// A shipment IS the goods issue. Before this existed, on-hand only fell when an operator
        /// remembered to call <c>POST /api/Order/{id}/consume-stock</c> by hand, so shipping and
        /// invoicing left stock at its opening balance forever.
        ///
        /// The order's holds are (re)allocated first because an order that was shipped without
        /// anyone calling <c>/allocate</c> has no holds at all, and consuming nothing would move no
        /// stock — the exact defect this closes. Both calls are idempotent per order line and per
        /// reservation, so a retried or re-shipped request never decrements twice, and both join
        /// the caller's ambient transaction rather than opening their own.
        ///
        /// PARTIAL SHIPMENT: the issue is driven by the shipment's OWN per-line quantities, not by
        /// the order. The UI offers an editable Ship Qty capped at the ordered quantity
        /// (CreateShipmentPage.tsx), and the previous order-scoped ConsumeOrderAsync read no
        /// quantity at all — shipping 10 of 100 decremented on-hand by 100, posted an Issue
        /// movement for 100 and consumed the whole hold, stranding 90 units that no release could
        /// recover, with ReconcileLedgerAsync reporting zero drift because the movement and the
        /// balance agreed with each other.
        /// </summary>
        private async Task IssueOrderStockAsync(
            long businessUnitId, long orderId, IEnumerable<CreateShipmentItemDto> items)
        {
            var actor = User.FindFirst("email")?.Value ?? User.Identity?.Name ?? "system";

            // Duplicate lines in one shipment are summed rather than letting the last one win.
            var declared = items
                .Where(i => i.Quantity > 0)
                .GroupBy(i => i.OrderItemId)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
            if (declared.Count == 0) return; // a shipment that declares no goods issues none

            await _stock.ReserveOrderAsync(businessUnitId, orderId, actor);
            await _stock.ConsumeOrderLinesAsync(businessUnitId, orderId, declared, actor);
        }

        /// <summary>
        /// Flips the order to SHIPPED only when every open line has been shipped in full, counting
        /// EVERY shipment against the order (a line may be delivered across several). A partial
        /// shipment leaves the status untouched, because "SHIPPED" on an order with 90 of 100 units
        /// still to leave the warehouse is a lie the rest of the product then acts on.
        /// </summary>
        private async Task MarkOrderShippedIfCompleteAsync(long businessUnitId, long orderId)
        {
            var order = await _context.Orders
                .SingleOrDefaultAsync(o => o.Id == orderId && o.BusinessUnitId == businessUnitId);
            if (order == null) return;

            var ordered = await _context.Set<OrderItem>().AsNoTracking()
                .Where(i => i.OrderId == orderId && i.IsActive)
                .Select(i => new { i.Id, i.Quantity })
                .ToListAsync();
            if (ordered.Count == 0) return;

            var shipped = await _context.ShipmentItems.AsNoTracking()
                .Where(si => si.IsActive && si.Shipment.OrderId == orderId
                             && si.Shipment.BusinessUnitId == businessUnitId && si.Shipment.IsActive)
                .GroupBy(si => si.OrderItemId)
                .Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
                .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity);

            if (!ordered.All(line => shipped.GetValueOrDefault(line.Id) >= line.Quantity)) return;

            var shippedStatus = await _context.SetupMasters.FirstOrDefaultAsync(s =>
                s.BusinessUnitId == businessUnitId && s.SetupType == "OrderStatus"
                && (s.SetupCode == "SHIPPED" || s.SetupValue.ToUpper() == "SHIPPED"));
            if (shippedStatus == null) return;

            order.StatusId = shippedStatus.SetupId;
            _context.Orders.Update(order);
            await _context.SaveChangesAsync();
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
