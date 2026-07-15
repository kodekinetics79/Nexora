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
        public string? CreatedBy { get; set; }
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
