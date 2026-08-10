using System;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.CurrencyDTOs
{
    public class CurrencyCreateRequestDTO
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
        // Sec-A1: the actor field is GONE, not merely ignored. Leaving `CreatedBy` on the
        // request contract invites the next writer of this endpoint to read it, which is how
        // the forgery got here. Attribution is derived from the validated token by
        // ActorContext.From(User).Stamp and cannot be influenced by a request body.
    }
}
