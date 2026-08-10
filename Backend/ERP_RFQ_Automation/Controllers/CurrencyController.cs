using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// Currencies and the effective-dated FX rates conversions actually read.
    ///
    /// <para><b>Sec-D4.</b> Every action on this controller used to carry nothing but the
    /// class-level <c>[Authorize]</c> — no module permission, no manager role, no entitlement —
    /// while every sibling controller gated each action. Six of them are writes. The consequence
    /// was not theoretical: a user with a zero-permission role could <c>POST fx-rates</c> and then
    /// <c>POST fx-rates/{id}/approve</c> on their own rate, and only Approved rows are visible to
    /// <c>FxConversionService</c>. That forged rate then converts quote totals, sets the
    /// below-floor pricing guard's threshold and re-bases the AI agent's spend cap.</para>
    ///
    /// <para>Two things were missing and both are fixed here: the permissions (the three modules
    /// added to <see cref="ModuleCatalog"/>), and the SEPARATION between raising a rate and making
    /// it real. Approval is a distinct, higher module, and the creator of a rate cannot approve it
    /// — the same maker-checker rule <c>CommercialFinanceApplicationService</c> already applies to
    /// write-offs and refunds.</para>
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CurrencyController : ControllerBase
    {
        /// <summary>Holding the currency list.</summary>
        private const string CurrenciesModule = "Currencies";

        /// <summary>Quoting a rate, and freezing one onto a document.</summary>
        private const string FxRatesModule = "Exchange Rates";

        /// <summary>Making a rate real. Deliberately a different module from
        /// <see cref="FxRatesModule"/>: a role can be granted one without the other, which is what
        /// makes the maker-checker rule below enforceable by configuration rather than by
        /// convention.</summary>
        private const string FxApprovalModule = "Exchange Rate Approval";

        private readonly ICurrencyRepository _repository;
        private readonly ErpRfqAutomationContext _context;
        private readonly IFxConversionService _fx;

        // ErpRfqAutomationContext is already a registered scoped service, so the FX surface needs
        // no new Program.cs wiring. FxConversionService is a thin, stateless collaborator over the
        // context and is constructed directly for the same reason.
        public CurrencyController(ICurrencyRepository repository, ErpRfqAutomationContext context)
        {
            _repository = repository;
            _context = context;
            _fx = new FxConversionService(context);
        }

        private long ResolveBusinessUnit(long? fromQuery)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            return claimBUId > 0 ? claimBUId : (fromQuery ?? 0);
        }

        [HttpGet]
        [RequireModulePermission(CurrenciesModule, PermissionAction.View)]
        public async Task<ActionResult<PaginatedCurrencyResponseDTO>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? code = null,
            [FromQuery] string? currencyName = null,
            [FromQuery] decimal? exchangeRate = null,
            [FromQuery] bool? isActive = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var currencies = await _repository.GetAllAsync(targetBUId);

                var filtered = currencies.AsQueryable();

                if (!string.IsNullOrWhiteSpace(code))
                    filtered = filtered.Where(c => c.Code.Contains(code, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(currencyName))
                    filtered = filtered.Where(c => c.CurrencyName.Contains(currencyName, StringComparison.OrdinalIgnoreCase));

                if (exchangeRate.HasValue)
                    filtered = filtered.Where(c => c.ExchangeRate == exchangeRate);

                if (isActive.HasValue)
                    filtered = filtered.Where(c => c.IsActive == isActive);

                var totalItems = filtered.Count();

                var items = filtered
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .Select(MapToResponse)
                    .ToList();

                return Ok(new PaginatedCurrencyResponseDTO
                {
                    Items = items,
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                });
            }
            catch (Exception ex)
            {
                return this.ServerError(ex, "Error retrieving data.");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission(CurrenciesModule, PermissionAction.View)]
        public async Task<ActionResult<CurrencyResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var currency = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(MapToResponse(currency));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        [HttpPost]
        [RequireModulePermission(CurrenciesModule, PermissionAction.Create)]
        public async Task<ActionResult<CurrencyResponseDTO>> Create([FromBody] CurrencyCreateRequestDTO request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId > 0) request.BusinessUnitID = claimBUId;

            if (request.BusinessUnitID <= 0) return BadRequest("Business Unit ID is required.");

            var currency = new Currency
            {
                Code = request.Code,
                CurrencyName = request.CurrencyName,
                Symbol = request.Symbol,
                ExchangeRate = request.ExchangeRate,
                IsBaseCurrency = request.IsBaseCurrency ?? false,
                BusinessUnitId = request.BusinessUnitID,
                IsActive = request.IsActive ?? true,
                // Sec-A1: the client-supplied CreatedBy always won here, because Identity.Name is
                // never populated under the tenant bearer scheme. Attribution now comes from the
                // token only.
                CreatedBy = ActorContext.From(User).Stamp,
                CreatedOn = DateTime.UtcNow
            };

            await _repository.AddAsync(currency);
            return CreatedAtAction(nameof(GetById), new { id = currency.Id, businessUnitId = currency.BusinessUnitId }, MapToResponse(currency));
        }

        // Edit rather than a lesser action on purpose: this body carries IsBaseCurrency and
        // ExchangeRate, so "editing a currency" can re-base every legacy conversion in the tenant.
        [HttpPut("{id}")]
        [RequireModulePermission(CurrenciesModule, PermissionAction.Edit)]
        public async Task<IActionResult> Update(long id, [FromBody] CurrencyUpdateRequestDTO request)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitID = claimBUId;

                if (request.BusinessUnitID <= 0) return BadRequest("Business Unit ID is required.");

                var existing = await _repository.GetByIdAsync(id, request.BusinessUnitID);

                existing.Code = request.Code;
                existing.CurrencyName = request.CurrencyName;
                existing.Symbol = request.Symbol;
                existing.ExchangeRate = request.ExchangeRate;
                existing.IsBaseCurrency = request.IsBaseCurrency ?? false;
                existing.BusinessUnitId = request.BusinessUnitID;
                existing.IsActive = request.IsActive ?? true;
                existing.ModifiedBy = ActorContext.From(User).Stamp;
                existing.ModifiedOn = DateTime.UtcNow;

                await _repository.UpdateAsync(existing);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("{id}")]
        [RequireModulePermission(CurrenciesModule, PermissionAction.Delete)]
        public async Task<IActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        // ---------------------------------------------------------------- FX rates
        //
        // Currency.ExchangeRate is a single mutable, undated column with no approval and no
        // history. It is left in place for backward compatibility but is NOT what conversions
        // read. The endpoints below manage the effective-dated, approval-gated FxRates table that
        // the commercial side actually uses.

        /// <summary>Effective-dated rates for this business unit, newest window first.</summary>
        [HttpGet("fx-rates")]
        [RequireModulePermission(FxRatesModule, PermissionAction.View)]
        public async Task<ActionResult<List<FxRateResponseDTO>>> GetFxRates(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] long? fromCurrencyId = null,
            [FromQuery] long? toCurrencyId = null,
            [FromQuery] string? status = null)
        {
            var buId = ResolveBusinessUnit(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");

            var query = _context.FxRates.AsNoTracking().Where(r => r.BusinessUnitId == buId);
            if (fromCurrencyId.HasValue) query = query.Where(r => r.FromCurrencyId == fromCurrencyId.Value);
            if (toCurrencyId.HasValue) query = query.Where(r => r.ToCurrencyId == toCurrencyId.Value);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);

            var rates = await query
                .OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id)
                .ToListAsync();
            var codes = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == buId)
                .ToDictionaryAsync(c => c.Id, c => c.Code);

            return Ok(rates.Select(r => MapFxRate(r, codes)).ToList());
        }

        /// <summary>
        /// Records a new effective-dated rate. It is created as Pending and is INERT until
        /// approved — an unapproved rate can never move a commercial total.
        /// </summary>
        [HttpPost("fx-rates")]
        [RequireModulePermission(FxRatesModule, PermissionAction.Create)]
        public async Task<ActionResult<FxRateResponseDTO>> CreateFxRate(
            [FromBody] FxRateCreateRequestDTO request, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnit(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");
            if (request is null) return BadRequest("A request body is required.");
            if (request.FromCurrencyId <= 0 || request.ToCurrencyId <= 0)
                return BadRequest("Both a source and a target currency are required.");
            if (request.FromCurrencyId == request.ToCurrencyId)
                return BadRequest("A currency's rate against itself is always 1 and is not stored.");
            if (request.Rate <= 0m)
                return BadRequest("An exchange rate must be greater than zero.");
            if (request.EffectiveTo.HasValue && request.EffectiveTo.Value <= request.EffectiveFrom)
                return BadRequest("EffectiveTo must be later than EffectiveFrom.");

            // Both currencies must belong to this business unit; the FK is not declared at the
            // model level (see Models/ErpRfqAutomationContext.Fx.cs), so it is checked here.
            var owned = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == buId &&
                            (c.Id == request.FromCurrencyId || c.Id == request.ToCurrencyId))
                .Select(c => c.Id).ToListAsync();
            if (owned.Count != 2)
                return BadRequest("Both currencies must exist in this business unit.");

            var duplicate = await _context.FxRates.AnyAsync(r => r.BusinessUnitId == buId &&
                r.FromCurrencyId == request.FromCurrencyId && r.ToCurrencyId == request.ToCurrencyId &&
                r.EffectiveFrom == request.EffectiveFrom);
            if (duplicate)
                return Conflict("A rate for this currency pair already starts at that effective date.");

            var rate = new FxRate
            {
                BusinessUnitId = buId,
                FromCurrencyId = request.FromCurrencyId,
                ToCurrencyId = request.ToCurrencyId,
                Rate = FxConversionService.RoundRate(request.Rate),
                EffectiveFrom = request.EffectiveFrom,
                EffectiveTo = request.EffectiveTo,
                Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual" : request.Source.Trim(),
                Status = FxRateStatuses.Pending,
                Version = 1,
                // ActorContext.Stamp, not User.Identity?.Name. The tenant bearer scheme never
                // configures a NameClaimType, so Identity.Name is ALWAYS null here and every rate
                // in the database was created by "System" — which would have made the
                // maker-checker comparison below a comparison of two identical constants, i.e. no
                // control at all. The stamp is derived from the validated token and nothing in a
                // request body can influence it.
                CreatedBy = ActorContext.From(User).Stamp,
                CreatedOn = DateTime.UtcNow,
                Note = request.Note
            };
            _context.FxRates.Add(rate);
            await _context.SaveChangesAsync();

            var codes = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == buId).ToDictionaryAsync(c => c.Id, c => c.Code);
            return Ok(MapFxRate(rate, codes));
        }

        /// <summary>
        /// Approves a rate, making it visible to conversions from its effective date.
        ///
        /// <para><b>Sec-D4: the maker may not be the checker.</b> Approval is the ONLY thing that
        /// turns a stored number into a number that moves money — <c>FxConversionService</c> reads
        /// Approved rows and nothing else — so one person doing both halves is not an approval, it
        /// is a self-assertion with an audit column attached. Same rule and same wording as
        /// <c>CommercialFinanceApplicationService</c>, which already refuses a write-off's creator
        /// the right to post it.</para>
        ///
        /// <para>It also gates on a DIFFERENT module from <c>CreateFxRate</c>, so a tenant can
        /// actually staff the two halves separately. Gating both on the same module would leave
        /// the rule true only until someone granted one role both, which is the normal case.</para>
        /// </summary>
        [HttpPost("fx-rates/{id}/approve")]
        [RequireModulePermission(FxApprovalModule, PermissionAction.Edit)]
        public async Task<ActionResult<FxRateResponseDTO>> ApproveFxRate(long id, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnit(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");

            var rate = await _context.FxRates.FirstOrDefaultAsync(r => r.Id == id && r.BusinessUnitId == buId);
            if (rate is null) return NotFound("Exchange rate not found.");
            if (rate.Status == FxRateStatuses.Approved) return Conflict("This exchange rate is already approved.");

            var approver = ActorContext.From(User).Stamp;

            // Unattributable maker → refuse, rather than assume. A rate whose CreatedBy is absent
            // or the legacy "System" placeholder cannot be shown to have been raised by someone
            // else, and approving it would let a maker launder their own rate through a row that
            // simply does not record them. ProcurementApplicationService takes the identical line
            // when a sourcing award does not record its approver: segregation that cannot be
            // VERIFIED is not segregation. Such a rate is re-raised, not rescued.
            if (string.IsNullOrWhiteSpace(rate.CreatedBy)
                || string.Equals(rate.CreatedBy, "System", StringComparison.OrdinalIgnoreCase))
                return Conflict(
                    "Segregation of duties cannot be verified: this exchange rate does not record "
                    + "who created it. Raise the rate again and have a second person approve it.");

            if (string.Equals(rate.CreatedBy, approver, StringComparison.OrdinalIgnoreCase))
                return Conflict(
                    "The person who created an exchange rate cannot approve it. A second approver "
                    + "must confirm the rate before quotes and orders convert at it.");

            rate.Status = FxRateStatuses.Approved;
            rate.ApprovedBy = approver;
            rate.ApprovedOn = DateTime.UtcNow;
            rate.Version += 1;
            await _context.SaveChangesAsync();

            var codes = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == buId).ToDictionaryAsync(c => c.Id, c => c.Code);
            return Ok(MapFxRate(rate, codes));
        }

        /// <summary>
        /// The rate that would be applied for a pair at a given instant, and how it was reached.
        /// Returns 409 with the explicit reason when no approved rate exists — the same fail-closed
        /// wording the commercial totals surface.
        /// </summary>
        [HttpGet("fx-rates/effective")]
        [RequireModulePermission(FxRatesModule, PermissionAction.View)]
        public async Task<IActionResult> GetEffectiveRate(
            [FromQuery] long fromCurrencyId, [FromQuery] long toCurrencyId,
            [FromQuery] DateTime? asOf = null, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnit(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");
            if (fromCurrencyId <= 0 || toCurrencyId <= 0)
                return BadRequest("Both a source and a target currency are required.");

            var at = asOf ?? DateTime.UtcNow;
            var resolution = await _fx.ResolveRateAsync(buId, fromCurrencyId, toCurrencyId, at);
            if (!resolution.Found)
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "No approved exchange rate",
                    Detail = resolution.Reason
                });

            return Ok(new
            {
                fromCurrencyId,
                toCurrencyId,
                asOf = at,
                rate = resolution.Rate,
                fxRateId = resolution.FxRateId,
                rateEffectiveFrom = resolution.RateEffectiveFrom,
                resolutionPath = resolution.ResolutionPath
            });
        }

        // ------------------------------------------------------- FX snapshots (audit)

        /// <summary>
        /// "What rate was used on this quote?" — the stable answer. Returns every rate frozen
        /// against the document, with the rate row and effective date it came from.
        /// </summary>
        [HttpGet("fx-snapshots/{documentType}/{documentId}")]
        [RequireModulePermission(FxRatesModule, PermissionAction.View)]
        public async Task<ActionResult<List<FxRateSnapshotResponseDTO>>> GetFxSnapshots(
            string documentType, long documentId, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnit(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");

            var snapshots = await _fx.GetSnapshotsAsync(buId, documentType, documentId);
            var codes = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == buId).ToDictionaryAsync(c => c.Id, c => c.Code);

            return Ok(snapshots.Select(s => new FxRateSnapshotResponseDTO
            {
                Id = s.Id,
                DocumentType = s.DocumentType,
                DocumentId = s.DocumentId,
                FromCurrencyId = s.FromCurrencyId,
                FromCurrencyCode = codes.TryGetValue(s.FromCurrencyId, out var fc) ? fc : null,
                ToCurrencyId = s.ToCurrencyId,
                ToCurrencyCode = codes.TryGetValue(s.ToCurrencyId, out var tc) ? tc : null,
                Rate = s.Rate,
                FxRateId = s.FxRateId,
                RateEffectiveFrom = s.RateEffectiveFrom,
                ResolutionPath = s.ResolutionPath,
                AsOf = s.AsOf,
                CapturedOn = s.CapturedOn,
                CapturedBy = s.CapturedBy
            }).ToList());
        }

        /// <summary>
        /// Freezes the current approved rate against a document so later rate corrections cannot
        /// restate it. Idempotent: an existing snapshot is returned unchanged.
        /// </summary>
        // Create, not View: a snapshot is a WRITE that fixes the rate a document will be
        // restated against forever. It is idempotent, which is not the same as harmless — the
        // first caller decides the number.
        [HttpPost("fx-snapshots/{documentType}/{documentId}")]
        [RequireModulePermission(FxRatesModule, PermissionAction.Create)]
        public async Task<IActionResult> CaptureFxSnapshot(
            string documentType, long documentId,
            [FromQuery] long fromCurrencyId, [FromQuery] long toCurrencyId,
            [FromQuery] DateTime? asOf = null, [FromQuery] long? businessUnitId = null)
        {
            var buId = ResolveBusinessUnit(businessUnitId);
            if (buId <= 0) return BadRequest("Business Unit ID is required.");

            try
            {
                var snapshot = await _fx.CaptureSnapshotAsync(buId, documentType, documentId,
                    fromCurrencyId, toCurrencyId, asOf ?? DateTime.UtcNow, ActorContext.From(User).Stamp);
                return Ok(new
                {
                    snapshot.Id, snapshot.DocumentType, snapshot.DocumentId,
                    snapshot.FromCurrencyId, snapshot.ToCurrencyId, snapshot.Rate,
                    snapshot.FxRateId, snapshot.RateEffectiveFrom, snapshot.ResolutionPath,
                    snapshot.AsOf, snapshot.CapturedOn, snapshot.CapturedBy
                });
            }
            catch (FxConversionException ex)
            {
                // Same surfacing contract as Procurement: the message is the contract.
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "No approved exchange rate",
                    Detail = ex.Message
                });
            }
        }

        private static FxRateResponseDTO MapFxRate(FxRate r, IReadOnlyDictionary<long, string> codes) => new()
        {
            Id = r.Id,
            BusinessUnitID = r.BusinessUnitId,
            FromCurrencyId = r.FromCurrencyId,
            FromCurrencyCode = codes.TryGetValue(r.FromCurrencyId, out var fc) ? fc : null,
            ToCurrencyId = r.ToCurrencyId,
            ToCurrencyCode = codes.TryGetValue(r.ToCurrencyId, out var tc) ? tc : null,
            Rate = r.Rate,
            EffectiveFrom = r.EffectiveFrom,
            EffectiveTo = r.EffectiveTo,
            Source = r.Source,
            Status = r.Status,
            ApprovedBy = r.ApprovedBy,
            ApprovedOn = r.ApprovedOn,
            Version = r.Version,
            CreatedBy = r.CreatedBy,
            CreatedOn = r.CreatedOn,
            Note = r.Note
        };

        private static CurrencyResponseDTO MapToResponse(Currency c) => new()
        {
            Id = c.Id,
            Code = c.Code,
            CurrencyName = c.CurrencyName,
            Symbol = c.Symbol,
            ExchangeRate = c.ExchangeRate,
            IsBaseCurrency = c.IsBaseCurrency,
            BusinessUnitID = c.BusinessUnitId,
            IsActive = c.IsActive,
            CreatedBy = c.CreatedBy,
            CreatedOn = c.CreatedOn,
            ModifiedBy = c.ModifiedBy,
            ModifiedOn = c.ModifiedOn
        };
    }
}