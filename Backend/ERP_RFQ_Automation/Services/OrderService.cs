using ERP_RFQ_Automation.DTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Authorization;

namespace ERP_RFQ_Automation.Services
{
    public class OrderService : IOrderService
    {
        private readonly IOrderRepository _orderRepository;
        private readonly ErpRfqAutomationContext _context;
        private readonly ILifecycleApplicationService? _lifecycle;
        private readonly ERP_RFQ_Automation.Inventory.IOrderStockReservationService _stock;

        public OrderService(IOrderRepository orderRepository, ErpRfqAutomationContext context,
            ILifecycleApplicationService? lifecycle = null,
            ERP_RFQ_Automation.Inventory.IOrderStockReservationService? stock = null)
        {
            _orderRepository = orderRepository;
            _context = context;
            _lifecycle = lifecycle;
            // Defaulted rather than required so the existing call sites keep compiling, but never
            // null: a null here would silently reinstate the stock leak this service exists to
            // close, and the service depends on nothing but the same DbContext.
            // The lot declarer is composed from the same context for the same reason. It is the
            // REAL adapter, not the null one: an order confirmed through this path issues stock
            // through ConsumeOrderLinesAsync, and a no-op declarer here would leave every such
            // issue undeclared in where-used trace with nothing on any screen to say so.
            _stock = stock ?? new ERP_RFQ_Automation.Inventory.OrderStockReservationService(
                context, new ERP_RFQ_Automation.Inventory.InventoryAvailabilityService(context),
                new ERP_RFQ_Automation.Traceability.MaterialLotFulfilmentDeclarer(context,
                    new ERP_RFQ_Automation.Traceability.MaterialTraceabilityService(context,
                        new ERP_RFQ_Automation.Inventory.StockLedgerService(context),
                        new ERP_RFQ_Automation.Inventory.InventoryAvailabilityService(context))));
        }

        public async Task<IEnumerable<OrderDto>> GetAllOrdersAsync(long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var orders = await _orderRepository.GetAllOrdersAsync(businessUnitId, accessScope);
            return orders.Select(MapToDto).ToList();
        }

        public async Task<OrderDto?> GetOrderByIdAsync(long id, long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var order = await _orderRepository.GetOrderByIdAsync(id, businessUnitId, accessScope);
            return order == null ? null : MapToDto(order);
        }

