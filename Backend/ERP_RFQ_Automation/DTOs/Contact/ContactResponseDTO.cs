namespace ERP_RFQ_Automation.DTOs.Contact
{
    public class ContactResponseDTO
    {
        public long Id { get; set; }
        public long? CustomerId { get; set; }
        public string? CustomerName { get; set; }
        public long? SupplierId { get; set; }
        public string? SupplierName { get; set; }
        public string FirstName { get; set; } = null!;
        public string? MiddleName { get; set; }
        public string LastName { get; set; } = null!;
        public string? Email { get; set; }
        public string? PhoneNo { get; set; }
        public string? MobileNo { get; set; }
        public string? Position { get; set; }
        public bool? IsPrimary { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}
