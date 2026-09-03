using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
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

        /// <summary>
        /// Required only when <see cref="Activation"/> is "password". The default path sends an
        /// activation link and mints no credential at all — an administrator who types a
        /// colleague's password holds a working credential for somebody else's account.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// "invite" or "password". Omitted: "password" when a password was supplied (wire
        /// compatibility with older clients), "invite" otherwise.
        /// </summary>
        [StringLength(16)]
        public string? Activation { get; set; }

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

        /// <summary>
        /// RC-7: retained for wire compatibility ONLY. The server derives the actor from the
        /// validated token (see Authorization/ActorContext.cs) and NEVER reads this value —
        /// accepting it let a caller name anyone as the author of their own privilege change.
        /// </summary>
        [JsonIgnore]
        public string? CreatedBy { get; set; }

        public IFormFile? ImageFile { get; set; }
    }
}