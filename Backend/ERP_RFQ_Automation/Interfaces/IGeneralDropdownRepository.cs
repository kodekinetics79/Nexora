using ERP_RFQ_Automation.DTOs.GeneralDropdown;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IGeneralDropdownRepository
    {
        Task<IEnumerable<GeneralDropdownDto>> GetCountriesAsync(long businessUnitId);
        Task<IEnumerable<GeneralDropdownDto>> GetStatesAsync(long businessUnitId, int? countryId = null);
        Task<IEnumerable<GeneralDropdownDto>> GetCitiesAsync(long businessUnitId, int? stateId = null, int? countryId = null);
        Task<IEnumerable<GeneralDropdownDto>> GetCategoriesAsync(long businessUnitId);
        Task<IEnumerable<GeneralDropdownDto>> GetWarehousesAsync(long businessUnitId);
        Task<IEnumerable<GeneralDropdownDto>> GetSuppliersAsync(long businessUnitId);
        Task<IEnumerable<GeneralDropdownDto>> GetStatusesAsync(long businessUnitId);

    }
}
