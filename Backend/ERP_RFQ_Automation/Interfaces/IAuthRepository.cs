using ERP_RFQ_Automation.DTOs.AuthDTOs;

namespace ERP_RFQ_Automation.Interfaces
{
    public interface IAuthRepository
    {
        Task<LoginResponseDTO> LoginAsync(LoginRequestDTO request);
    }
}