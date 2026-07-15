using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IOrderService
    {
        Task<IEnumerable<OrderDto>> GetAllOrdersAsync(long businessUnitId);
        Task<OrderDto?> GetOrderByIdAsync(long id, long businessUnitId);
        Task<OrderDto> CreateManualOrderAsync(CreateOrderDto createOrderDto, long businessUnitId);
        Task<OrderDto> CreateOrderFromRfqAsync(long rfqId, long businessUnitId);
        Task<OrderDto> CreateOrderFromQuoteAsync(long quoteId, long businessUnitId);
        Task<OrderDto> UpdateOrderAsync(long id, UpdateOrderDto updateOrderDto, long businessUnitId);
        Task DeleteOrderAsync(long id, long businessUnitId);
        Task<IEnumerable<OrderDto>> GetOrdersByCustomerIdAsync(long customerId, long businessUnitId);
        Task<InvoiceDto?> GetInvoiceDataAsync(long orderId, long businessUnitId);
    }
}
