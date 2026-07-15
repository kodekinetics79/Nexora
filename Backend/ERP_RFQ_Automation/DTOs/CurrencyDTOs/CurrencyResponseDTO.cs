using System;

namespace ERP_RFQ_Automation.DTOs.CurrencyDTOs
{
    public class CurrencyResponseDTO
    {
        public long Id { get; set; }
        public string Code { get; set; } = null!;
        public string CurrencyName { get; set; } = null!;
        public string? Symbol { get; set; }
        public decimal? ExchangeRate { get; set; }
        public bool? IsBaseCurrency { get; set; }
        public long BusinessUnitID { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}
