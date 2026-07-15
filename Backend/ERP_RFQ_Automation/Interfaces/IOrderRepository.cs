using ERP_RFQ_Automation.DTOs.OrderDTOs;
using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IOrderRepository
    {
        Task<IEnumerable<Order>> GetAllOrdersAsync(long businessUnitId);
        Task<Order?> GetOrderByIdAsync(long id, long businessUnitId);
        Task<Order> CreateOrderAsync(Order order);
        Task<Order> UpdateOrderAsync(Order order, long businessUnitId);
        Task DeleteOrderAsync(long id, long businessUnitId);
        Task<string> GetNextOrderNumberAsync(long businessUnitId);
        Task<IEnumerable<Order>> GetOrdersByCustomerIdAsync(long customerId, long businessUnitId);
        Task<Order?> GetOrderForInvoiceAsync(long id, long businessUnitId);
        Task<OrderStatsDTO> GetOrderStatsAsync(long businessUnitId);
    }
}
