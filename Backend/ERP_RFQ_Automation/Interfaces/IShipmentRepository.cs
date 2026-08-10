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
        /// <summary>
        /// Withdraws a shipment that never despatched, attributed and reasoned. Refuses anything in
        /// <c>DeliveryStatuses.Despatched</c> and anything carrying a proof of delivery — see the
        /// implementation for why the two checks are independent.
        /// </summary>
        Task DeleteShipmentAsync(long id, long businessUnitId, string reason, string actor);
        Task<string> GetNextShipmentNumberAsync(long businessUnitId);
        Task<IEnumerable<Shipment>> GetShipmentsByOrderIdAsync(long orderId, long businessUnitId);
    }
}
