using ERP_RFQ_Automation.DTOs.Contact;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IContactRepository
    {
        Task<(IEnumerable<ContactResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? firstName, string? lastName, string? email, long? customerId, long? supplierId, bool? isPrimary, bool? isActive, long businessUnitId);
        Task<Contact> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Contact contact, long businessUnitId, string actor);
        Task UpdateAsync(Contact contact, long businessUnitId, string actor, Guid expectedConcurrencyToken);
        Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken);
        Task<IEnumerable<CustomerDropdown>> GetCustomersAsync(long businessUnitId);
        Task<IEnumerable<SupplierDropDown>> GetSuppliersAsync(long businessUnitId);
    }
}
