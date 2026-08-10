using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.UserDTO
{
    public class ChangePasswordRequestDTO
    {
        /// <summary>
        /// Sec-A2: proof that whoever is holding this session is the account owner and not merely
        /// someone who found it open.
        ///
        /// <para>The cross-user takeover on this endpoint is closed (a caller may only change
        /// their OWN password), which made this look optional. It is not: without it, any borrowed
        /// session — an unlocked laptop, a stolen token, an XSS payload — converts into permanent
        /// ownership of the account, because the attacker sets a password the real user does not
        /// know and the real user is never signed out. Knowing the current password is the one
        /// thing a session thief does not have.</para>
        /// </summary>
        [Required]
        public string CurrentPassword { get; set; } = null!;

        [Required]
        public string NewPassword { get; set; } = null!;
    }
}