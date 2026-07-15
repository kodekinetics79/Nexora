using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.AuthDTOs
{
    public class LoginRequestDTO
    {
        [Required]
        [EmailAddress]
        [StringLength(255)]
        public string Email { get; set; } = null!;

        [Required]
        [StringLength(100, MinimumLength = 6)]
        public string Password { get; set; } = null!;

        [Required]
        public long BusinessUnitId { get; set; }
    }
}