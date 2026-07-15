namespace ERP_RFQ_Automation.DTOs.UserDTO
{
    public class UserChangePasswordRequestDTO
    {
        public long UserId { get; set; }
        public string NewPassword { get; set; }
    }
}