        public async Task<OrderDto> CreateManualOrderAsync(CreateOrderDto dto, long businessUnitId)
        {
            if (dto.QuoteId.HasValue)
                throw new InvalidOperationException(
                    "Quote-linked orders require a confirmed customer award. Capture the customer PO and award from the quote.");
            if (dto.Items == null || dto.Items.Count == 0)
                throw new ArgumentException("An order must contain at least one line item.");

            // FR-COM-07. The originating document is resolved BEFORE any money is computed, so a
            // request that cannot name its case is rejected outright rather than half-built.
            //
            // There is deliberately no "allocate a new case here" branch. A commercial case is a
            // one-to-one principal of a Lead (UX_Leads_CommercialCaseID), so minting one for a
            // counter sale would have to manufacture a phantom lead, and Phase 1 (BRD v3.0 §2)
            // starts the spine at an inquiry — there is no walk-in/counter-sale requirement to
            // serve. An order with no preceding inquiry is refused, not silently unlinked.
            var origin = await ResolveOrderOriginAsync(dto, businessUnitId);

            // FIN-12: reject non-positive quantity/price and negative tax/discount before any math.
            foreach (var it in dto.Items)
            {
                if (it.Quantity <= 0)
                    throw new ArgumentException($"Invalid line quantity ({it.Quantity}). Quantity must be greater than zero.");
                if (it.UnitPrice <= 0)
                    throw new ArgumentException($"Invalid unit price ({it.UnitPrice}). Unit price must be greater than zero.");
                if (it.Discount < 0)
                    throw new ArgumentException($"Invalid discount ({it.Discount}). Discount cannot be negative.");
                if (it.TaxAmount < 0)
                    throw new ArgumentException($"Invalid tax ({it.TaxAmount}). Tax cannot be negative.");
            }

            // FIN-02 / FIN-01 / FIN-09: recompute ALL money server-side. Client-supplied amount
            // fields (line TotalAmount and any header SubTotal/Tax/Discount/Total) are treated as
            // NON-AUTHORITATIVE and are never persisted as sent.
            var computedItems = new List<OrderItem>();
            decimal subTotal = 0m;
            decimal totalDiscount = 0m;
            decimal totalTax = 0m;

            // R17: one policy read for the whole order, so every line is taxed on one answer.
            var outputTaxRatePercent = await _context.ResolveOutputTaxRatePercentAsync(businessUnitId);
            if (outputTaxRatePercent is null)
                throw new InvalidOperationException(
                    "This business unit has no output tax rate configured, so an order's tax cannot be derived. " +
                    "Set the output tax rate in Commercial Policy settings before creating orders.");

            foreach (var itemDto in dto.Items)
            {
                // FIN-09: round each line to currency scale before summing.
                decimal lineGross = RoundCurrency(itemDto.Quantity * itemDto.UnitPrice);

                // Order line items carry only a flat discount amount (the DTO has no discount
                // type/rate). Sanitize it: never negative, never more than the gross line value.
                decimal lineDiscount = RoundCurrency(itemDto.Discount);
                if (lineDiscount > lineGross) lineDiscount = lineGross;

                // FIN-01 / R17: tax is DERIVED server-side from the tenant's output tax rate (see
                // ResolveLineTaxAmount). itemDto.TaxAmount is ignored — accepting it is what let an
                // order be booked with no tax at all. The null case is impossible here because the
                // missing-rate guard above already refused the request.
                decimal lineTax = ResolveLineTaxAmount(lineGross - lineDiscount, outputTaxRatePercent) ?? 0m;

                decimal lineTotal = RoundCurrency(lineGross - lineDiscount + lineTax);

                subTotal += lineGross;
                totalDiscount += lineDiscount;
                totalTax += lineTax;

                computedItems.Add(new OrderItem
                {
                    ProductId = itemDto.ProductId,
                    Description = itemDto.Description,
                    Quantity = itemDto.Quantity,
                    UnitPrice = itemDto.UnitPrice,
                    Discount = lineDiscount,
                    TaxAmount = lineTax,
                    TotalAmount = lineTotal,
                    UomId = itemDto.UomId,
                    WarehouseId = itemDto.WarehouseId,
                    CreatedBy = "System",
                    CreatedDate = DateTime.Now,
                    IsActive = true
                });
            }

            subTotal = RoundCurrency(subTotal);
            totalDiscount = RoundCurrency(totalDiscount);
            totalTax = RoundCurrency(totalTax);
            decimal totalAmount = RoundCurrency(subTotal - totalDiscount + totalTax);

            // Get Status IDs from SetupMaster
            var draftStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "OrderStatus" && s.SetupCode == "DRAFT");

            // Fallback: pick any available OrderStatus if DRAFT is not configured
            if (draftStatus == null)
            {
                draftStatus = await _context.SetupMasters
                    .FirstOrDefaultAsync(s => s.SetupType == "OrderStatus");
            }

            if (draftStatus == null)
                throw new Exception("No OrderStatus setup found in the system. Please configure OrderStatus in SetupMaster.");

            var unpaidStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "PaymentStatus" && s.SetupCode == "UNPAID");

            var orderNo = await _orderRepository.GetNextOrderNumberAsync(businessUnitId);

            var order = new Order
            {
                OrderNo = orderNo,
                QuoteId = dto.QuoteId,
                LeadId = dto.LeadId,
                Rfqid = dto.RfqId,
                CustomerId = dto.CustomerId,
                BusinessUnitId = businessUnitId, // Prioritize the parameter
                CurrencyId = dto.CurrencyId,
                StatusId = draftStatus.SetupId,
                PaymentMethodId = dto.PaymentMethodId,
                PaymentStatusId = dto.PaymentStatusId ?? unpaidStatus?.SetupId,
                PaymentDate = dto.PaymentDate,
                PaidAmount = dto.PaidAmount,
                PaymentReference = dto.PaymentReference,
                OrderDate = dto.OrderDate,
                DeliveryDate = dto.DeliveryDate,
                TotalAmount = totalAmount,
                SubTotal = subTotal,
                TaxAmount = totalTax,
                DiscountAmount = totalDiscount,
                TermsAndConditions = dto.TermsAndConditions,
                Notes = dto.Notes,
                CreatedBy = "System", // Replace with actual user context
                CreatedOn = DateTime.Now,
                IsActive = true
            };
            origin.ApplyTo(order);

