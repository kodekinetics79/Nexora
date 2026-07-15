using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.DTOs.UserDTO
{
    public class UserCreateRequestDTO
    {
        [Required]
        public string FirstName { get; set; } = null!;

        public string? MiddleName { get; set; }

        [Required]
        public string LastName { get; set; } = null!;

        [Required, EmailAddress]
        public string Email { get; set; } = null!;

        [Required]
        public string Password { get; set; } = null!;

        public string? ImageUrl { get; set; }

        public long? RoleId { get; set; }

        public long? TeamId { get; set; }

        public string? Timezone { get; set; }

        public string? Region { get; set; }

        public long? ManagerId { get; set; }

        [Required]
        public long Buid { get; set; }


        public long? UserGroupId { get; set; }

        public bool? IsActive { get; set; }

        public string? CreatedBy { get; set; }

        public IFormFile? ImageFile { get; set; }
    }
}