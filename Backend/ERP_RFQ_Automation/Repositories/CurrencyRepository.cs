using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace ERP_RFQ_Automation.Repositories
{
    public class CurrencyRepository : ICurrencyRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public CurrencyRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Currency>> GetAllAsync(long businessUnitId)
        {
            return await _context.Currencies
                .Where(c => c.BusinessUnitId == businessUnitId)
                .ToListAsync();
        }

        public async Task<Currency> GetByIdAsync(long id, long businessUnitId)
        {
            var currency = await _context.Currencies
                .FirstOrDefaultAsync(c => c.Id == id && c.BusinessUnitId == businessUnitId);
            return currency ?? throw new KeyNotFoundException($"Currency with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Currency currency)
        {
            // Check duplicate code per BusinessUnit
            var codeExists = await _context.Currencies.AnyAsync(c =>
                c.Code == currency.Code && c.BusinessUnitId == currency.BusinessUnitId);
            if (codeExists)
                throw new ArgumentException($"Currency code {currency.Code} already exists for this Business Unit.");

            // Ensure one base currency per Business Unit
            if (currency.IsBaseCurrency == true)
            {
                var existingBase = await _context.Currencies
                    .AnyAsync(c => c.IsBaseCurrency == true && c.BusinessUnitId == currency.BusinessUnitId);
                if (existingBase)
                    throw new ArgumentException("Another base currency already exists for this Business Unit.");
            }

            _context.Currencies.Add(currency);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Currency currency)
        {
            var existing = await _context.Currencies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == currency.Id);
            if (existing == null)
                throw new KeyNotFoundException($"Currency with ID {currency.Id} not found.");

            if (existing.BusinessUnitId != currency.BusinessUnitId)
                throw new ArgumentException("Cannot change the Business Unit of a currency.");

            var codeExists = await _context.Currencies.AnyAsync(c =>
                c.Code == currency.Code &&
                c.BusinessUnitId == currency.BusinessUnitId &&
                c.Id != currency.Id);
            if (codeExists)
                throw new ArgumentException($"Currency code {currency.Code} already exists for this Business Unit.");

            if (currency.IsBaseCurrency == true)
            {
                var existingBase = await _context.Currencies
                    .AnyAsync(c => c.IsBaseCurrency == true && c.BusinessUnitId == currency.BusinessUnitId && c.Id != currency.Id);
                if (existingBase)
                    throw new ArgumentException("Another base currency already exists for this Business Unit.");
            }

            _context.Currencies.Update(currency);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var currency = await GetByIdAsync(id, businessUnitId);
            _context.Currencies.Remove(currency);
            await _context.SaveChangesAsync();
        }
    }
}