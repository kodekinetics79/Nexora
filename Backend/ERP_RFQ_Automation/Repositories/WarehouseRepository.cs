using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class WarehouseRepository : IWarehouseRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public WarehouseRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Warehouse>> GetAllAsync(long businessUnitId)
        {
            return await _context.Warehouses
                .AsNoTracking()
                .Where(w => w.BusinessUnitId == businessUnitId)
                .ToListAsync();
        }

        public async Task<Warehouse> GetByIdAsync(long id, long businessUnitId)
        {
            var warehouse = await _context.Warehouses
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == id && w.BusinessUnitId == businessUnitId);

            return warehouse ?? throw new KeyNotFoundException($"Warehouse with ID {id} not found in Business Unit {businessUnitId}.");
        }

        public async Task AddAsync(Warehouse warehouse)
        {
            // Validate unique warehouse code within same BusinessUnit
            var codeExists = await _context.Warehouses.AnyAsync(w =>
                w.WarehouseCode == warehouse.WarehouseCode && w.BusinessUnitId == warehouse.BusinessUnitId);
            if (codeExists)
                throw new ArgumentException($"Warehouse code {warehouse.WarehouseCode} already exists in this Business Unit.");

            // Validate unique warehouse name within same BusinessUnit
            var nameExists = await _context.Warehouses.AnyAsync(w =>
                w.WarehouseName == warehouse.WarehouseName && w.BusinessUnitId == warehouse.BusinessUnitId);
            if (nameExists)
                throw new ArgumentException($"Warehouse name {warehouse.WarehouseName} already exists in this Business Unit.");

            // Validate BusinessUnit exists
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == warehouse.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {warehouse.BusinessUnitId} does not exist.");

            // Validate Capacity (if provided)
            if (warehouse.Capacity.HasValue && warehouse.Capacity < 0)
                throw new ArgumentException("Capacity cannot be negative.");

            // Validate ContactEmail format (if provided)
            if (!string.IsNullOrWhiteSpace(warehouse.ContactEmail))
            {
                var emailValidator = new System.ComponentModel.DataAnnotations.EmailAddressAttribute();
                if (!emailValidator.IsValid(warehouse.ContactEmail))
                    throw new ArgumentException("Invalid contact email format.");
            }

            // Validate ContactPhone format (if provided)
            if (!string.IsNullOrWhiteSpace(warehouse.ContactPhone))
            {
                var phoneValidator = new System.ComponentModel.DataAnnotations.PhoneAttribute();
                if (!phoneValidator.IsValid(warehouse.ContactPhone))
                    throw new ArgumentException("Invalid contact phone format.");
            }

            _context.Warehouses.Add(warehouse);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Warehouse warehouse)
        {
            var existing = await _context.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == warehouse.Id);
            if (existing == null)
                throw new KeyNotFoundException($"Warehouse with ID {warehouse.Id} not found.");

            if (existing.BusinessUnitId != warehouse.BusinessUnitId)
                throw new ArgumentException("Cannot change the Business Unit of a warehouse.");

            // Validate unique warehouse code within same BusinessUnit (excluding current warehouse)
            var codeExists = await _context.Warehouses.AnyAsync(w =>
                w.WarehouseCode == warehouse.WarehouseCode && w.BusinessUnitId == warehouse.BusinessUnitId && w.Id != warehouse.Id);
            if (codeExists)
                throw new ArgumentException($"Warehouse code {warehouse.WarehouseCode} already exists in this Business Unit.");

            // Validate unique warehouse name within same BusinessUnit (excluding current warehouse)
            var nameExists = await _context.Warehouses.AnyAsync(w =>
                w.WarehouseName == warehouse.WarehouseName && w.BusinessUnitId == warehouse.BusinessUnitId && w.Id != warehouse.Id);
            if (nameExists)
                throw new ArgumentException($"Warehouse name {warehouse.WarehouseName} already exists in this Business Unit.");

            // Validate BusinessUnit exists
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == warehouse.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit with ID {warehouse.BusinessUnitId} does not exist.");

            // Validate Capacity (if provided)
            if (warehouse.Capacity.HasValue && warehouse.Capacity < 0)
                throw new ArgumentException("Capacity cannot be negative.");

            // Validate ContactEmail format (if provided)
            if (!string.IsNullOrWhiteSpace(warehouse.ContactEmail))
            {
                var emailValidator = new System.ComponentModel.DataAnnotations.EmailAddressAttribute();
                if (!emailValidator.IsValid(warehouse.ContactEmail))
                    throw new ArgumentException("Invalid contact email format.");
            }

            // Validate ContactPhone format (if provided)
            if (!string.IsNullOrWhiteSpace(warehouse.ContactPhone))
            {
                var phoneValidator = new System.ComponentModel.DataAnnotations.PhoneAttribute();
                if (!phoneValidator.IsValid(warehouse.ContactPhone))
                    throw new ArgumentException("Invalid contact phone format.");
            }

            _context.Warehouses.Update(warehouse);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var warehouse = await GetByIdAsync(id, businessUnitId);

            _context.Warehouses.Remove(warehouse);
            await _context.SaveChangesAsync();
        }
    }
}