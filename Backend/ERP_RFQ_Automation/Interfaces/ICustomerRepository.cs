using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ICustomerRepository
    {
        Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? name, string? contactEmail, bool? isActive, string? docId, long businessUnitId);
        Task<Customer> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Customer customer, long businessUnitId, string actor);
        Task UpdateAsync(Customer customer, long businessUnitId, string actor, Guid expectedConcurrencyToken);
        Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken);
        Task<Customer?> GetByEmailAsync(string email, long businessUnitId);

    }
}
