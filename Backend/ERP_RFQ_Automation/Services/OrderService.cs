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
            _stock = stock ?? new ERP_RFQ_Automation.Inventory.OrderStockReservationService(
                context, new ERP_RFQ_Automation.Inventory.InventoryAvailabilityService(context));
        }

        public async Task<IEnumerable<OrderDto>> GetAllOrdersAsync(long businessUnitId)
        {
            var orders = await _orderRepository.GetAllOrdersAsync(businessUnitId);
            return orders.Select(MapToDto).ToList();
        }

        public async Task<OrderDto?> GetOrderByIdAsync(long id, long businessUnitId)
        {
            var order = await _orderRepository.GetOrderByIdAsync(id, businessUnitId);
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

            foreach (var itemDto in dto.Items)
            {
                // FIN-09: round each line to currency scale before summing.
                decimal lineGross = RoundCurrency(itemDto.Quantity * itemDto.UnitPrice);

                // Order line items carry only a flat discount amount (the DTO has no discount
                // type/rate). Sanitize it: never negative, never more than the gross line value.
                decimal lineDiscount = RoundCurrency(itemDto.Discount);
                if (lineDiscount > lineGross) lineDiscount = lineGross;

                // FIN-01: tax is resolved server-side (see ResolveLineTaxAmount). For the
                // pilot it uses the client-entered amount clamped non-negative; the totals
                // below are still fully recomputed so nothing the client sends is trusted wholesale.
                decimal lineTax = ResolveLineTaxAmount(itemDto.TaxAmount, lineGross - lineDiscount, businessUnitId);

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
                    Quantity = rfqItem.Quantity, // Quantity is int, not nullable
                    UnitPrice = rfqItem.UnitPrice ?? 0,
                    TotalAmount = rfqItem.Quantity * (rfqItem.UnitPrice ?? 0),
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
            var totalTax = RoundCurrency(quote.QuoteItems.Sum(i => RoundCurrency(i.TaxAmount ?? 0m)));
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
                order.OrderItems.Add(new OrderItem
                {
                    ProductId = qItem.ProductId ?? 0,
                    Description = qItem.ItemDescription,
                    Quantity = qItem.Quantity,
                    UnitPrice = qItem.UnitPrice,
                    Discount = qItem.Discount ?? 0,
                    TaxAmount = qItem.TaxAmount ?? 0,
                    TotalAmount = RoundCurrency(
                        (qItem.Quantity * qItem.UnitPrice) - (qItem.Discount ?? 0m) + (qItem.TaxAmount ?? 0m)),
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
                    await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
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
                    await transaction.CommitAsync();
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

        public async Task<IEnumerable<OrderDto>> GetOrdersByCustomerIdAsync(long customerId, long businessUnitId)
        {
            var orders = await _orderRepository.GetOrdersByCustomerIdAsync(customerId, businessUnitId);
            return orders.Select(MapToDto).ToList();
        }

        public async Task<InvoiceDto?> GetInvoiceDataAsync(long orderId, long businessUnitId)
        {
            var order = await _orderRepository.GetOrderForInvoiceAsync(orderId, businessUnitId);
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

        // FIN-01: server-side tax resolution.
        // The client-supplied TaxAmount is NOT trusted. Ideally we would compute
        // round(taxableBase * rate) from the applicable Taxis row, but that rate cannot be
        // resolved from OrderService today:
        //   * Taxis (Models/Taxis.cs: TaxRate/TaxType/Country/State/BusinessUnitId/EffectiveDate)
        //     is NOT mapped in ErpRfqAutomationContext — there is no DbSet<Taxis> and no entity
        //     configuration, so it cannot be queried here.
        //   * There is no FK from Order/OrderItem to a tax row, and selecting the correct rate
        //     needs BU + customer country/state + tax-type context not available in this method.
        // Until the schema exposes a resolvable rate, we charge 0 tax (conservative and
        // non-forgeable) rather than trusting the client value.
        // TODO(FIN-01): map Taxis in the DbContext, add a resolver keyed by BU/country/state/
        // tax-type, then return RoundCurrency(taxableBase * rate) here. Requires schema/context
        // follow-up outside OrderService.
        // FIN-01: a deterministic server-side tax engine is not yet possible — the
        // Taxis table is not mapped in the DbContext and there is no
        // BU/country/state/tax-type resolver. For the pilot we accept the client's
        // entered line tax as a workflow value (clamped non-negative) and STILL
        // recompute every order total from it server-side, so totals always reconcile
        // with the lines and a bogus header total can never be submitted.
        // TODO(FIN-01): map Taxis, add a BU/country/state/tax-type resolver, and
        // compute tax as round(taxableBase * rate) instead of accepting the amount.
        private decimal ResolveLineTaxAmount(decimal submittedTax, decimal taxableBase, long businessUnitId)
        {
            return submittedTax < 0 ? 0m : RoundCurrency(submittedTax);
        }

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
