using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.DTOs.AuthDTOs
{
    public class LoginResponseDTO
    {
        public long Id { get; set; }
        public string Email { get; set; } = null!;
        public string UserName { get; set; } = null!;
        public long? RoleId { get; set; }
        public string RoleName { get; set; } = null!;
        public bool IsSuperAdmin { get; set; }
        public bool IsManager { get; set; }
        public long? BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }
        public string Token { get; set; } = null!;

        /// <summary>
        /// True only when the same email+password is valid in more than one
        /// business unit and the client must pick one. Hidden from the normal
        /// success payload so the response shape is unchanged.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool RequiresBusinessUnitSelection { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LoginBusinessUnitOptionDTO>? BusinessUnits { get; set; }
    }

    public class LoginBusinessUnitOptionDTO
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
    }
}
