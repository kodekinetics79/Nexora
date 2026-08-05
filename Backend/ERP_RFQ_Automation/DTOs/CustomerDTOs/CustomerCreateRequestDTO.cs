using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.CustomerDTOs
{
    public class CustomerCreateRequestDTO
    {
        [Required, StringLength(255), RegularExpression(@".*\S.*", ErrorMessage = "Name cannot be blank.")]
        public string Name { get; set; } = null!;
        [EmailAddress, StringLength(320)]
        public string? ContactEmail { get; set; }
        [StringLength(100)]
        public string? ImageUrl { get; set; }
        [StringLength(255)]
        public string? BillingAddressLine1 { get; set; }
        [StringLength(255)]
        public string? BillingAddressLine2 { get; set; }
        [StringLength(100)]
        public string? BillingCity { get; set; }
        [StringLength(100)]
        public string? BillingState { get; set; }
        [StringLength(100)]
        public string? BillingCountry { get; set; }
        [StringLength(20)]
        public string? BillingPostalCode { get; set; }
        [StringLength(255)]
        public string? ShippingAddressLine1 { get; set; }
        [StringLength(255)]
        public string? ShippingAddressLine2 { get; set; }
        [StringLength(100)]
        public string? ShippingCity { get; set; }
        [StringLength(100)]
        public string? ShippingState { get; set; }
        [StringLength(100)]
        public string? ShippingCountry { get; set; }
        [StringLength(20)]
        public string? ShippingPostalCode { get; set; }
        public bool? IsActive { get; set; }
        public IFormFile? ImageFile { get; set; }
    }
}
