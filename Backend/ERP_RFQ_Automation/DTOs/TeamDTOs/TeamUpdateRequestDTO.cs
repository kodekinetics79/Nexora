using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.TeamDTOs
{
    public class TeamUpdateRequestDTO
    {
        [Required]
        public string TeamName { get; set; } = null!;

        public long? SubTeamId { get; set; }

        public long? ManagerId { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public string? ModifiedBy { get; set; }
    }
}