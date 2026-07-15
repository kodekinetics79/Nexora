using ERP_RFQ_Automation.DTOs.LocationDTOs;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface ICityRepository
    {
        Task<IEnumerable<CityResponseDTO>> GetAllAsync(long buid);
        Task<CityResponseDTO?> GetByIdAsync(int id);
        Task<CityResponseDTO> CreateAsync(CityCreateDTO dto, string userId);
        Task<CityResponseDTO> UpdateAsync(int id, CityUpdateDTO dto, string userId);
        Task<bool> DeleteAsync(int id);
    }
}
