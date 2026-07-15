using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class QuoteConfigurationRepository : IQuoteConfigurationRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public QuoteConfigurationRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId)
        {
            return await _context.QuoteConfigurations
                .FirstOrDefaultAsync(q => q.BusinessUnitId == businessUnitId);
        }

        public async Task AddAsync(QuoteConfiguration config)
        {
            _context.QuoteConfigurations.Add(config);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(QuoteConfiguration config)
        {
            _context.QuoteConfigurations.Update(config);
            await _context.SaveChangesAsync();
        }
    }
}
