using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            if (dto.Items is null || dto.Items.Count == 0)
                return BadRequest(new { message = "A shipment must contain at least one active order line." });
            if (!await IsAuthorizedShipmentStatusAsync(targetBUId, dto.StatusId))
                return BadRequest(new { message = "The shipment status is not active for this business unit." });

            var requestHash = HashCreateCommand(dto);
            var idempotencyKey = ShipmentIdempotencyKey(dto, requestHash);
            if (idempotencyKey.Length > 160)
                return BadRequest(new { message = "Idempotency-Key must not exceed 160 characters." });

            var replay = await FindShipmentReplayAsync(targetBUId, idempotencyKey);
            if (replay is not null)
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                    return Conflict(new { message = "That Idempotency-Key was already used for a different shipment command." });
                return Ok(MapToDto(replay));
            }

            // TENANT ISOLATION: the order used to be resolved with `_context.Orders.FindAsync(id)`,
            // which carries no business-unit predicate. A caller could therefore create a shipment
            // against another tenant's order — and now that a shipment issues stock, that would
            // have decremented the victim tenant's on-hand as well. The order is proven to belong
            // to the caller BEFORE anything is written.
            var sourceOrder = await _context.Orders.AsNoTracking().Include(x => x.Status)
                .SingleOrDefaultAsync(o => o.Id == dto.OrderId && o.BusinessUnitId == targetBUId && o.IsActive);
            if (sourceOrder is null)
                return NotFound(new { message = $"Order {dto.OrderId} was not found in this business unit." });

            // The declared lines drive a goods issue, so they are validated BEFORE anything is
            // written. OrderItem ids are global (the table carries no BusinessUnitId — isolation
            // is parent-derived), so a caller could otherwise name another order's — or another
            // tenant's — line and have it recorded on this shipment.
            var orderLines = await _context.Set<OrderItem>().AsNoTracking()
                .Where(i => i.OrderId == dto.OrderId && i.IsActive)
                .Select(i => new { i.Id, i.Quantity })
                .ToListAsync();
            var orderLineIds = orderLines.Select(i => i.Id).ToList();
            var unknownLines = dto.Items.Select(i => i.OrderItemId).Distinct()
                .Except(orderLineIds).OrderBy(id => id).ToList();
            if (unknownLines.Count > 0)
                return NotFound(new
                {
                    message = $"Order line(s) {string.Join(", ", unknownLines)} do not belong to order {dto.OrderId}."
                });
            if (dto.Items.Any(i => i.Quantity <= 0))
                return BadRequest(new { message = "Every shipment line must declare a positive quantity." });

            // OVER-SHIPMENT. The ordered quantity was a ceiling in the browser only —
            // CreateShipmentPage set `max` on a number input, and the server rejected nothing but
            // a non-positive quantity. A caller could therefore ship 150 against an order for 100:
            // the shipment was accepted and written, the stock left, and the shipment could then
            // never be invoiced, because the INVOICE ceiling is enforced server-side
            // (CommercialFinanceApplicationService: alreadyInvoiced + requested > source.Quantity).
            // Physical loss behind an order that looks clean on every screen.
            //
            // Cumulative across shipments, exactly as the invoice check is cumulative across
            // invoices — a per-request check would let three shipments of 50 past an order for 100.
            //
            // FR-DLM-05: the ceiling counts DESPATCHED shipments only. A cancelled despatch put
            // nothing on a lorry, and leaving it in the total would permanently consume the order
            // line's remaining quantity with goods that never left — wiring-contract failure #9,
            // a new state that every hand-written guard has to be told about. The set lives in
            // DeliveryStatuses.Despatched so the next status added is a visible decision in one
            // file rather than a clause somebody forgets in one method out of three.
            var declaredByLine = dto.Items.GroupBy(i => i.OrderItemId)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
            var orderedByLine = orderLines.ToDictionary(x => x.Id, x => x.Quantity);
            var overShipped = await OverShippedLinesAsync(targetBUId, dto.OrderId, orderedByLine, declaredByLine);
            if (overShipped.Count > 0)
                return Conflict(new
                {
                    message = "Shipment quantity exceeds the remaining quantity for "
                              + string.Join("; ", overShipped) + "."
                });
            if (IsTerminalOrder(sourceOrder))
                return Conflict(new { message = $"A {OrderStatus(sourceOrder)} order cannot be shipped." });

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
                    _context.ChangeTracker.Clear();
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    // Read inside the transaction, and inside the tenant predicate, so the master
                    // reference stamped on the despatch note is the one the order held when the
                    // goods were issued — and can only ever be this tenant's.
                    // Serialise every despatch for one order. Idempotency protects retries of ONE
                    // command; this lock protects two separately-keyed commands arriving together.
                    // Both must observe the quantity written by the other before either may issue
                    // stock, otherwise two valid 60-unit requests can despatch 120 against 100.
                    var order = await LockActiveOrderForShipmentAsync(targetBUId, dto.OrderId)
                        ?? throw new InvalidOperationException(
                            $"Order {dto.OrderId} is no longer active in this business unit.");
                    var lockedOrderLines = await _context.Set<OrderItem>().AsNoTracking()
                        .Where(i => i.OrderId == dto.OrderId && i.IsActive)
                        .Select(i => new { i.Id, i.Quantity })
                        .ToListAsync();
                    var vanishedLines = declaredByLine.Keys.Except(lockedOrderLines.Select(x => x.Id)).OrderBy(x => x).ToList();
                    if (vanishedLines.Count > 0)
                        throw new InvalidOperationException(
                            $"Order line(s) {string.Join(", ", vanishedLines)} are no longer active on order {dto.OrderId}.");
                    var concurrentOverShipment = await OverShippedLinesAsync(
                        targetBUId, dto.OrderId,
                        lockedOrderLines.ToDictionary(x => x.Id, x => x.Quantity), declaredByLine);
                    if (concurrentOverShipment.Count > 0)
                        throw new InvalidOperationException(
                            "Shipment quantity exceeds the remaining quantity for "
                            + string.Join("; ", concurrentOverShipment) + ".");
                    if (IsTerminalOrder(order))
                        throw new InvalidOperationException($"A {OrderStatus(order)} order cannot be shipped.");

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
                        IdempotencyKey = idempotencyKey,
                        RequestHash = requestHash,
                        ShippingCost = dto.ShippingCost,
                        LabelUrl = dto.LabelUrl,
                        ShippingAddress = dto.ShippingAddress,
                        DeliveryCityId = dto.DeliveryCityId,
                        Notes = dto.Notes,
                        CreatedBy = User.Identity?.Name ?? "system",
                        CreatedOn = DateTime.Now,
                        IsActive = true,
                        // FR-DLM-05. The governed lifecycle, alongside the tenant's own picklist
                        // label in StatusId. DISPATCHED and not SCHEDULED, because this call
                        // ISSUES THE STOCK a few lines below: the goods leave in the same
                        // transaction, so recording them as still in the warehouse would make the
                        // governed status disagree with the inventory ledger from the first row.
                        // A shipment that is planned but not yet gone is a scheduling feature and
                        // is out of scope under R22.
                        DeliveryStatus = DeliveryStatuses.Dispatched,
                        DeliveryStatusChangedBy = User.Identity?.Name ?? "system",
                        DeliveryStatusChangedOn = DateTime.UtcNow
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

                    // The goods issue consumes exactly what THIS shipment declares, against the
                    // despatch note that was just written — so the lot declarations it makes name
                    // the delivery note the material left on.
                    await IssueOrderStockAsync(targetBUId, dto.OrderId, dto.Items, row.Id,
                        dto.ComplianceOverrideReason);

                    // Order status follows the goods, not the existence of a shipment: SHIPPED is
                    // only reached once every open line has been shipped in full. It used to be
                    // set unconditionally, so a 10-of-100 shipment closed the order.
                    await MarkOrderShippedIfCompleteAsync(targetBUId, dto.OrderId);

                    await transaction.CommitAsync();
                    return row;
                });

                return CreatedAtAction(nameof(GetShipment), new { id = created.Id }, MapToDto(created));
            }
            catch (DbUpdateException ex) when (IsShipmentIdempotencyConflict(ex))
            {
                _context.ChangeTracker.Clear();
                var replayAfterRace = await FindShipmentReplayAsync(targetBUId, idempotencyKey);
                if (replayAfterRace is not null &&
                    string.Equals(replayAfterRace.RequestHash, requestHash, StringComparison.Ordinal))
                    return Ok(MapToDto(replayAfterRace));
                return Conflict(new { message = "That Idempotency-Key was already used for a different shipment command." });
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
            catch (Inventory.IncompleteGoodsIssueException ex)
            {
                // Nothing was written: the despatch note, the goods issue and the status
                // transition share one transaction. 409 rather than 200-with-a-warning, because
                // a warning on a delivery note is a warning nobody reads at the loading bay.
                return Conflict(new { message = ex.Message });
            }
            catch (ERP_RFQ_Automation.Inventory.QuarantinedLotIssueException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (ERP_RFQ_Automation.Traceability.MaterialTraceabilityValidationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (ERP_RFQ_Automation.Traceability.MaterialTraceabilityConflictException ex)
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
            long businessUnitId, long orderId, IEnumerable<CreateShipmentItemDto> items,
            long shipmentId, string? complianceOverrideReason)
        {
            var actor = User.FindFirst("email")?.Value ?? User.Identity?.Name ?? "system";

            // Duplicate lines in one shipment are summed rather than letting the last one win.
            var declared = items
                .Where(i => i.Quantity > 0)
                .GroupBy(i => i.OrderItemId)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
            if (declared.Count == 0) return; // a shipment that declares no goods issues none

            await _stock.ReserveOrderAsync(businessUnitId, orderId, actor);
            var issue = await _stock.ConsumeOrderLinesAsync(businessUnitId, orderId, declared, actor,
                shipmentId, complianceOverrideReason);

            // THE RESULT IS NOT OPTIONAL. Both calls above used to be awaited and discarded.
            // Partial allocation deliberately does not throw — a short order must still be able to
            // raise a supplier purchase order for the balance — so an order whose stock had been
            // quarantined between confirmation and despatch reserved nothing, consumed nothing,
            // and produced a delivery note for goods that never moved. The order was then marked
            // SHIPPED because MarkOrderShippedIfCompleteAsync counts shipment LINES rather than
            // issued QUANTITY, so nothing downstream ever noticed.
            //
            // A shipment is one physical event: if any line could not issue what it declared, the
            // whole shipment fails and the transaction rolls the despatch note back with it.
            if (issue.IsShort)
                throw new ERP_RFQ_Automation.Inventory.IncompleteGoodsIssueException(orderId, issue.ShortLines);
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
                             && si.Shipment.BusinessUnitId == businessUnitId && si.Shipment.IsActive
                             && DeliveryStatuses.DespatchedForQuery.Contains(si.Shipment.DeliveryStatus))
                .GroupBy(si => si.OrderItemId)
                .Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
                .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity);

            if (!ordered.All(line => shipped.GetValueOrDefault(line.Id) >= line.Quantity)) return;

            // Resolve through the same canonical, active-only rule used by provisioning and every
            // other governed lifecycle. Legacy tenants may spell this type "Order Status" or keep
            // a lower-case code; neither is a reason to strand a physically completed despatch.
            order.StatusId = await LifecycleStatusCatalog.ResolveIdAsync(
                _context, businessUnitId, "Order", "SHIPPED");
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
                if (!await IsAuthorizedShipmentStatusAsync(claimBUId, dto.StatusId))
                    return BadRequest(new { message = "The shipment status is not active for this business unit." });

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

        /// <summary>
        /// Withdraws a shipment that never despatched. A reason is required and the actor is taken
        /// from the token, never from the request — see
        /// <c>ShipmentRepository.DeleteShipmentAsync</c> for what this refuses and why.
        ///
        /// <para>Every refusal reaches the caller as its own status code with the server's own
        /// sentence: 400 for a missing reason, 404 for a shipment that is not this tenant's, 409 for
        /// a despatched or proved shipment. This used to be one <c>catch (Exception)</c> returning
        /// a 500 with the message stringified into it, so a governed refusal was indistinguishable
        /// from a database outage on the screen and in the logs.</para>
        /// </summary>
        [HttpDelete("{id}")]
        [RequireModulePermission("Shipments", PermissionAction.Delete)]
        public async Task<IActionResult> DeleteShipment(
            long id, [FromQuery] string? reason = null, [FromQuery] long? businessUnitId = null)
        {
            // TryParse, not Parse: this block sits outside the try that used to swallow everything
            // into a 500, and a malformed claim must not become an unhandled exception.
            _ = long.TryParse(User.FindFirst("businessUnitId")?.Value, out var claimBUId);
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);
            if (targetBUId <= 0)
                return BadRequest(new { message = "Business Unit ID is required." });

            // The actor comes from the token. A destructive verb attributed to a name the caller
            // supplied is not attribution.
            var actor = User.FindFirst("email")?.Value ?? User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor))
                return Unauthorized(new
                {
                    message = "A shipment can only be withdrawn by a named authenticated user."
                });

            try
            {
                await _repository.DeleteShipmentAsync(id, targetBUId, reason ?? string.Empty, actor);
                return NoContent();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
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

        /// <summary>
        /// A quantity as an operator reads it: no trailing scale zeros, invariant separators.
        /// Used only in messages, never in arithmetic.
        /// </summary>
        private static string Units(decimal quantity)
            => quantity.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private async Task<List<string>> OverShippedLinesAsync(
            long businessUnitId, long orderId, IReadOnlyDictionary<long, decimal> orderedByLine,
            IReadOnlyDictionary<long, decimal> declaredByLine)
        {
            var alreadyShipped = await _context.ShipmentItems.AsNoTracking()
                .Where(si => si.IsActive && si.Shipment.OrderId == orderId
                             && si.Shipment.BusinessUnitId == businessUnitId && si.Shipment.IsActive
                             && DeliveryStatuses.DespatchedForQuery.Contains(si.Shipment.DeliveryStatus))
                .GroupBy(si => si.OrderItemId)
                .Select(g => new { OrderItemId = g.Key, Quantity = g.Sum(x => x.Quantity) })
                .ToDictionaryAsync(x => x.OrderItemId, x => x.Quantity);

            return orderedByLine
                .Where(line => declaredByLine.ContainsKey(line.Key)
                               && alreadyShipped.GetValueOrDefault(line.Key) + declaredByLine[line.Key] > line.Value)
                .OrderBy(line => line.Key)
                .Select(line => $"line {line.Key}: {Units(line.Value)} ordered, "
                                + $"{Units(alreadyShipped.GetValueOrDefault(line.Key))} already shipped, "
                                + $"{Units(declaredByLine[line.Key])} declared now")
                .ToList();
        }

        private async Task<Order?> LockActiveOrderForShipmentAsync(long businessUnitId, long orderId)
        {
            IQueryable<Order> orders = _context.Orders;
            if (_context.Database.IsNpgsql())
                orders = _context.Orders.FromSqlInterpolated(
                    $"SELECT * FROM \"Orders\" WHERE \"ID\" = {orderId} FOR UPDATE");
            return await orders.Include(x => x.Status).Include(x => x.Currency)
                .SingleOrDefaultAsync(x => x.Id == orderId && x.BusinessUnitId == businessUnitId && x.IsActive);
        }

        private Task<bool> IsAuthorizedShipmentStatusAsync(long businessUnitId, long statusId)
            => IsActiveShipmentStatusAsync(_context, businessUnitId, statusId);

        /// <summary>
        /// The one predicate every shipment mutation gates its status on: an ACTIVE
        /// <c>ShipmentStatus</c> row belonging to THIS business unit. Static and internal so the
        /// tenant-baseline tests can prove a freshly provisioned tenant satisfies exactly this
        /// query rather than a paraphrase of it.
        /// </summary>
        internal static async Task<bool> IsActiveShipmentStatusAsync(
            ErpRfqAutomationContext context, long businessUnitId, long statusId)
            => statusId > 0 && await context.SetupMasters.AsNoTracking().AnyAsync(status =>
                status.SetupId == statusId && status.BusinessUnitId == businessUnitId && status.IsActive != false
                && status.SetupType.ToLower().Replace(" ", "") == "shipmentstatus");

        private static string OrderStatus(Order order)
            => LifecyclePolicy.Canonicalize("Order", order.Status?.SetupCode, order.Status?.SetupValue);

        private static bool IsTerminalOrder(Order order)
            => OrderStatus(order) is "SHIPPED" or "DELIVERED" or "COMPLETED" or "CANCELLED" or "CANCELED";

        private string ShipmentIdempotencyKey(CreateShipmentDto dto, string requestHash)
        {
            var header = Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(header)) return header;
            if (!string.IsNullOrWhiteSpace(dto.ExternalId))
                return $"external:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dto.ExternalId.Trim()))).ToLowerInvariant()}";

            // Backward-compatible callers receive an isolated command identity. New browser and
            // integration clients should send the header; legacy calls remain valid but cannot
            // accidentally alias two separately intended partial despatches.
            return $"legacy:{Guid.NewGuid():N}:{requestHash[..16]}";
        }

        private static string HashCreateCommand(CreateShipmentDto dto)
        {
            var canonical = JsonSerializer.Serialize(new
            {
                dto.OrderId,
                dto.StatusId,
                dto.ShipmentDate,
                dto.EstimatedDeliveryDate,
                Carrier = dto.Carrier?.Trim(),
                ServiceLevel = dto.ServiceLevel?.Trim(),
                TrackingNumber = dto.TrackingNumber?.Trim(),
                ExternalId = dto.ExternalId?.Trim(),
                dto.ShippingCost,
                LabelUrl = dto.LabelUrl?.Trim(),
                ShippingAddress = dto.ShippingAddress?.Trim(),
                dto.DeliveryCityId,
                Notes = dto.Notes?.Trim(),
                ComplianceOverrideReason = dto.ComplianceOverrideReason?.Trim(),
                Items = dto.Items.OrderBy(x => x.OrderItemId).ThenBy(x => x.Quantity)
                    .Select(x => new { x.OrderItemId, x.Quantity, Notes = x.Notes?.Trim() })
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        private async Task<Shipment?> FindShipmentReplayAsync(long businessUnitId, string key)
            => await _context.Shipments.AsNoTracking()
                .Include(x => x.Order).ThenInclude(x => x.Currency)
                .Include(x => x.Status)
                .Include(x => x.DeliveryCity).ThenInclude(x => x!.State)
                .Include(x => x.ShipmentItems).ThenInclude(x => x.OrderItem).ThenInclude(x => x.Product)
                .Include(x => x.ShipmentStatusHistories).ThenInclude(x => x.PreviousStatus)
                .Include(x => x.ShipmentStatusHistories).ThenInclude(x => x.NewStatus)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key);

        private static bool IsShipmentIdempotencyConflict(DbUpdateException exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
                if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                    postgres.ConstraintName == "UX_Shipments_BU_IdempotencyKey")
                    return true;
            return false;
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
                CurrencyCode = shipment.Order?.Currency?.Code,
                LabelUrl = shipment.LabelUrl,
                ShippingAddress = shipment.ShippingAddress,
                // FR-DLM-05 / FR-DLM-01. The governed state and the governed region reach the API
                // contract and the client type, not just the table.
                DeliveryStatus = shipment.DeliveryStatus,
                DeliveryStatusChangedOn = shipment.DeliveryStatusChangedOn,
                DeliveryStatusChangedBy = shipment.DeliveryStatusChangedBy,
                DeliveryCityId = shipment.DeliveryCityId,
                DeliveryCityName = shipment.DeliveryCity?.CityName,
                DeliveryRegionName = shipment.DeliveryCity?.State?.StateName,
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
