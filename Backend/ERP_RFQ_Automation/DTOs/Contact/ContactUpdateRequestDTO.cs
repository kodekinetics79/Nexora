using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Contact
{
    public class ContactUpdateRequestDTO : IValidatableObject
    {
        public long? CustomerId { get; set; }
        public long? SupplierId { get; set; }
        [Required, StringLength(100), RegularExpression(@".*\S.*", ErrorMessage = "First name cannot be blank.")]
        public string FirstName { get; set; } = null!;
        [StringLength(100)]
        public string? MiddleName { get; set; }
        [Required, StringLength(100), RegularExpression(@".*\S.*", ErrorMessage = "Last name cannot be blank.")]
        public string LastName { get; set; } = null!;
        [EmailAddress, StringLength(320)]
        public string? Email { get; set; }
        [StringLength(50)]
        public string? PhoneNo { get; set; }
        [StringLength(50)]
        public string? MobileNo { get; set; }
        [StringLength(100)]
        public string? Position { get; set; }
        public bool? IsPrimary { get; set; }
        [Required]
        public Guid? ConcurrencyToken { get; set; }
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (CustomerId.HasValue == SupplierId.HasValue)
                yield return new ValidationResult(
                    "Exactly one customer or supplier is required.",
                    [nameof(CustomerId), nameof(SupplierId)]);
        }
    }
}
