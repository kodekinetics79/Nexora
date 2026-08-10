using System;

namespace ERP_RFQ_Automation.DTOs.QuoteConfigurationDTOs
{
    public class QuoteConfigurationResponseDTO
    {
        public long Id { get; set; }
        public long BusinessUnitId { get; set; }
        public string? Logo { get; set; }
        public string? PrimaryColor { get; set; }
        public string? TermsAndConditions { get; set; }
        public string? CompanyAddress { get; set; }
        public string? CompanyPhone { get; set; }
        public string? CompanyEmail { get; set; }
        public string? FooterText { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }

    public class QuoteConfigurationCreateRequestDTO
    {
        public long BusinessUnitId { get; set; }
        public string? Logo { get; set; }
        public string? PrimaryColor { get; set; }
        public string? TermsAndConditions { get; set; }
        public string? CompanyAddress { get; set; }
        public string? CompanyPhone { get; set; }
        public string? CompanyEmail { get; set; }
        public string? FooterText { get; set; }
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }

    public class QuoteConfigurationUpdateRequestDTO
    {
        public string? Logo { get; set; }
        public string? PrimaryColor { get; set; }
        public string? TermsAndConditions { get; set; }
        public string? CompanyAddress { get; set; }
        public string? CompanyPhone { get; set; }
        public string? CompanyEmail { get; set; }
        public string? FooterText { get; set; }
        public string? ModifiedBy { get; set; }
    }
}
