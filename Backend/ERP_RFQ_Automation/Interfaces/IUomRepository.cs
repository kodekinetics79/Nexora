using ERP_RFQ_Automation.DTOs.UomDTOs;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IUomRepository
    {
        Task<IEnumerable<UomResponseDTO>> GetAllAsync(long businessUnitId);
        Task<UomResponseDTO?> GetByIdAsync(int id);
        Task<UomResponseDTO> CreateAsync(UomCreateDTO dto, string userId);
        Task<UomResponseDTO> UpdateAsync(int id, UomUpdateDTO dto, string userId);
        Task<bool> DeleteAsync(int id);
    }
}