            // Attach the server-computed line items (see recompute above).
            foreach (var computedItem in computedItems)
            {
                order.OrderItems.Add(computedItem);
            }

            var createdOrder = await _orderRepository.CreateOrderAsync(order);

            return MapToDto(createdOrder);
        }

        /// <summary>
        /// The document a manually raised order inherits its commercial case from.
        ///
        /// <para>Resolution is strongest-link-first — RFQ, then lead — and every candidate is
        /// re-read inside this tenant, so a client cannot name another tenant's document to borrow
        /// its case. A request naming no originating document, or naming one that itself has no
        /// case, is refused: those are the two ways an order used to escape the spine.</para>
        /// </summary>
        private async Task<OrderOrigin> ResolveOrderOriginAsync(CreateOrderDto dto, long businessUnitId)
        {
            if (dto.RfqId is > 0)
            {
                var rfq = await _context.Rfqs
                    .FirstOrDefaultAsync(r => r.Id == dto.RfqId.Value && r.BusinessUnitId == businessUnitId)
                    ?? throw new InvalidOperationException(
                        $"RFQ {dto.RfqId.Value} was not found in this business unit.");
                if (!rfq.CommercialCaseId.HasValue)
                    throw new InvalidOperationException(
                        $"RFQ {rfq.Rfqno} carries no commercial case, so an order cannot inherit one from it.");
                return new OrderOrigin(rfq, null);
            }

            if (dto.LeadId is > 0)
            {
                var lead = await _context.Leads
                    .FirstOrDefaultAsync(l => l.Id == dto.LeadId.Value && l.BusinessUnitId == businessUnitId)
                    ?? throw new InvalidOperationException(
                        $"Lead {dto.LeadId.Value} was not found in this business unit.");
                if (lead.CommercialCaseId <= 0)
                    throw new InvalidOperationException(
                        $"Lead {lead.Rfqno} carries no commercial case, so an order cannot inherit one from it.");
                return new OrderOrigin(null, lead);
            }

            throw new InvalidOperationException(
                "An order must originate from an inquiry. Supply the RFQ or lead it fulfils — " +
                "a sales order cannot be created outside a commercial case.");
        }

        private sealed record OrderOrigin(Rfq? Rfq, Lead? Lead)
        {
            public void ApplyTo(Order order)
            {
                if (Rfq is not null) order.InheritCommercialIdentity(Rfq);
                else if (Lead is not null) order.InheritCommercialIdentity(Lead);
                else throw new InvalidOperationException("An order must originate from an inquiry.");

                if (!order.HasCommercialIdentity)
                    throw new InvalidOperationException(
                        "The originating document did not yield a commercial case for this order.");
            }
        }

        public async Task<OrderDto> CreateOrderFromRfqAsync(long rfqId, long businessUnitId)
        {
            var rfq = await _context.Rfqs
                .Include(r => r.Rfqitems)
                .FirstOrDefaultAsync(r => r.Id == rfqId && r.BusinessUnitId == businessUnitId);

            if (rfq == null) throw new Exception("RFQ not found");
            if (rfq.Rfqitems.Any(item => item.Quantity is null or <= 0))
                throw new InvalidOperationException(
                    "This RFQ still needs quantity clarification and cannot create an order.");
            if (!rfq.CommercialCaseId.HasValue)
                throw new InvalidOperationException(
                    $"RFQ {rfq.Rfqno} carries no commercial case, so an order cannot inherit one from it.");

            // Fetch Default Statuses
            var draftStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "OrderStatus" && s.SetupCode == "DRAFT");

            // Fallback: pick any available OrderStatus if DRAFT is not configured
            if (draftStatus == null)
            {
                draftStatus = await _context.SetupMasters
                    .FirstOrDefaultAsync(s => s.SetupType == "OrderStatus");
            }

            if (draftStatus == null)
                throw new Exception("No OrderStatus setup found. Please configure OrderStatus in SetupMaster.");

            var unpaidStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "PaymentStatus" && s.SetupCode == "UNPAID");

            var orderNo = await _orderRepository.GetNextOrderNumberAsync(businessUnitId);

            // Create Order Header
            var order = new Order
            {
                OrderNo = orderNo,
                Rfqid = rfq.Id,
                LeadId = rfq.LeadId,
                CustomerId = rfq.CustomerId ?? 0,
                BusinessUnitId = businessUnitId,
                StatusId = draftStatus.SetupId,
                PaymentStatusId = unpaidStatus?.SetupId,
                OrderDate = DateTime.Now,
                TotalAmount = 0, // Will correspond to Quote if available, else 0
                SubTotal = 0,
                PaidAmount = 0,
                CreatedBy = "System",
                CreatedOn = DateTime.Now,
                IsActive = true
            };
            order.InheritCommercialIdentity(rfq);

            // Map RFQ Items to Order Items
            foreach (var rfqItem in rfq.Rfqitems)
            {
                order.OrderItems.Add(new OrderItem
                {
                    ProductId = rfqItem.ProductId ?? 0,
                    Description = rfqItem.ItemText,
                    Quantity = rfqItem.Quantity!.Value,
                    UnitPrice = rfqItem.UnitPrice ?? 0,
                    TotalAmount = rfqItem.Quantity!.Value * (rfqItem.UnitPrice ?? 0),
                    WarehouseId = rfqItem.WarehouseId,
                    CreatedBy = "System",
                    CreatedDate = DateTime.Now,
                    IsActive = true
                });
            }

            // Re-calc totals
            order.SubTotal = order.OrderItems.Sum(i => i.TotalAmount);
            order.TotalAmount = order.SubTotal ?? 0;

            var createdOrder = await _orderRepository.CreateOrderAsync(order);
            return MapToDto(createdOrder);
        }

        public async Task<OrderDto> CreateOrderFromQuoteAsync(long quoteId, long businessUnitId)
        {
            var existingOrder = await _context.Orders
                .Include(o => o.OrderItems)
                .FirstOrDefaultAsync(o => o.QuoteId == quoteId && o.BusinessUnitId == businessUnitId);
            if (existingOrder is not null)
                return MapToDto(existingOrder);

            var quote = await _context.Quotes
                .Include(q => q.QuoteItems)
                .Include(q => q.Rfq)
                .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId);

            if (quote == null) throw new Exception("Quote not found");

            if (quote.CustomerId == null || quote.CustomerId == 0)
                throw new Exception("Cannot create order: The source Quote does not have a linked Customer.");

            // R17 OUTPUT-TAX GATE. The same blocker the quote's own PDF and send paths run
            // (QuoteService.TaxDerivationBlocker), on the same policy rate, so a quotation this
            // platform refuses to ISSUE cannot become a sales order by the side door.
            //
            // A line whose TaxRatePercentApplied is null was never taxed at all. This method used to
            // read that null as `?? 0m` and book a standard-rated supply at SAR 0.00 VAT — and the
            // AR invoice pro-rated from the order (CommercialFinanceApplicationService) inherited
            // the zero. Under KSA law a document with no VAT separately stated is deemed
            // VAT-inclusive, so the seller owes 15/115 ≈ 13.04% of the price out of its own margin.
            // Refusing is the only honest answer: nobody can state a tax nobody derived.
            if (QuoteService.TaxDerivationBlocker(quote.QuoteItems,
                    await _context.ResolveOutputTaxRatePercentAsync(businessUnitId)) is { } taxBlocker)
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' cannot become a sales order yet. {taxBlocker}");

            // Fetch Default Statuses
            var draftStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "OrderStatus" && s.SetupCode == "DRAFT");

            if (draftStatus == null)
            {
                draftStatus = await _context.SetupMasters
                    .FirstOrDefaultAsync(s => s.SetupType == "OrderStatus");
            }

            if (draftStatus == null)
                throw new Exception("No OrderStatus setup found. Please configure OrderStatus in SetupMaster.");

            var unpaidStatus = await _context.SetupMasters
                .FirstOrDefaultAsync(s => s.SetupType == "PaymentStatus" && s.SetupCode == "UNPAID");

            var orderNo = await _orderRepository.GetNextOrderNumberAsync(businessUnitId);

            var grossSubtotal = RoundCurrency(quote.QuoteItems.Sum(i => RoundCurrency(i.Quantity * i.UnitPrice)));
            var itemDiscount = RoundCurrency(quote.QuoteItems.Sum(i => RoundCurrency(i.Discount ?? 0m)));
            var totalTax = RoundCurrency(quote.QuoteItems.Sum(i => RoundCurrency(DerivedTax(quote, i))));
            var preHeaderTotal = quote.FinancialCalculationVersion >= 2
                ? RoundCurrency(grossSubtotal - itemDiscount + totalTax)
                : RoundCurrency(grossSubtotal - itemDiscount);
            var headerDiscount = RoundCurrency(Math.Max(0m, preHeaderTotal - (quote.TotalAmount ?? preHeaderTotal)));
            var totalDiscount = RoundCurrency(itemDiscount + headerDiscount);
            var totalAmount = RoundCurrency(grossSubtotal - totalDiscount + totalTax);

            var order = new Order
            {
                OrderNo = orderNo,
                QuoteId = quote.Id,
                SourceType = OrderSourceTypes.LegacyQuote,
                LeadId = quote.Rfq?.LeadId,
                Rfqid = quote.Rfqid,
                CustomerId = quote.CustomerId.Value,
                BusinessUnitId = businessUnitId,
                StatusId = draftStatus.SetupId,
                PaymentStatusId = unpaidStatus?.SetupId,
                OrderDate = DateTime.Now,
                CurrencyId = quote.CurrencyId,
                TotalAmount = totalAmount,
                SubTotal = grossSubtotal,
                TaxAmount = totalTax,
                DiscountAmount = totalDiscount,
                BalanceAmount = totalAmount,
                CreatedBy = "System",
                CreatedOn = DateTime.Now,
                IsActive = true
            };
            order.InheritCommercialIdentity(quote);

            // Map Quote Items to Order Items
            foreach (var qItem in quote.QuoteItems)
            {
                var lineTax = DerivedTax(quote, qItem);
                order.OrderItems.Add(new OrderItem
                {
                    ProductId = qItem.ProductId ?? 0,
                    Description = qItem.ItemDescription,
                    Quantity = qItem.Quantity,
                    UnitPrice = qItem.UnitPrice,
                    Discount = qItem.Discount ?? 0,
                    TaxAmount = lineTax,
                    TotalAmount = RoundCurrency(
                        (qItem.Quantity * qItem.UnitPrice) - (qItem.Discount ?? 0m) + lineTax),
                    CreatedBy = "System",
                    CreatedDate = DateTime.Now,
                    IsActive = true
                });
            }

            _context.Orders.Add(order);

            try
            {
                if (_lifecycle is not null)
                {
                    // The transaction is owned by the configured execution strategy. Program.cs
                    // enables retry-on-failure, so NpgsqlRetryingExecutionStrategy is installed and
                    // rejects a user-initiated transaction outside its delegate — quote-to-order
                    // conversion threw "does not support user-initiated transactions" on every
                    // PostgreSQL request. Same shape as InTransactionAsync below, with one
                    // deliberate difference: NO ChangeTracker.Clear, because the Order graph was
                    // staged (Add) before this block and clearing would discard it. The transition
                    // carries its own idempotency key, so a retry converges rather than duplicating.
                    var lifecycleTransition = async () =>
                    {
                        await _lifecycle.TransitionQuoteInCurrentTransactionAsync(
                            businessUnitId,
                            quote.Id,
                            new LifecycleActor("system:order-create", "order-service"),
                            new LifecycleTransitionCommand(
                                "ORDERED",
                                quote.LifecycleVersion,
                                null,
                                null,
                                "order-create",
                                Guid.NewGuid().ToString("N"),
                                $"order-from-quote:{quote.Id}",
                                $"order-from-quote:{quote.Id}:v{quote.LifecycleVersion}"),
                            false,
                            CancellationToken.None);
                    };

                    if (_context.Database.CurrentTransaction is not null || !_context.Database.IsRelational())
                    {
                        await lifecycleTransition();
                    }
                    else
                    {
                        var strategy = _context.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            await using var transaction = await _context.Database.BeginTransactionAsync(
                                IsolationLevel.Serializable);
                            await lifecycleTransition();
                            await transaction.CommitAsync();
                        });
                    }
                }
                else
                {
                    var orderedStatus = await _context.SetupMasters
                        .FirstOrDefaultAsync(sm => sm.SetupType == "QuoteStatus" &&
                            (sm.SetupCode == "ORDERED" || sm.SetupValue == "ORDERED" || sm.SetupValue == "Ordered"));
                    if (orderedStatus != null) quote.StatusId = orderedStatus.SetupId;
                    await _context.SaveChangesAsync();
                }
            }
            catch (DbUpdateException)
            {
                _context.ChangeTracker.Clear();
                var replay = await _context.Orders
                    .Include(o => o.OrderItems)
                    .FirstOrDefaultAsync(o => o.QuoteId == quoteId && o.BusinessUnitId == businessUnitId);
                if (replay is not null)
                    return MapToDto(replay);
                throw;
            }

            return MapToDto(order);
        }

        public async Task<OrderDto> UpdateOrderAsync(long id, UpdateOrderDto dto, long businessUnitId)
        {
            var order = await _orderRepository.GetOrderByIdAsync(id, businessUnitId);
            if (order == null) throw new Exception("Order not found");

            // Check if order is locked due to shipments
            if (order.Shipments != null && order.Shipments.Any())
            {
                throw new Exception("Locked: Order cannot be modified as a shipment has been created.");
            }

            // An order that moves to CANCELLED must give its stock back. Without this the holds
            // stay Active forever, so available-to-promise stays suppressed and the units are
            // never resellable — the cancellation is invisible to every availability screen.
            var isCancelling = !await IsCancelledStatusAsync(order.StatusId, businessUnitId)
                               && await IsCancelledStatusAsync(dto.StatusId, businessUnitId);

            order.StatusId = dto.StatusId;
            order.PaymentMethodId = dto.PaymentMethodId;
            order.PaymentStatusId = dto.PaymentStatusId;
            order.PaymentDate = dto.PaymentDate;
            order.PaidAmount = dto.PaidAmount;
            order.PaymentReference = dto.PaymentReference;
            order.DeliveryDate = dto.DeliveryDate;
            order.Notes = dto.Notes;
            order.ModifiedBy = "System";
            order.ModifiedOn = DateTime.Now;

            if (!isCancelling)
            {
                await _orderRepository.UpdateOrderAsync(order, businessUnitId);
                return MapToDto(order);
            }

            // The release and the cancellation are one decision, so they commit together: an order
            // marked cancelled while its stock stays held, or stock released while the order stays
            // open, are both wrong and neither is self-correcting.
            await InTransactionAsync(async () =>
            {
                await _stock.ReleaseOrderAsync(businessUnitId, order.Id, "order-cancelled");
                await _orderRepository.UpdateOrderAsync(order, businessUnitId);
            });
            return MapToDto(order);
        }

        public async Task DeleteOrderAsync(long id, long businessUnitId)
        {
            var shipmentsExist = await _context.Shipments.AnyAsync(s => s.OrderId == id && s.BusinessUnitId == businessUnitId);
            if (shipmentsExist)
            {
                throw new Exception("Locked: Order cannot be deleted as a shipment has been created.");
            }

            // StockReservation has NO foreign key to Orders (only an index on OrderId), so
            // `Orders.Remove` leaves every active hold behind with a dangling OrderId. Nothing can
            // ever call ReleaseForOrderAsync for it again and the stock is suppressed from
            // available-to-promise permanently. Release BEFORE the delete, in the same
            // transaction, while the order still exists: the orphan sweep is a recovery path for
            // holds already stranded, not the fix.
            await InTransactionAsync(async () =>
            {
                await _stock.ReleaseOrderAsync(businessUnitId, id, "order-deleted");
                await _orderRepository.DeleteOrderAsync(id, businessUnitId);
            });
        }

        /// <summary>
        /// Runs a stock-and-order mutation as one unit of work. The reservation services join an
        /// ambient transaction when they find one, so the holds and the order state commit or roll
        /// back together.
        /// </summary>
        private async Task InTransactionAsync(Func<Task> work)
        {
            if (_context.Database.CurrentTransaction is not null)
            {
                await work();
                return;
            }

            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync();
                await work();
                await transaction.CommitAsync();
            });
        }

        /// <summary>
        /// True when the setup id names a cancelled order status in this tenant. Resolved from
        /// SetupMaster rather than a hard-coded id because OrderStatus ids are tenant-seeded.
        /// </summary>
        private async Task<bool> IsCancelledStatusAsync(long? statusId, long businessUnitId)
        {
            if (statusId is null or 0) return false;
            return await _context.SetupMasters.AsNoTracking().AnyAsync(s =>
                s.SetupId == statusId && s.BusinessUnitId == businessUnitId && s.SetupType == "OrderStatus"
                && (s.SetupCode == "CANCELLED" || s.SetupCode == "CANCELED"
                    || s.SetupValue.ToUpper() == "CANCELLED" || s.SetupValue.ToUpper() == "CANCELED"));
        }

        public async Task<IEnumerable<OrderDto>> GetOrdersByCustomerIdAsync(long customerId, long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var orders = await _orderRepository.GetOrdersByCustomerIdAsync(customerId, businessUnitId, accessScope);
            return orders.Select(MapToDto).ToList();
        }

        public async Task<InvoiceDto?> GetInvoiceDataAsync(long orderId, long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var order = await _orderRepository.GetOrderForInvoiceAsync(orderId, businessUnitId, accessScope);
            if (order == null) return null;

            return new InvoiceDto
            {
                OrderId = order.Id,
                OrderNo = order.OrderNo,
                CommercialCaseId = order.CommercialCaseId,
                NexoraSerial = order.NexoraSerial,
                OrderDate = order.OrderDate,
                DeliveryDate = order.DeliveryDate,
                Status = order.Status?.SetupValue ?? "Draft",
                QuoteNo = order.Quote?.QuoteNo,
                RfqNo = order.Rfq?.Rfqno,
                LeadNo = order.Quote?.Rfq?.Lead?.Rfqno ?? order.Rfq?.Lead?.Rfqno,
                CustomerName = order.Customer?.Name ?? "Unknown",
                CustomerEmail = order.Customer?.ContactEmail,
                CustomerPhone = order.Customer?.Contacts?.FirstOrDefault(c => c.IsPrimary == true)?.PhoneNo
                                ?? order.Customer?.Contacts?.FirstOrDefault()?.PhoneNo,
                CustomerAddress = string.Join(", ", new[] {
                    order.Customer?.BillingAddressLine1,
                    order.Customer?.BillingCity,
                    order.Customer?.BillingCountry
                }.Where(s => !string.IsNullOrEmpty(s))),
                CurrencyId = order.CurrencyId,
                CurrencyCode = order.Currency?.Code,
                SubTotal = order.SubTotal ?? 0,
                TaxAmount = order.TaxAmount ?? 0,
                DiscountAmount = order.DiscountAmount ?? 0,
                TotalAmount = order.TotalAmount,
                TermsAndConditions = order.TermsAndConditions,
                Notes = order.Notes,
                Items = order.OrderItems.Select(i => new InvoiceItemDto
                {
                    ProductName = i.Product?.ProductName ?? i.Description ?? "No Name",
                    Description = i.Description ?? "",
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    TotalAmount = i.TotalAmount
                }).ToList()
            };
        }

        // Rounds a monetary value to the 2-decimal currency scale (FIN-09).
        // Half-away-from-zero matches standard commercial/accounting rounding.
        private static decimal RoundCurrency(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        /// <summary>
        /// R17: a quote line's output tax, or a refusal. Never <c>?? 0m</c>.
        ///
        /// <para>Null on <c>QuoteItem.TaxAmount</c> means "never derived", and coercing it to zero is
        /// what let a standard-rated supply become a sales order — and then an AR invoice — stating
        /// SAR 0.00 VAT. The gate at the top of <see cref="CreateOrderFromQuoteAsync"/> has already
        /// refused every quote carrying one, so reaching this throw means a line went null between
        /// the gate and here; failing loudly is the only safe answer left.</para>
        /// </summary>
        private static decimal DerivedTax(Quote quote, QuoteItem item) =>
            item.TaxAmount ?? throw new InvalidOperationException(
                $"Quote '{quote.QuoteNo}' line {item.Id} has no derived output tax, so a sales order " +
                "cannot state its VAT. Price the line and set the output tax rate in Commercial Policy settings.");

        /// <summary>
        /// FIN-01 / R17: the order line's output tax, DERIVED from the business unit's
        /// <c>CommercialMatchingPolicy.OutputTaxRatePercent</c> — not accepted from the client.
        ///
        /// <para>This method used to return the submitted amount unchanged, above a standing
        /// TODO(FIN-01) that waited for a jurisdiction-aware tax engine to exist. Decision R8 says
        /// that engine is never being built, and waiting for it meant every order carried whatever
        /// tax the operator typed — including nothing. One tenant-level rate, set by the customer,
        /// is the whole answer the platform needs and it is available now.</para>
        ///
        /// <para><b>Known gap, stated rather than hidden.</b> R19's tax category lives on the QUOTE
        /// line, not the order line, and the order DTO carries no link back to the quote line it
        /// came from. An order is therefore taxed as a standard-rated supply. For a genuinely
        /// zero-rated export re-keyed as an order this over-states tax, which is visible on the
        /// document and correctable; the alternative — trusting a typed zero — under-states it
        /// silently and costs 15/115 of the line. Carrying the category from quote line to order
        /// line is the next increment, and it needs an order-line-to-quote-line link that does not
        /// exist today.</para>
        /// </summary>
        private static decimal? ResolveLineTaxAmount(decimal taxableBase, decimal? outputTaxRatePercent) =>
            OutputTaxFormula.Derive(RoundCurrency(taxableBase), outputTaxRatePercent,
                QuoteLineTaxCategories.Standard);

        private OrderDto MapToDto(Order order)
        {
            return new OrderDto
            {
                Id = order.Id,
                OrderNo = order.OrderNo,
                CustomerId = order.CustomerId,
                CommercialCaseId = order.CommercialCaseId,
                NexoraSerial = order.NexoraSerial,
                ContactId = order.ContactId,
                CustomerName = order.Customer?.Name ?? "Unknown",
                QuoteId = order.QuoteId,
                QuoteNo = order.Quote?.QuoteNo,
                RfqId = order.Rfqid,
                RfqNo = order.Rfq?.Rfqno,
                LeadId = order.LeadId,
                LeadNo = order.Lead?.Rfqno,
                StatusId = order.StatusId,
                Status = order.Status?.SetupValue ?? "Unknown",
                PaymentStatusId = order.PaymentStatusId,
                PaymentStatus = order.PaymentStatus?.SetupValue ?? "Unpaid",
                PaymentMethodId = order.PaymentMethodId,
                // Order.CurrencyId is persisted and the repository Includes the navigation; the
                // DTO simply never carried it, which left every Orders/Shipments amount rendered
                // without its denomination. Null Currency means the include was not applied on
                // this read path OR the order genuinely has none — both render as unknown, and
                // neither is silently defaulted to a house currency.
                CurrencyId = order.CurrencyId,
                CurrencyCode = order.Currency?.Code,
                TotalAmount = order.TotalAmount,
                SubTotal = order.SubTotal ?? 0,
                TaxAmount = order.TaxAmount ?? 0,
                DiscountAmount = order.DiscountAmount ?? 0,
                PaidAmount = order.PaidAmount,
                BalanceAmount = order.TotalAmount - order.PaidAmount,
                OrderDate = order.OrderDate,
                DeliveryDate = order.DeliveryDate,
                Notes = order.Notes,
                TermsAndConditions = order.TermsAndConditions,
                HasShipments = order.Shipments?.Any() ?? false,
                Items = order.OrderItems.Select(i => new OrderItemDto
                {
                    Id = i.Id,
                    ProductId = i.ProductId,
                    ProductName = i.Product?.ProductName ?? "Unknown Product",
                    Description = i.Description,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    Discount = i.Discount,
                    TaxAmount = i.TaxAmount,
                    TotalAmount = i.TotalAmount,
                    UomId = i.UomId,
                    WarehouseId = i.WarehouseId
                }).ToList()
            };
        }
    }
}
