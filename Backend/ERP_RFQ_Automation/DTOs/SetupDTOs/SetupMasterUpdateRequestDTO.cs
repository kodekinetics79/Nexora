using System;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs
{
    public class SetupMasterUpdateRequestDTO
    {
        [Required]
        public string SetupType { get; set; } = null!;

        public string? SetupCode { get; set; }

        [Required(AllowEmptyStrings = true)]
        public string SetupName { get; set; } = null!;

        public string? Description { get; set; }

        public long? ParentSetupId { get; set; }



        public bool? IsActive { get; set; } = true;

        public string? ModifiedBy { get; set; }
    }
}