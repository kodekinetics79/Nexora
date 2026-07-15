using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class TeamRepository : ITeamRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public TeamRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<Team>, int TotalCount)> GetAllAsync(
            int pageNumber, int pageSize, long? id, string? teamName, long? subTeamId, long businessUnitId)
        {
            var query = _context.Teams
                .AsNoTracking()
                .Include(t => t.SubTeam)
                .Where(t => t.BusinessUnitId == businessUnitId)
                .AsQueryable();

            // Apply filters
            if (id.HasValue)
                query = query.Where(t => t.Id == id.Value);

            if (!string.IsNullOrWhiteSpace(teamName))
                query = query.Where(t => t.TeamName.ToLower().Contains(teamName.ToLower()));

            if (subTeamId.HasValue)
                query = query.Where(t => t.SubTeamId == subTeamId.Value);

            // Total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination
            var teams = await query
                .OrderBy(t => t.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (teams, totalCount);
        }

        public async Task<Team> GetByIdAsync(long id, long businessUnitId)
        {
            var team = await _context.Teams
                .AsNoTracking()
                .Include(t => t.SubTeam)
                .FirstOrDefaultAsync(t => t.Id == id && t.BusinessUnitId == businessUnitId);

            return team ?? throw new KeyNotFoundException($"Team with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Team team)
        {
            // Validate unique team name within same BusinessUnit
            var nameExists = await _context.Teams.AnyAsync(t =>
                t.TeamName == team.TeamName && t.BusinessUnitId == team.BusinessUnitId);
            if (nameExists)
                throw new ArgumentException($"Team name {team.TeamName} already exists in this Business Unit.");

            // Validate BusinessUnit exists
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == team.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {team.BusinessUnitId} does not exist.");

            // Validate SubTeamId exists (if provided) and within same BusinessUnit
            if (team.SubTeamId.HasValue)
            {
                var subTeam = await _context.Teams.FirstOrDefaultAsync(t => t.Id == team.SubTeamId.Value && t.BusinessUnitId == team.BusinessUnitId);
                if (subTeam == null)
                    throw new ArgumentException($"Sub-team with ID {team.SubTeamId.Value} does not exist in Business Unit {team.BusinessUnitId}.");
            }

            // Validate ManagerId exists (if provided)
            if (team.ManagerId.HasValue)
            {
                var managerExists = await _context.Users.AnyAsync(u => u.Id == team.ManagerId.Value);
                if (!managerExists)
                    throw new ArgumentException($"Manager with ID {team.ManagerId.Value} does not exist.");
            }

            _context.Teams.Add(team);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Team team)
        {
            var existing = await _context.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == team.Id);
            if (existing == null)
                throw new KeyNotFoundException($"Team with ID {team.Id} not found.");

            if (existing.BusinessUnitId != team.BusinessUnitId)
                throw new ArgumentException("Cannot change the Business Unit of a team.");

            // Validate unique team name within same BusinessUnit (excluding current team)
            var nameExists = await _context.Teams.AnyAsync(t =>
                t.TeamName == team.TeamName && t.BusinessUnitId == team.BusinessUnitId && t.Id != team.Id);
            if (nameExists)
                throw new ArgumentException($"Team name {team.TeamName} already exists in this Business Unit.");

            // Validate BusinessUnit exists
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == team.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {team.BusinessUnitId} does not exist.");

            // Validate SubTeamId exists (if provided) and within same BusinessUnit, and prevent self-referencing
            if (team.SubTeamId.HasValue)
            {
                if (team.SubTeamId.Value == team.Id)
                    throw new ArgumentException("A team cannot be its own sub-team.");

                var subTeam = await _context.Teams.FirstOrDefaultAsync(t => t.Id == team.SubTeamId.Value && t.BusinessUnitId == team.BusinessUnitId);
                if (subTeam == null)
                    throw new ArgumentException($"Sub-team with ID {team.SubTeamId.Value} does not exist in Business Unit {team.BusinessUnitId}.");
            }

            // Validate ManagerId exists (if provided)
            if (team.ManagerId.HasValue)
            {
                var managerExists = await _context.Users.AnyAsync(u => u.Id == team.ManagerId.Value);
                if (!managerExists)
                    throw new ArgumentException($"Manager with ID {team.ManagerId.Value} does not exist.");
            }

            _context.Teams.Update(team);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var team = await GetByIdAsync(id, businessUnitId);

            // Check for dependent sub-teams
            var hasSubTeams = await _context.Teams.AnyAsync(t => t.SubTeamId == id && t.BusinessUnitId == businessUnitId);
            if (hasSubTeams)
                throw new InvalidOperationException($"Cannot delete Team with ID {id} because it has sub-teams.");

            // Check for dependent users
            var hasUsers = await _context.Users.AnyAsync(u => u.TeamId == id);
            if (hasUsers)
                throw new InvalidOperationException($"Cannot delete Team with ID {id} because it has associated users.");

            _context.Teams.Remove(team);
            await _context.SaveChangesAsync();
        }
    }
}