using System;

namespace ERP_RFQ_Automation.DTOs.TeamDTOs
{
    public class TeamResponseDTO
    {
        public long Id { get; set; }
        public string TeamName { get; set; } = null!;
        public long? SubTeamId { get; set; }
        public string? SubTeamName { get; set; }
        public long? ManagerId { get; set; }
        public long BusinessUnitId { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}