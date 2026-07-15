
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.LeadDTOs;
namespace ERP_RFQ_Automation.Interfaces
{
    public interface ILeadRepository
    {
        Task<(IEnumerable<LeadResponseDTO>, int TotalCount)> GetLeadListAsync(int pageNumber, int pageSize, long? id, string? rfqno, string? buyersName, string? leadSource, long businessUnitId, DateTime? startDate = null, DateTime? endDate = null, string? emailSource = null, string? clientemail = null);
        Task<IEnumerable<EmailConfigurationDropdownDTO>> GetActiveEmailConfigurationsAsync(long businessUnitId);

        Task<IEnumerable<RejectionReasonDTO>> GetLeadRejectionReasonsAsync();
        Task AcceptLeadAsync(long id, long businessUnitId);
        Task RejectLeadAsync(long id, long reasonId, long businessUnitId);

        //Accepted Lead Section 


        Task<(IEnumerable<AcceptedLeadResponseDTO>, int TotalCount)> GetAcceptedLeadsAsync(
         int pageNumber,
         int pageSize,
         long businessUnitId,
         long? assignedToId = null,
         string? searchTerm = null,
         DateTime? startDate = null,
         DateTime? endDate = null,
         bool excludeAssigned = false,
         bool onlyAssigned = false);

        Task<IEnumerable<UserDropdownDTO>> GetUsersForAssignmentAsync(long businessUnitId);
        Task<AcceptedLeadResponseDTO?> GetAcceptedLeadByIdAsync(long id, long businessUnitId);

        // Updated method signature
        Task AssignLeadAsync(long leadId, long assignedToUserId, long businessUnitId, string? comment = null);

        Task<LeadResponseDTO?> GetLeadByIdAsync(long id, long businessUnitId);

        Task<LeadStatsDTO> GetLeadStatsAsync(long businessUnitId);
    }
}