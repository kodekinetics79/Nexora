using ERP_RFQ_Automation.DTOs.OrderDTOs;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class OrderRepository : IOrderRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public OrderRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Order>> GetAllOrdersAsync(long businessUnitId)
        {
            return await _context.Orders
                .Where(o => o.BusinessUnitId == businessUnitId)
                .Include(o => o.Customer)
                .Include(o => o.Status)
                .Include(o => o.PaymentStatus)
                // The list read now carries the denomination too. GetOrderByIdAsync already
                // Included it; without it here, OrderDto.CurrencyCode would be null on the
                // Orders grid and every amount would again render without its currency.
                .Include(o => o.Currency)
                .Include(o => o.OrderItems)
                .Include(o => o.Shipments)
                .OrderByDescending(o => o.CreatedOn)
                .ToListAsync();
        }

        public async Task<Order?> GetOrderByIdAsync(long id, long businessUnitId)
        {
            return await _context.Orders
                .Include(o => o.Customer)
                .Include(o => o.Status)
                .Include(o => o.PaymentStatus)
                .Include(o => o.PaymentMethod)
                .Include(o => o.Currency)
                .Include(o => o.Quote)
                .Include(o => o.Rfq)
                .Include(o => o.Lead)
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Product)
                .Include(o => o.Shipments)
                .FirstOrDefaultAsync(o => o.Id == id && o.BusinessUnitId == businessUnitId);
        }

        public async Task<Order> CreateOrderAsync(Order order)
        {
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();
            return order;
        }

        public async Task<Order> UpdateOrderAsync(Order order, long businessUnitId)
        {
            var existing = await _context.Orders.AnyAsync(o => o.Id == order.Id && o.BusinessUnitId == businessUnitId);
            if (!existing)
                throw new KeyNotFoundException($"Order with ID {order.Id} not found in Business Unit {businessUnitId}.");

            _context.Entry(order).State = EntityState.Modified;
            await _context.SaveChangesAsync();
            return order;
        }

        public async Task DeleteOrderAsync(long id, long businessUnitId)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == id && o.BusinessUnitId == businessUnitId);
            if (order != null)
            {
                _context.Orders.Remove(order);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<Order?> GetOrderForInvoiceAsync(long id, long businessUnitId)
        {
            return await _context.Orders
                .Include(o => o.Customer).ThenInclude(c => c.Contacts)
                .Include(o => o.Status)
                // An invoice that states amounts without a currency is not a document anyone
                // should be sending to a customer.
                .Include(o => o.Currency)
                .Include(o => o.OrderItems).ThenInclude(i => i.Product)
                .Include(o => o.Quote).ThenInclude(q => q.Rfq).ThenInclude(r => r.Lead)
                .FirstOrDefaultAsync(o => o.Id == id && o.BusinessUnitId == businessUnitId);
        }

        public async Task<string> GetNextOrderNumberAsync(long businessUnitId)
        {
            var now = DateTime.Now;
            var prefix = $"ORD-{now:MM}{now:yy}-";
            
            var lastOrder = await _context.Orders
                .Where(o => o.BusinessUnitId == businessUnitId && o.OrderNo.StartsWith(prefix))
                .OrderByDescending(o => o.OrderNo)
                .Select(o => o.OrderNo)
                .FirstOrDefaultAsync();

            int nextSequence = 1;

            if (lastOrder != null)
            {
                var parts = lastOrder.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out int lastSeq))
                {
                    nextSequence = lastSeq + 1;
                }
            }

            return $"{prefix}{nextSequence:D6}";
        }

        public async Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(long customerId, long businessUnitId)
        {
             return await _context.Orders
                .Where(o => o.CustomerId == customerId && o.BusinessUnitId == businessUnitId)
                .Include(o => o.Status)
                .ToListAsync();
        }

        public async Task<OrderStatsDTO> GetOrderStatsAsync(long businessUnitId)
        {
            var orders = await _context.Orders
                .AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId)
                .Select(o => new { o.Id, o.StatusId, o.TotalAmount, o.CurrencyId })
                .ToListAsync();

            // FX fix: this used to be `orders.Sum(o => o.TotalAmount)`, which added AED, USD and
            // EUR order totals together as bare decimals and reported the result as one revenue
            // number. Order.TotalAmount is denominated by Order.CurrencyId, so that sum was only
            // ever correct for a single-currency tenant. Amounts are now converted into the
            // business unit's base currency using approved, effective-dated rates, and the total
            // FAILS CLOSED (null + a surfaced reason + a per-currency breakdown) when any order's
            // currency has no approved rate. Same contract as QuoteRepository.GetQuoteStatsAsync.
            var fx = new FxConversionService(_context);
            var total = await fx.TotalAsync(
                businessUnitId,
                orders.Select(o => new FxAmount(o.TotalAmount, o.CurrencyId)).ToArray(),
                DateTime.UtcNow);

            return new OrderStatsDTO
            {
                TotalOrders = orders.Count,
                PendingOrders = orders.Count(o => o.StatusId == 36), // Assuming 36 is Pending/Processing
                CompletedOrders = orders.Count(o => o.StatusId == 37), // Assuming 37 is Completed/Delivered
                TotalRevenue = total.Total,
                TotalRevenueCurrency = total.TargetCurrencyCode,
                TotalRevenueConverted = total.Converted,
                TotalRevenueUnavailableReason = total.UnavailableReason,
                RevenueByCurrency = total.Components
                    .Select(c => new OrderStatsCurrencyBreakdownDTO
                    {
                        CurrencyId = c.CurrencyId,
                        CurrencyCode = c.CurrencyCode,
                        Subtotal = c.Subtotal,
                        OrderCount = c.RowCount,
                        Converted = c.Converted,
                        ExchangeRate = c.Rate,
                        ConvertedSubtotal = c.ConvertedSubtotal,
                        Reason = c.Reason
                    })
                    .ToList()
            };
        }
    }
}
