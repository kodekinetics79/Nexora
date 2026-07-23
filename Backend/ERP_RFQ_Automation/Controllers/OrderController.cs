using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.DTOs.OrderDTOs;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // SEC-03: was anonymous; every action resolves BU from the JWT claim (claim wins over any client-supplied businessUnitId)
    public class OrderController : ControllerBase
    {
        private readonly IOrderService _orderService;
        private readonly IOrderRepository _repository;
        private readonly ERP_RFQ_Automation.Inventory.IOrderStockReservationService _stock;

        public OrderController(
            IOrderService orderService,
            IOrderRepository repository,
            ERP_RFQ_Automation.Inventory.IOrderStockReservationService stock)
        {
            _orderService = orderService;
            _repository = repository;
            _stock = stock;
        }

        private long ResolveBusinessUnitId(long? businessUnitId)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            return claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);
        }

        // POST: api/Order/5/allocate — reserve available stock for every order line (confirmation).
        [HttpPost("{id}/allocate")]
        [RequireModulePermission("Orders", PermissionAction.Edit)]
        public async Task<IActionResult> AllocateStock(long id, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnitId(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");
            var actor = User.FindFirst("email")?.Value ?? User.Identity?.Name;
            var result = await _stock.ReserveOrderAsync(buId, id, actor);
            return Ok(result);
        }

        // POST: api/Order/5/release-stock — release all active holds (order cancelled/unallocated).
        [HttpPost("{id}/release-stock")]
        [RequireModulePermission("Orders", PermissionAction.Edit)]
        public async Task<IActionResult> ReleaseStock(long id, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnitId(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");
            var actor = User.FindFirst("email")?.Value ?? User.Identity?.Name;
            var released = await _stock.ReleaseOrderAsync(buId, id, actor);
            return Ok(new { orderId = id, released });
        }

        // POST: api/Order/5/consume-stock — consume holds and decrement on-hand (goods issue/delivery).
        [HttpPost("{id}/consume-stock")]
        [RequireModulePermission("Orders", PermissionAction.Edit)]
        public async Task<IActionResult> ConsumeStock(long id, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnitId(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");
            var actor = User.FindFirst("email")?.Value ?? User.Identity?.Name;
            var consumed = await _stock.ConsumeOrderAsync(buId, id, actor);
            return Ok(new { orderId = id, consumed });
        }

        // GET: api/Order
        [HttpGet]
        [RequireModulePermission("Orders", PermissionAction.View)]
        public async Task<IActionResult> GetOrders([FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var orders = await _orderService.GetAllOrdersAsync(targetBUId);
                return Ok(orders);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        // GET: api/Order/5
        [HttpGet("{id}")]
        [RequireModulePermission("Orders", PermissionAction.View)]
        public async Task<IActionResult> GetOrder(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var order = await _orderService.GetOrderByIdAsync(id, targetBUId);
                if (order == null)
                {
                    return NotFound();
                }
                return Ok(order);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        // POST: api/Order
        [HttpPost]
        [RequireModulePermission("Orders", PermissionAction.Create)]
        public async Task<IActionResult> CreateManualOrder([FromBody] CreateOrderDto createOrderDto)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : createOrderDto.BusinessUnitId;

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var order = await _orderService.CreateManualOrderAsync(createOrderDto, targetBUId);
                return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // POST: api/Order/from-rfq/{rfqId}
        [HttpPost("from-rfq/{rfqId}")]
        [RequireModulePermission("Orders", PermissionAction.Create)]
        public async Task<IActionResult> CreateOrderFromRfq(long rfqId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var order = await _orderService.CreateOrderFromRfqAsync(rfqId, targetBUId);
                return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
            }
            catch (Exception ex)
            {
                 // Differentiate not found vs other errors if needed
                return BadRequest(new { message = ex.Message });
            }
        }

        // POST: api/Order/from-quote/{quoteId}
        [HttpPost("from-quote/{quoteId}")]
        [RequireModulePermission("Orders", PermissionAction.Create)]
        public async Task<IActionResult> CreateOrderFromQuote(long quoteId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var order = await _orderService.CreateOrderFromQuoteAsync(quoteId, targetBUId);
                return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // PUT: api/Order/5
        [HttpPut("{id}")]
        [RequireModulePermission("Orders", PermissionAction.Edit)]
        public async Task<IActionResult> UpdateOrder(long id, [FromBody] UpdateOrderDto updateOrderDto)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var updatedOrder = await _orderService.UpdateOrderAsync(id, updateOrderDto, businessUnitId);
                return Ok(updatedOrder);
            }
            catch (Exception ex)
            {
                 if (ex.Message == "Order not found") return NotFound();
                 return BadRequest(new { message = ex.Message });
            }
        }

        // DELETE: api/Order/5
        [HttpDelete("{id}")]
        [RequireModulePermission("Orders", PermissionAction.Delete)]
        public async Task<IActionResult> DeleteOrder(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _orderService.DeleteOrderAsync(id, targetBUId);
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        // GET: api/Order/customer/5
        [HttpGet("customer/{customerId}")]
        [RequireModulePermission("Orders", PermissionAction.View)]
        public async Task<IActionResult> GetOrdersByCustomer(long customerId, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var orders = await _orderService.GetOrdersByCustomerIdAsync(customerId, targetBUId);
                return Ok(orders);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpGet("{id}/invoice")]
        [RequireModulePermission("Orders", PermissionAction.View)]
        public async Task<IActionResult> GetInvoice(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var invoice = await _orderService.GetInvoiceDataAsync(id, targetBUId);
                if (invoice == null) return NotFound();
                return Ok(invoice);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpGet("stats")]
        [RequireModulePermission("Orders", PermissionAction.View)]
        public async Task<ActionResult<OrderStatsDTO>> GetOrderStats()
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");
                
                var stats = await _repository.GetOrderStatsAsync(businessUnitId);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
}
