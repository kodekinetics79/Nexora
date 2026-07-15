using ERP_RFQ_Automation.DTOs.LocationDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class StateRepository : IStateRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public StateRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<StateResponseDTO>> GetAllAsync(long buid)
        {
            return await _context.SetStates
                .AsNoTracking()
                .Include(s => s.Country)
                .Where(s => s.Buid == buid)
                .Select(s => new StateResponseDTO
                {
                    StateId = s.StateId,
                    StateCode = s.StateCode,
                    StateName = s.StateName,
                    CountryId = s.CountryId,
                    CountryName = s.Country.CountryName,
                    Buid = s.Buid,
                    Description = s.Description,
                    IsActive = s.IsActive,
                    CreatedBy = s.CreatedBy,
                    CreatedDate = s.CreatedDate,
                    ModifiedBy = s.ModifiedBy,
                    ModifiedDate = s.ModifiedDate
                })
                .ToListAsync();
        }

        public async Task<StateResponseDTO?> GetByIdAsync(int id)
        {
            var s = await _context.SetStates
                .AsNoTracking()
                .Include(s => s.Country)
                .FirstOrDefaultAsync(x => x.StateId == id);

            if (s == null) return null;

            return new StateResponseDTO
            {
                StateId = s.StateId,
                StateCode = s.StateCode,
                StateName = s.StateName,
                CountryId = s.CountryId,
                CountryName = s.Country.CountryName,
                Buid = s.Buid,
                Description = s.Description,
                IsActive = s.IsActive,
                CreatedBy = s.CreatedBy,
                CreatedDate = s.CreatedDate,
                ModifiedBy = s.ModifiedBy,
                ModifiedDate = s.ModifiedDate
            };
        }

        public async Task<StateResponseDTO> CreateAsync(StateCreateDTO dto, string userId)
        {
            var state = new SetState
            {
                StateCode = dto.StateCode,
                StateName = dto.StateName,
                CountryId = dto.CountryId,
                Buid = dto.Buid,
                Description = dto.Description,
                IsActive = dto.IsActive,
                CreatedBy = userId,
                CreatedDate = DateTime.UtcNow
            };

            _context.SetStates.Add(state);
            await _context.SaveChangesAsync();

            return (await GetByIdAsync(state.StateId))!;
        }

        public async Task<StateResponseDTO> UpdateAsync(int id, StateUpdateDTO dto, string userId)
        {
            var state = await _context.SetStates.FindAsync(id);
            if (state == null) throw new Exception("State not found");

            state.StateCode = dto.StateCode;
            state.StateName = dto.StateName;
            state.CountryId = dto.CountryId;
            state.Buid = dto.Buid;
            state.Description = dto.Description;
            state.IsActive = dto.IsActive;
            state.ModifiedBy = userId;
            state.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return (await GetByIdAsync(id))!;
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var state = await _context.SetStates.FindAsync(id);
            if (state == null) return false;

            _context.SetStates.Remove(state);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
