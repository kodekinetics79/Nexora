using ERP_RFQ_Automation.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IShipmentRepository
    {
        Task<IEnumerable<Shipment>> GetAllShipmentsAsync(long businessUnitId);
        Task<Shipment?> GetShipmentByIdAsync(long id, long businessUnitId);
        Task<Shipment> CreateShipmentAsync(Shipment shipment);
        Task<Shipment> UpdateShipmentAsync(Shipment shipment, long businessUnitId);
        Task DeleteShipmentAsync(long id, long businessUnitId);
        Task<string> GetNextShipmentNumberAsync(long businessUnitId);
        Task<IEnumerable<Shipment>> GetShipmentsByOrderIdAsync(long orderId, long businessUnitId);
    }
}
