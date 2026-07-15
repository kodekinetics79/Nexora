using System;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.CurrencyDTOs
{
    public class CurrencyUpdateRequestDTO
    {
        [Required]
        public string Code { get; set; } = null!;

        [Required]
        public string CurrencyName { get; set; } = null!;

        public string? Symbol { get; set; }

        public decimal? ExchangeRate { get; set; }

        public bool? IsBaseCurrency { get; set; }

        [Required]
        public long BusinessUnitID { get; set; }

        public bool? IsActive { get; set; } = true;

        public string? ModifiedBy { get; set; }
    }
}
