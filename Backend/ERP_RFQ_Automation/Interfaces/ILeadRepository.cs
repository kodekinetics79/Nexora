
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
        Task<(long RfqId, string Rfqno)> ConvertLeadToRfqAsync(long id, long businessUnitId, string createdBy);
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

        // WP-A3: resolve a duplicate flag ("not_duplicate" | "confirm"); returns the
        // refreshed lead detail, or null when the lead does not exist in the BU.
        Task<LeadResponseDTO?> ResolveDuplicateAsync(long id, long businessUnitId, string action, string resolvedBy);

        Task<LeadResponseDTO?> GetLeadByIdAsync(long id, long businessUnitId);

        Task<LeadStatsDTO> GetLeadStatsAsync(long businessUnitId);

        // Extraction review workbench
        Task<(IEnumerable<LeadNeedsReviewItemDTO>, int TotalCount)> GetNeedsReviewLeadsAsync(int pageNumber, int pageSize, long businessUnitId, string? search = null);
        Task<LeadResponseDTO?> SubmitLeadReviewAsync(
            long id, long businessUnitId, LeadReviewSubmitDTO review, string reviewedBy = "system");
    }
}
