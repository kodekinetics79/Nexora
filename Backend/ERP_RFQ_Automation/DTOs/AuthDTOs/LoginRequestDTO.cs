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

        /// <summary>
        /// Optional. The server derives the tenant from the email; this is only
        /// needed (and honored for backward compatibility) when the same email
        /// exists in multiple business units and the client must disambiguate.
        /// </summary>
        public long? BusinessUnitId { get; set; }
    }
}
