using ERP_RFQ_Automation.DTOs.LocationDTOs;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ICountryRepository
    {
        Task<IEnumerable<CountryResponseDTO>> GetAllAsync(long buid);
        Task<CountryResponseDTO?> GetByIdAsync(int id);
        Task<CountryResponseDTO> CreateAsync(CountryCreateDTO dto, string userId);
        Task<CountryResponseDTO> UpdateAsync(int id, CountryUpdateDTO dto, string userId);
        Task<bool> DeleteAsync(int id);
    }
}
