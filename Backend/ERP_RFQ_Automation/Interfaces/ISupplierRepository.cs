using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ISupplierRepository
    {
        Task<(IEnumerable<SupplierResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize, long? id, string? name, string? contactEmail, long? currencyId, bool? isActive, string? docId, long businessUnitId);
        Task<Supplier> GetByIdAsync(long id, long businessUnitId);
        Task AddAsync(Supplier supplier);
        Task UpdateAsync(Supplier supplier, long businessUnitId);
        Task DeleteAsync(long id, long businessUnitId);
        /// <summary>
        /// <paramref name="tier"/> is an optional, already-canonicalised view filter (FR-QTM-01).
        /// It narrows and orders the list; it never gates who may be dispatched to (ruling R-A).
        /// </summary>
        Task<List<SupplierSearchResultDTO>> SearchSuppliersAsync(string? searchTerm, string? productCategory, long businessUnitId, string? tier = null);
    }

    public interface ISupplierNumberGenerator
    {
        string Generate(long supplierId, long businessUnitId);
    }

    public sealed class DeterministicSupplierNumberGenerator : ISupplierNumberGenerator
    {
        public string Generate(long supplierId, long businessUnitId)
        {
            if (supplierId <= 0 || businessUnitId <= 0)
                throw new ArgumentOutOfRangeException(nameof(supplierId), "Supplier and tenant identities must be persisted before numbering.");

            return $"SU{supplierId:D8}";
        }
    }
}
