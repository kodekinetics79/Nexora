using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.UserDTO
{
    public class ChangePasswordRequestDTO
    {
        [Required]
        public string NewPassword { get; set; } = null!;
    }
}