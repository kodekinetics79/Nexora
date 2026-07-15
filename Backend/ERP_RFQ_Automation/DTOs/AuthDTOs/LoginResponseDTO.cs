namespace ERP_RFQ_Automation.DTOs.AuthDTOs
{
    public class LoginResponseDTO
    {
        public long Id { get; set; }
        public string Email { get; set; } = null!;
        public string UserName { get; set; } = null!;
        public long? RoleId { get; set; }
        public string RoleName { get; set; } = null!;
        public long? BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }
        public string Token { get; set; } = null!;
    }
}