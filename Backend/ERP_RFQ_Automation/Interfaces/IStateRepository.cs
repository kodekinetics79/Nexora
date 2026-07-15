using ERP_RFQ_Automation.DTOs.LocationDTOs;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IStateRepository
    {
        Task<IEnumerable<StateResponseDTO>> GetAllAsync(long buid);
        Task<StateResponseDTO?> GetByIdAsync(int id);
        Task<StateResponseDTO> CreateAsync(StateCreateDTO dto, string userId);
        Task<StateResponseDTO> UpdateAsync(int id, StateUpdateDTO dto, string userId);
        Task<bool> DeleteAsync(int id);
    }
}
