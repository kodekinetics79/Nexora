using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Contact
{
    public class ContactCreateRequestDTO
    {
        public long? CustomerId { get; set; }
        public long? SupplierId { get; set; }
        [Required]
        public string FirstName { get; set; } = null!;
        public string? MiddleName { get; set; }
        [Required]
        public string LastName { get; set; } = null!;
        public string? Email { get; set; }
        public string? PhoneNo { get; set; }
        public string? MobileNo { get; set; }
        public string? Position { get; set; }
        public bool? IsPrimary { get; set; }
        public bool? IsActive { get; set; }
        [Required]
        public string CreatedBy { get; set; } = null!;
    }
}
