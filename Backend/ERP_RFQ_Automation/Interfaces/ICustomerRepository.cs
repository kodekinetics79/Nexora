using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.CustomerDTOs;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ICustomerRepository
    {
        /// <summary>
        /// FR-CST-02: <paramref name="scope"/> is REQUIRED, not optional. It is a parameter rather
        /// than something the repository resolves for itself so that the caller cannot forget to
        /// pass it — omitting it is a compile error, not a silently tenant-wide read.
        /// </summary>
        Task<(IEnumerable<CustomerResponseDTO>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? name, string? contactEmail,
            bool? isActive, string? docId, long businessUnitId, AccountTeamScope scope);

        /// <summary>
        /// Throws <see cref="KeyNotFoundException"/> when the customer exists in the tenant but is
        /// outside <paramref name="scope"/>. Deliberately the same exception as "no such customer":
        /// a distinct 403 would confirm the record's existence to a caller who may not read it.
        /// </summary>
        Task<Customer> GetByIdAsync(long id, long businessUnitId, AccountTeamScope scope);

        Task AddAsync(Customer customer, long businessUnitId, string actor);
        Task UpdateAsync(Customer customer, long businessUnitId, string actor, Guid expectedConcurrencyToken);
        Task DeleteAsync(long id, long businessUnitId, string actor, Guid expectedConcurrencyToken);
        /// <summary>
        /// Resolves an email to a customer. Account-scoped for the same reason the list is: an
        /// unscoped lookup that returns a whole customer record is a side door around FR-CST-02,
        /// and "guess the email" is a cheaper enumeration than "guess the id". Returns null both
        /// when no customer holds the address and when the one that does is out of scope.
        /// </summary>
        Task<Customer?> GetByEmailAsync(string email, long businessUnitId, AccountTeamScope scope);
    }
}
