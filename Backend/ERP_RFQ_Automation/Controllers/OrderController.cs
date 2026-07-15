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

        public OrderController(IOrderService orderService, IOrderRepository repository)
        {
            _orderService = orderService;
            _repository = repository;
        }

        // GET: api/Order
        [HttpGet]
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
