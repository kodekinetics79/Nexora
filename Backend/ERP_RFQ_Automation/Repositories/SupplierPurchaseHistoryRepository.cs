using ERP_RFQ_Automation.DTOs.SupplierPurchaseHistory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    /// <summary>
    /// Supplier purchase history. Reads are tenant-scoped through the owning Product.
    ///
    /// Every mutation runs inside a single serializable transaction so the purchase-history
    /// rows and the landed cost they record are committed or discarded together.
    ///
    /// These mutations move NO stock. Purchase history is a commercial record; physical balances
    /// belong to <c>Inventory.QtyOnHand</c>, which only <c>StockLedgerService</c> and the governed
    /// goods-receipt path may write, and every such write posts a balancing InventoryMovement.
    /// The controller already answers 410 Gone on all mutating actions.
    ///
    /// PO document numbers come from the database sequence
    /// <c>public.nexora_supplier_po_doc_seq</c>, mirroring the RFQ-number authority in
    /// <see cref="RfqRepository"/>. The previous implementation read MAX(PoDocId), parsed it and
    /// incremented in application memory with no transaction and no lock, so two concurrent
    /// callers observed the same maximum and issued the same PO number.
    /// </summary>
    public class SupplierPurchaseHistoryRepository : ISupplierPurchaseHistoryRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public SupplierPurchaseHistoryRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<SupplierPurchaseHistoryResponseDTO>> GetAllAsync(long businessUnitId)
        {
            return await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Include(h => h.Supplier)
                .Where(h => h.Product.Buid == businessUnitId)
                .Select(h => MapToDTO(h))
                .ToListAsync();
        }

        public async Task<IEnumerable<SupplierPurchaseHistoryResponseDTO>> GetByProductIdAsync(long productId, long businessUnitId)
        {
            return await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Include(h => h.Supplier)
                .Where(h => h.ProductId == productId && h.Product.Buid == businessUnitId)
                .Select(h => MapToDTO(h))
                .ToListAsync();
        }

        public async Task<SupplierPurchaseHistoryResponseDTO?> GetByIdAsync(long id, long businessUnitId)
        {
            var history = await _context.SupplierPurchaseHistories
                .Include(h => h.Product)
                .Include(h => h.Supplier)
                .FirstOrDefaultAsync(h => h.Id == id && h.Product.Buid == businessUnitId);

            return history != null ? MapToDTO(history) : null;
        }

        public Task AddAsync(SupplierPurchaseHistory history)
            => AddBatchAsync(new[] { history ?? throw new ArgumentNullException(nameof(history)) });

        /// <summary>
        /// Records one or more purchase-history rows under a single server-issued PO number.
        ///
        /// <para><b>This path no longer moves stock.</b> It used to increment the legacy
        /// <c>Product</c> stock column directly,
        /// which is a second, independent stock ledger: it posts no <see cref="Commercial.InventoryMovement"/>,
        /// is not per-warehouse, and is invisible to available-to-promise, so a receipt booked here
        /// silently disagreed with every availability screen and with
        /// <c>GET /api/inventory-intelligence/stock/reconciliation</c>. Physical stock arrives
        /// through the governed goods-receipt path (<c>ProcurementApplicationService.PostGoodsReceiptAsync</c>)
        /// or the stock ledger (<c>IStockLedgerService</c>) and nowhere else. Purchase history is
        /// commercial history — what was bought, from whom, at what price — not a stock event.</para>
        /// </summary>
        public async Task<string> AddBatchAsync(IEnumerable<SupplierPurchaseHistory> histories)
        {
            var rows = (histories ?? throw new ArgumentNullException(nameof(histories))).ToList();
            if (rows.Count == 0)
                throw new ArgumentException("At least one purchase history row is required.", nameof(histories));
            if (rows.Any(row => row.Quantity <= 0))
                throw new ArgumentException("Purchase history quantity must be positive.", nameof(histories));
            if (rows.Any(row => row.UnitPrice < 0))
                throw new ArgumentException("Purchase history unit price cannot be negative.", nameof(histories));

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                // Resolve the owning tenant from persisted Products, never from caller input, then
                // require every row — product and supplier alike — to belong to that one tenant.
                var productIds = rows.Select(row => row.ProductId).Distinct().ToList();
                var products = await _context.Products
                    .Where(product => productIds.Contains(product.Id))
                    .ToDictionaryAsync(product => product.Id);
                if (products.Count != productIds.Count)
                    throw new ArgumentException("Every purchase history row must reference an existing Product.", nameof(histories));

                var businessUnitIds = products.Values.Select(product => product.Buid).Distinct().ToList();
                if (businessUnitIds.Count != 1)
                    throw new ArgumentException("A purchase history batch cannot span business units.", nameof(histories));
                var businessUnitId = businessUnitIds[0];

                var supplierIds = rows.Select(row => row.SupplierId).Distinct().ToList();
                var tenantSupplierCount = await _context.Suppliers
                    .CountAsync(supplier => supplierIds.Contains(supplier.Id) && supplier.Buid == businessUnitId);
                if (tenantSupplierCount != supplierIds.Count)
                    throw new ArgumentException("Every purchase history Supplier must belong to the Product's business unit.", nameof(histories));

                var poDocId = await AllocatePoDocIdAsync();
                foreach (var row in rows)
                {
                    row.PoDocId = poDocId;
                    _context.SupplierPurchaseHistories.Add(row);
                }

                // Fold the batch per product so a product appearing on several rows records its
                // latest landed cost once. The stock increment that used to live here has been
                // removed: see the method summary — Product.QtyOnHand is a legacy column that the
                // availability engine cannot see, and writing it here made the two stock figures
                // diverge with no movement to reconcile against.
                foreach (var movement in rows.GroupBy(row => row.ProductId))
                {
                    var product = products[movement.Key];
                    product.FinalLandedCost = movement.OrderBy(row => row.PurchaseDate)
                        .ThenBy(row => row.UnitPrice).Last().UnitPrice;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                return poDocId;
            });
        }

        /// <summary>
        /// Issues the next PO document number from the database sequence, which is the only
        /// concurrency-safe source: <c>nextval</c> is atomic and never returns a value twice.
        /// </summary>
        private async Task<string> AllocatePoDocIdAsync()
        {
            if (_context.Database.IsNpgsql())
            {
                var sequence = await _context.Database.SqlQueryRaw<long>(
                    "SELECT nextval('public.nexora_supplier_po_doc_seq') AS \"Value\"").SingleAsync();
                return $"PO{sequence:D8}";
            }

            // Providers without sequences (the SQLite test lane) fall back to a high-water read.
            // Safe only because every caller already holds the serializable transaction opened
            // above, which is what the original code was missing.
            var issued = await _context.SupplierPurchaseHistories
                .Where(history => history.PoDocId != null && history.PoDocId.StartsWith("PO"))
                .Select(history => history.PoDocId!)
                .ToListAsync();
            var highWater = issued
                .Select(value => long.TryParse(value.AsSpan(2), out var parsed) ? parsed : 0L)
                .DefaultIfEmpty(0L)
                .Max();
            return $"PO{highWater + 1:D8}";
        }

        public async Task UpdateAsync(SupplierPurchaseHistory history)
        {
            _context.SupplierPurchaseHistories.Update(history);
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var history = await _context.SupplierPurchaseHistories
                    .Include(h => h.Product)
                    .FirstOrDefaultAsync(h => h.Id == id && h.Product.Buid == businessUnitId);
                if (history is null)
                {
                    await transaction.CommitAsync();
                    return;
                }

                _context.SupplierPurchaseHistories.Remove(history);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            });
        }

        public async Task DeleteByPoDocIdAsync(string poDocId, long businessUnitId)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var items = await _context.SupplierPurchaseHistories
                    .Include(h => h.Product)
                    .Where(h => h.PoDocId == poDocId && h.Product.Buid == businessUnitId)
                    .ToListAsync();
                if (items.Count == 0)
                {
                    await transaction.CommitAsync();
                    return;
                }

                _context.SupplierPurchaseHistories.RemoveRange(items);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            });
        }

        private static SupplierPurchaseHistoryResponseDTO MapToDTO(SupplierPurchaseHistory history)
        {
            return new SupplierPurchaseHistoryResponseDTO
            {
                Id = history.Id,
                ProductId = history.ProductId,
                ProductName = history.Product?.ProductName,
                PartNo = history.Product?.PartNo,
                SupplierId = history.SupplierId,
                SupplierName = history.Supplier?.Name,
                PurchaseDate = history.PurchaseDate,
                Quantity = history.Quantity,
                UnitPrice = history.UnitPrice,
                Currency = history.Currency,
                BatchNo = history.BatchNo,
                ExpiryDate = history.ExpiryDate,
                PoDocId = history.PoDocId,
                CreatedBy = history.CreatedBy,
                CreatedOn = history.CreatedOn
            };
        }
    }
}
