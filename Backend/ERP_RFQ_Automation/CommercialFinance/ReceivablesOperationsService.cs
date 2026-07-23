using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.CommercialFinance;

public interface IReceivablesOperationsService
{
    Task<FinanceCommunicationContactDto> CreateContactAsync(long businessUnitId, string idempotencyKey, CreateFinanceCommunicationContactRequest request, string actor);
    Task<FinanceCommunicationContactDto> DeactivateContactAsync(long businessUnitId, long contactId, DeactivateFinanceCommunicationContactRequest request, string actor);
    Task<IReadOnlyList<FinanceCommunicationContactDto>> GetContactsAsync(long businessUnitId, long? customerId, string? purpose);
    Task<CustomerStatementDto> CreateStatementAsync(long businessUnitId, string idempotencyKey, CreateCustomerStatementRequest request, string actor);
    Task<CustomerStatementDto> FinalizeStatementAsync(long businessUnitId, long statementId, StatementActionRequest request, string actor);
    Task<CustomerStatementDto> CancelStatementAsync(long businessUnitId, long statementId, StatementActionRequest request, string actor);
    Task<CustomerStatementDto?> GetStatementAsync(long businessUnitId, long statementId);
    Task<CustomerStatementArtifactDto?> GetStatementArtifactAsync(long businessUnitId, long statementId);
    Task<IReadOnlyList<CustomerStatementDto>> GetStatementsAsync(long businessUnitId, long? customerId, string? status);
    Task<DunningPolicyDto> CreatePolicyAsync(long businessUnitId, string idempotencyKey, CreateDunningPolicyRequest request, string actor);
    Task<DunningPolicyDto> ApprovePolicyAsync(long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor);
    Task<DunningPolicyDto> ActivatePolicyAsync(long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor);
    Task<DunningPolicyDto> RetirePolicyAsync(long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor);
    Task<IReadOnlyList<DunningPolicyDto>> GetPoliciesAsync(long businessUnitId, string? status);
    Task<CustomerCollectionProfileDto> UpsertCollectionProfileAsync(long businessUnitId, UpsertCustomerCollectionProfileRequest request, string actor);
    Task<IReadOnlyList<CustomerCollectionProfileDto>> GetCollectionProfilesAsync(long businessUnitId, long? customerId);
    Task<CollectionControlDto> CreateControlAsync(long businessUnitId, string idempotencyKey, CreateCollectionControlRequest request, string actor);
    Task<CollectionControlDto> ResolveControlAsync(long businessUnitId, long controlId, ResolveCollectionControlRequest request, string actor);
    Task<IReadOnlyList<CollectionControlDto>> GetControlsAsync(long businessUnitId, long? customerId, string? status);
    Task<DunningCaseDto> OpenCaseAsync(long businessUnitId, string idempotencyKey, OpenDunningCaseRequest request, string actor);
    Task<DunningCaseDto> TransitionCaseAsync(long businessUnitId, long caseId, string action, DunningCaseActionRequest request, string actor);
    Task<DunningCaseDto> AssignCaseAsync(long businessUnitId, long caseId, AssignDunningCaseRequest request, string actor);
    Task<PromiseToPayDto> CreatePromiseAsync(long businessUnitId, long caseId, string idempotencyKey, CreatePromiseToPayRequest request, string actor);
    Task<PromiseToPayDto> ClosePromiseAsync(long businessUnitId, long promiseId, ClosePromiseToPayRequest request, string actor);
    Task<IReadOnlyList<DunningCaseDto>> GetCasesAsync(long businessUnitId, long? customerId, string? status);
    Task<DunningNoticeDto> CreateNoticeAsync(long businessUnitId, string idempotencyKey, CreateDunningNoticeRequest request, string actor);
    Task<DunningNoticeDto> TransitionNoticeAsync(long businessUnitId, long noticeId, string action, DunningNoticeActionRequest request, string actor);
    Task<DunningNoticeDto> RecordDeliveryResultAsync(long businessUnitId, long noticeId, bool delivered, DunningDeliveryResultRequest request, string actor);
    Task<IReadOnlyList<DunningNoticeDto>> GetNoticesAsync(long businessUnitId, long? caseId, string? status);
    Task<DunningRunDto> RunDunningAsync(long businessUnitId, string idempotencyKey, CreateDunningRunRequest request, string actor);
    Task<IReadOnlyList<DunningRunDto>> GetRunsAsync(long businessUnitId);
}

public sealed class ReceivablesOperationsService : IReceivablesOperationsService
{
    private const string GeneratorVersion = "nexora-ar-1";
    private readonly ErpRfqAutomationContext _context;
    private readonly byte[] _contactVerificationSecret;
    private readonly byte[] _dunningProviderSecret;

    public ReceivablesOperationsService(ErpRfqAutomationContext context, IConfiguration configuration)
    {
        _context = context;
        var secret = configuration["CommercialFinance:ContactVerificationSecret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("CommercialFinance:ContactVerificationSecret must contain at least 32 bytes.");
        _contactVerificationSecret = Encoding.UTF8.GetBytes(secret);
        var dunningSecret = configuration["CommercialFinance:DunningProviderWebhookSecret"];
        if (string.IsNullOrWhiteSpace(dunningSecret) || Encoding.UTF8.GetByteCount(dunningSecret) < 32)
            throw new InvalidOperationException("CommercialFinance:DunningProviderWebhookSecret must contain at least 32 bytes.");
        _dunningProviderSecret = Encoding.UTF8.GetBytes(dunningSecret);
    }

    public async Task<FinanceCommunicationContactDto> CreateContactAsync(
        long businessUnitId, string idempotencyKey, CreateFinanceCommunicationContactRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        if (request.VerificationProviderEventId == Guid.Empty)
            throw new ArgumentException("A trusted provider verification event ID is required.");
        var purpose = RequiredChoice(request.Purpose, "purpose", "Billing", "Collections");
        var channel = RequiredChoice(request.Channel, "channel", "Email", "Portal");
        var token = request.DestinationToken?.Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200 ||
            !Regex.IsMatch(token, "^token:[A-Za-z0-9_-]{8,194}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A verified provider destination token is required.");
        var masked = RequiredText(request.MaskedDestination, "masked destination", 120, 3);
        var evidence = RequiredText(request.VerificationEvidenceReference, "verification evidence", 500, 8);
        var effectiveFrom = NormalizeUtc(request.EffectiveFrom);
        if (request.EffectiveTo.HasValue && request.EffectiveTo <= effectiveFrom)
            throw new ArgumentException("Contact expiry must be after its effective date.");
        VerifyContactProviderSignature(businessUnitId, request, purpose, channel, token, masked,
            evidence, effectiveFrom);
        var requestHash = Hash(new { request.CustomerId, Purpose = purpose, Channel = channel,
            DestinationToken = token, MaskedDestination = masked, effectiveFrom, request.EffectiveTo,
            Evidence = evidence, request.VerificationProviderEventId });

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.FinanceCommunicationContacts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return MapContact(replay);
            }
            if (await _context.FinanceCommunicationContacts.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                    (x.DestinationToken == token || x.VerificationProviderEventId == request.VerificationProviderEventId)))
                throw new FinanceConflictException("The destination token or verification event is already registered.");
            await EnsureCustomerAsync(businessUnitId, request.CustomerId);
            var contact = new FinanceCommunicationContact
            {
                BusinessUnitId = businessUnitId,
                CustomerId = request.CustomerId,
                Purpose = purpose,
                Channel = channel,
                DestinationToken = token,
                MaskedDestination = masked,
                IsVerified = true,
                EffectiveFrom = effectiveFrom,
                EffectiveTo = request.EffectiveTo,
                VerificationEvidenceReference = evidence,
                VerificationProviderEventId = request.VerificationProviderEventId,
                ProviderSignature = request.ProviderSignature.Trim().ToLowerInvariant(),
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                CreatedBy = Actor(actor),
                CreatedOn = DateTime.UtcNow
            };
            _context.FinanceCommunicationContacts.Add(contact);
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "FinanceCommunicationContact", contact.Id, contact.Version,
                "Verified", actor, "finance.communication-contact.verified",
                new { contact.Id, contact.CustomerId, contact.Purpose, contact.Channel, contact.MaskedDestination, contact.Version });
            await _context.SaveChangesAsync();
            return MapContact(contact);
        });
    }

    public async Task<FinanceCommunicationContactDto> DeactivateContactAsync(
        long businessUnitId, long contactId, DeactivateFinanceCommunicationContactRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var contact = await LockAsync(_context.FinanceCommunicationContacts, "FinanceCommunicationContacts", contactId, businessUnitId);
            if (!contact.IsActive) return MapContact(contact);
            if (contact.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The communication contact changed; reload it.");
            var reason = RequiredText(request.Reason, "deactivation reason", 500, 20);
            contact.IsActive = false;
            contact.EffectiveTo ??= DateTime.UtcNow;
            contact.DeactivatedBy = Actor(actor);
            contact.DeactivatedOn = DateTime.UtcNow;
            contact.DeactivationReason = reason;
            contact.Version++;
            await AuditAndOutboxAsync(businessUnitId, "FinanceCommunicationContact", contact.Id, contact.Version,
                "Deactivated", actor, "finance.communication-contact.deactivated",
                new { contact.Id, contact.CustomerId, contact.Purpose, contact.Version });
            await _context.SaveChangesAsync();
            return MapContact(contact);
        });

    public async Task<IReadOnlyList<FinanceCommunicationContactDto>> GetContactsAsync(
        long businessUnitId, long? customerId, string? purpose)
    {
        var query = _context.FinanceCommunicationContacts.Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(purpose)) query = query.Where(x => x.Purpose == purpose);
        return (await query.OrderByDescending(x => x.IsActive).ThenBy(x => x.CustomerId).ThenBy(x => x.Id).ToListAsync())
            .Select(MapContact).ToArray();
    }

    public async Task<CustomerStatementDto> CreateStatementAsync(
        long businessUnitId, string idempotencyKey, CreateCustomerStatementRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var periodStart = request.PeriodStart;
        var cutoffAt = request.CutoffAt;
        if (periodStart > cutoffAt || cutoffAt > DateTime.UtcNow.AddMinutes(1))
            throw new ArgumentException("Statement period and cutoff are invalid.");
        if ((cutoffAt - periodStart).TotalDays > 370)
            throw new ArgumentException("A statement period cannot exceed 370 days.");
        var templateVersion = RequiredToken(request.TemplateVersion, "template version", 40);
        var normalized = request with { PeriodStart = periodStart, CutoffAt = cutoffAt, TemplateVersion = templateVersion };
        var requestHash = Hash(normalized);

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.CustomerStatements.Include(x => x.Lines).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return await MapStatementAsync(replay);
            }

            var customer = await _context.Customers.SingleOrDefaultAsync(x => x.Id == request.CustomerId &&
                (x.Buid == businessUnitId || x.Buid == null))
                ?? throw new KeyNotFoundException("Customer not found.");
            if (request.CurrencyId.HasValue && !await _context.Currencies.AnyAsync(x =>
                    x.Id == request.CurrencyId && x.BusinessUnitId == businessUnitId))
                throw new KeyNotFoundException("Currency not found.");
            CustomerStatement? superseded = null;
            var revision = 1;
            if (request.SupersedesStatementId.HasValue)
            {
                superseded = await LockStatementAsync(request.SupersedesStatementId.Value, businessUnitId);
                if (superseded.Status != CustomerStatementStatuses.Finalized ||
                    superseded.CustomerId != request.CustomerId || superseded.CurrencyId != request.CurrencyId)
                    throw new FinanceConflictException("Only a finalized statement for the same customer and currency can be corrected.");
                if (superseded.PeriodStart != periodStart || superseded.CutoffAt != cutoffAt)
                    throw new FinanceConflictException("A correction must retain the original statement period and cutoff.");
                if (await _context.CustomerStatements.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                        x.SupersedesStatementId == superseded.Id && x.Status != CustomerStatementStatuses.Cancelled))
                    throw new FinanceConflictException("The statement already has a correction successor.");
                revision = superseded.Revision + 1;
            }
            var correctionReason = request.SupersedesStatementId.HasValue
                ? RequiredText(request.CorrectionReason, "correction reason", 500, 20)
                : null;

            var snapshot = await BuildStatementSnapshotAsync(
                businessUnitId, request.CustomerId, request.CurrencyId, periodStart, cutoffAt);
            var issuerName = await _context.BusinessUnits.Where(x => x.Id == businessUnitId)
                .Select(x => x.BusinessUnitName).SingleAsync();
            var billingAddress = string.Join(", ", new[] { customer.BillingAddressLine1, customer.BillingAddressLine2,
                customer.BillingCity, customer.BillingState, customer.BillingPostalCode, customer.BillingCountry }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
            if (string.IsNullOrWhiteSpace(billingAddress)) billingAddress = "Billing address not provided";
            var capturedOn = DateTime.UtcNow;
            var snapshotHash = Hash(new { businessUnitId, request.CustomerId, request.CurrencyId,
                periodStart, cutoffAt, revision, snapshot.SourceFingerprint, snapshot.OpeningBalance,
                snapshot.DebitTotal, snapshot.CreditTotal, snapshot.UnappliedCash, snapshot.ClosingBalance,
                snapshot.NetCustomerPosition, snapshot.Aging, Lines = snapshot.Lines });
            var currencyCode = await CurrencyCodeAsync(request.CurrencyId) ?? "Base currency";
            var artifactContent = BuildStatementArtifact(issuerName, customer.Name, billingAddress,
                currencyCode, periodStart, cutoffAt, revision, snapshot);
            var artifactHash = HashText(artifactContent);
            var statement = new CustomerStatement
            {
                BusinessUnitId = businessUnitId,
                CustomerId = request.CustomerId,
                CurrencyId = request.CurrencyId,
                SupersedesStatementId = request.SupersedesStatementId,
                PeriodStart = periodStart,
                CutoffAt = cutoffAt,
                CapturedOn = capturedOn,
                Revision = revision,
                OpeningBalance = snapshot.OpeningBalance,
                DebitTotal = snapshot.DebitTotal,
                CreditTotal = snapshot.CreditTotal,
                UnappliedCash = snapshot.UnappliedCash,
                ClosingBalance = snapshot.ClosingBalance,
                NetCustomerPosition = snapshot.NetCustomerPosition,
                AgingCurrent = snapshot.Aging.Current,
                Aging1To30 = snapshot.Aging.OneToThirty,
                Aging31To60 = snapshot.Aging.ThirtyOneToSixty,
                Aging61To90 = snapshot.Aging.SixtyOneToNinety,
                AgingOver90 = snapshot.Aging.OverNinety,
                SourceFingerprint = snapshot.SourceFingerprint,
                SnapshotHash = snapshotHash,
                ArtifactHash = artifactHash,
                ArtifactMediaType = "text/html; charset=utf-8",
                ArtifactContent = artifactContent,
                GeneratorVersion = GeneratorVersion,
                TemplateVersion = templateVersion,
                IssuerNameSnapshot = issuerName,
                CustomerNameSnapshot = customer.Name,
                BillingAddressSnapshot = billingAddress,
                CorrectionReason = correctionReason,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                CreatedBy = Actor(actor),
                CreatedOn = capturedOn
            };
            foreach (var line in snapshot.Lines)
                statement.Lines.Add(new CustomerStatementLine
                {
                    BusinessUnitId = businessUnitId,
                    Sequence = line.Sequence,
                    SourceType = line.SourceType,
                    SourceId = line.SourceId,
                    SourceVersion = line.SourceVersion,
                    SourceNumber = line.SourceNumber,
                    CommercialCaseId = line.CommercialCaseId,
                    ActivityDate = line.ActivityDate,
                    DueDate = line.DueDate,
                    Description = line.Description,
                    DebitAmount = line.DebitAmount,
                    CreditAmount = line.CreditAmount,
                    AppliedAmount = line.AppliedAmount,
                    OutstandingAmount = line.OutstandingAmount,
                    AgingBucket = line.AgingBucket,
                    RunningBalance = line.RunningBalance
                });
            _context.CustomerStatements.Add(statement);
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "CustomerStatement", statement.Id, statement.Version,
                "Generated", actor, "finance.statement.generated",
                new { statement.Id, statement.CustomerId, statement.CurrencyId, statement.CutoffAt,
                    statement.Revision, statement.SnapshotHash, statement.Version });
            await _context.SaveChangesAsync();
            return await MapStatementAsync(statement);
        });
    }

    public async Task<CustomerStatementDto> FinalizeStatementAsync(
        long businessUnitId, long statementId, StatementActionRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var statement = await LockStatementAsync(statementId, businessUnitId);
            if (statement.Status == CustomerStatementStatuses.Finalized) return await MapStatementAsync(statement);
            if (statement.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The statement changed; reload it.");
            if (statement.Status != CustomerStatementStatuses.Draft)
                throw new FinanceConflictException("Only a draft statement can be finalized.");
            if (string.Equals(statement.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new FinanceConflictException("Statement finalization requires an independent checker.");
            var current = await BuildStatementSnapshotAsync(businessUnitId, statement.CustomerId,
                statement.CurrencyId, statement.PeriodStart, statement.CutoffAt);
            if (!string.Equals(current.SourceFingerprint, statement.SourceFingerprint, StringComparison.Ordinal))
                throw new FinanceConflictException("The receivable ledger changed after statement generation; create a fresh statement.");
            var sequence = await AllocateNumberAsync(businessUnitId, "Statement", statement.CutoffAt.Year);
            statement.StatementNumber = $"STM-{statement.CutoffAt.Year}-{sequence:D8}";
            statement.ArtifactContent = statement.ArtifactContent.Replace(
                "{{STATEMENT_NUMBER}}", WebUtility.HtmlEncode(statement.StatementNumber), StringComparison.Ordinal);
            statement.ArtifactHash = HashText(statement.ArtifactContent);
            statement.Status = CustomerStatementStatuses.Finalized;
            statement.FinalizedBy = Actor(actor);
            statement.FinalizedOn = DateTime.UtcNow;
            statement.ArtifactReference = $"statement:{statement.Id}:{statement.ArtifactHash}";
            statement.Version++;
            if (statement.SupersedesStatementId.HasValue)
            {
                var prior = await LockStatementAsync(statement.SupersedesStatementId.Value, businessUnitId);
                if (prior.Status != CustomerStatementStatuses.Finalized)
                    throw new FinanceConflictException("The superseded statement is no longer final.");
                prior.Status = CustomerStatementStatuses.Superseded;
                prior.Version++;
            }
            await AuditAndOutboxAsync(businessUnitId, "CustomerStatement", statement.Id, statement.Version,
                "Finalized", actor, "finance.statement.finalized",
                new { statement.Id, statement.StatementNumber, statement.CustomerId, statement.CurrencyId,
                    statement.CutoffAt, statement.Revision, statement.SnapshotHash, statement.ArtifactHash, statement.Version });
            await _context.SaveChangesAsync();
            return await MapStatementAsync(statement);
        });

    public async Task<CustomerStatementDto> CancelStatementAsync(
        long businessUnitId, long statementId, StatementActionRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var statement = await LockStatementAsync(statementId, businessUnitId);
            if (statement.Status == CustomerStatementStatuses.Cancelled) return await MapStatementAsync(statement);
            if (statement.Version != request.ExpectedVersion || statement.Status != CustomerStatementStatuses.Draft)
                throw new FinanceConflictException("Only the current draft statement can be cancelled.");
            var reason = RequiredText(request.Reason, "cancellation reason", 500, 20);
            statement.Status = CustomerStatementStatuses.Cancelled;
            statement.CancelledBy = Actor(actor);
            statement.CancelledOn = DateTime.UtcNow;
            statement.CancellationReason = reason;
            statement.Version++;
            await AuditAndOutboxAsync(businessUnitId, "CustomerStatement", statement.Id, statement.Version,
                "Cancelled", actor, "finance.statement.cancelled",
                new { statement.Id, statement.CustomerId, statement.CurrencyId, statement.SnapshotHash, statement.Version });
            await _context.SaveChangesAsync();
            return await MapStatementAsync(statement);
        });

    public async Task<CustomerStatementDto?> GetStatementAsync(long businessUnitId, long statementId)
    {
        var statement = await _context.CustomerStatements.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == statementId);
        return statement is null ? null : await MapStatementAsync(statement);
    }

    public async Task<CustomerStatementArtifactDto?> GetStatementArtifactAsync(long businessUnitId, long statementId)
        => await _context.CustomerStatements.Where(x => x.BusinessUnitId == businessUnitId && x.Id == statementId &&
                (x.Status == CustomerStatementStatuses.Finalized || x.Status == CustomerStatementStatuses.Superseded))
            .Select(x => new CustomerStatementArtifactDto(x.Id, x.ArtifactMediaType, x.ArtifactContent,
                x.ArtifactHash, x.ArtifactReference)).SingleOrDefaultAsync();

    public async Task<IReadOnlyList<CustomerStatementDto>> GetStatementsAsync(
        long businessUnitId, long? customerId, string? status)
    {
        var query = _context.CustomerStatements.Include(x => x.Lines).Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var rows = await query.OrderByDescending(x => x.CutoffAt).ThenByDescending(x => x.Revision).Take(500).ToListAsync();
        var result = new List<CustomerStatementDto>(rows.Count);
        foreach (var row in rows) result.Add(await MapStatementAsync(row));
        return result;
    }

    public async Task<DunningPolicyDto> CreatePolicyAsync(
        long businessUnitId, string idempotencyKey, CreateDunningPolicyRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var name = RequiredText(request.Name, "policy name", 120, 3);
        var jurisdiction = RequiredToken(request.JurisdictionCode, "jurisdiction", 20);
        var timeZone = RequiredText(request.TimeZoneId, "time zone", 100, 2);
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("The dunning policy time zone is invalid."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("The dunning policy time zone is invalid."); }
        var template = RequiredToken(request.TemplateVersion, "template version", 40);
        var steps = request.Steps.OrderBy(x => x.Stage).ToArray();
        if (steps.Length == 0 || steps.Length > 9 || steps.Select(x => x.Stage).SequenceEqual(Enumerable.Range(1, steps.Length)) is false)
            throw new ArgumentException("Policy stages must be contiguous from 1 through 9.");
        if (request.GraceDays < 0 || request.CadenceDays <= 0 || request.MinimumOverdueAmount < 0 ||
            request.QuietHoursStart is < 0 or > 23 || request.QuietHoursEnd is < 0 or > 23)
            throw new ArgumentException("Dunning policy thresholds are invalid.");
        foreach (var step in steps)
        {
            if (step.MinimumDaysPastDue < 0 || step.MinimumAmount < 0 || step.WaitDaysAfterPriorStage < 0 ||
                step.MaximumAttempts is < 1 or > 20)
                throw new ArgumentException("Dunning policy step thresholds are invalid.");
            _ = RequiredChoice(step.Channel, "channel", "Email", "Portal");
            _ = RequiredToken(step.TemplateVersion, "step template version", 40);
            _ = RequiredText(step.EscalationRole, "escalation role", 100, 2);
        }
        var normalized = request with { Name = name, JurisdictionCode = jurisdiction, TimeZoneId = timeZone,
            TemplateVersion = template, Steps = steps };
        var requestHash = Hash(normalized);

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.DunningPolicies.Include(x => x.Steps).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return MapPolicy(replay); }
            var nextVersion = (await _context.DunningPolicies.Where(x => x.BusinessUnitId == businessUnitId)
                .MaxAsync(x => (int?)x.PolicyVersion) ?? 0) + 1;
            var policy = new DunningPolicy
            {
                BusinessUnitId = businessUnitId, PolicyVersion = nextVersion, Name = name,
                JurisdictionCode = jurisdiction, TimeZoneId = timeZone, GraceDays = request.GraceDays,
                CadenceDays = request.CadenceDays, MaximumStage = steps.Length,
                MinimumOverdueAmount = Round(request.MinimumOverdueAmount),
                QuietHoursStart = request.QuietHoursStart, QuietHoursEnd = request.QuietHoursEnd,
                TemplateVersion = template, IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            foreach (var step in steps) policy.Steps.Add(new DunningPolicyStep
            {
                BusinessUnitId = businessUnitId, Stage = step.Stage,
                MinimumDaysPastDue = step.MinimumDaysPastDue, MinimumAmount = Round(step.MinimumAmount),
                WaitDaysAfterPriorStage = step.WaitDaysAfterPriorStage, Channel = step.Channel,
                TemplateVersion = step.TemplateVersion, RequiresApproval = step.RequiresApproval,
                EscalationRole = step.EscalationRole, MaximumAttempts = step.MaximumAttempts
            });
            _context.DunningPolicies.Add(policy);
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "DunningPolicy", policy.Id, policy.Version,
                "DraftCreated", actor, "finance.dunning-policy.draft-created",
                new { policy.Id, policy.PolicyVersion, policy.JurisdictionCode, policy.MaximumStage, policy.Version });
            await _context.SaveChangesAsync();
            return MapPolicy(policy);
        });
    }

    public async Task<CustomerCollectionProfileDto> UpsertCollectionProfileAsync(
        long businessUnitId, UpsertCustomerCollectionProfileRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            await EnsureCustomerAsync(businessUnitId, request.CustomerId);
            if (request.CurrencyId.HasValue && !await _context.Currencies.AnyAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == request.CurrencyId.Value))
                throw new KeyNotFoundException("Currency not found.");
            var policy = await _context.DunningPolicies.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.DunningPolicyId)
                ?? throw new KeyNotFoundException("Dunning policy not found.");
            if (policy.Status is not ("Approved" or "Active"))
                throw new FinanceConflictException("Only an approved or active dunning policy can be assigned.");
            if (request.FinanceCommunicationContactId.HasValue)
            {
                var contact = await _context.FinanceCommunicationContacts.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == request.FinanceCommunicationContactId.Value)
                    ?? throw new KeyNotFoundException("Communication contact not found.");
                if (contact.CustomerId != request.CustomerId || !contact.IsVerified || !contact.IsActive)
                    throw new FinanceConflictException("The communication contact is not an active verified contact for this customer.");
            }
            var locale = RequiredToken(request.Locale, "locale", 20);
            var timeZone = RequiredText(request.TimeZoneId, "time zone", 100, 2);
            EnsureTimeZone(timeZone);
            var collector = OptionalText(request.Collector, "collector", 255);
            var profile = await _context.CustomerCollectionProfiles.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.CustomerId == request.CustomerId &&
                x.CurrencyId == request.CurrencyId);
            if (profile is null)
            {
                if (request.ExpectedVersion.HasValue)
                    throw new FinanceConflictException("The collection profile no longer exists; reload it.");
                profile = new CustomerCollectionProfile
                {
                    BusinessUnitId = businessUnitId, CustomerId = request.CustomerId,
                    CurrencyId = request.CurrencyId, CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
                };
                _context.CustomerCollectionProfiles.Add(profile);
            }
            else
            {
                if (!request.ExpectedVersion.HasValue || profile.Version != request.ExpectedVersion.Value)
                    throw new FinanceConflictException("The collection profile changed; reload it.");
                profile.Version++;
                profile.ModifiedBy = Actor(actor);
                profile.ModifiedOn = DateTime.UtcNow;
            }
            profile.DunningPolicyId = request.DunningPolicyId;
            profile.FinanceCommunicationContactId = request.FinanceCommunicationContactId;
            profile.Locale = locale;
            profile.TimeZoneId = timeZone;
            profile.Collector = collector;
            profile.AutomaticDeliveryAllowed = request.AutomaticDeliveryAllowed;
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "CustomerCollectionProfile", profile.Id, profile.Version,
                "Upserted", actor, "finance.collection-profile.upserted",
                new { profile.Id, profile.CustomerId, profile.CurrencyId, profile.DunningPolicyId,
                    profile.AutomaticDeliveryAllowed, profile.Version });
            await _context.SaveChangesAsync();
            return MapProfile(profile);
        });

    public async Task<IReadOnlyList<CustomerCollectionProfileDto>> GetCollectionProfilesAsync(
        long businessUnitId, long? customerId)
    {
        var query = _context.CustomerCollectionProfiles.Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        return (await query.OrderBy(x => x.CustomerId).ThenBy(x => x.CurrencyId).Take(500).ToListAsync())
            .Select(MapProfile).ToArray();
    }

    public async Task<CollectionControlDto> CreateControlAsync(
        long businessUnitId, string idempotencyKey, CreateCollectionControlRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var type = RequiredChoice(request.ControlType, "control type", CollectionControlTypes.Dispute,
            CollectionControlTypes.CommunicationRestriction, CollectionControlTypes.LegalHold);
        var reasonCode = RequiredToken(request.ReasonCode, "reason code", 50);
        var reason = RequiredText(request.Reason, "reason", 500, 20);
        var evidence = RequiredText(request.EvidenceReference, "evidence reference", 500, 8);
        var effectiveFrom = request.EffectiveFrom ?? DateTime.UtcNow;
        if (request.ReviewOn.HasValue && request.ReviewOn < effectiveFrom)
            throw new ArgumentException("Control review date cannot precede its effective date.");
        if (request.ExpiresOn.HasValue && request.ExpiresOn <= effectiveFrom)
            throw new ArgumentException("Control expiry must follow its effective date.");
        if (type == CollectionControlTypes.Dispute &&
            (!request.ReceivableDocumentId.HasValue || request.DisputedAmount is null or <= 0))
            throw new ArgumentException("A dispute requires a receivable document and positive disputed amount.");
        if (type != CollectionControlTypes.Dispute && request.DisputedAmount.HasValue)
            throw new ArgumentException("Only a dispute may carry a disputed amount.");
        var requestHash = Hash(new { request.CustomerId, request.CurrencyId, request.ReceivableDocumentId,
            ControlType = type, DisputedAmount = request.DisputedAmount.HasValue ? (decimal?)Round(request.DisputedAmount.Value) : null,
            ReasonCode = reasonCode, Reason = reason, Evidence = evidence, effectiveFrom, request.ReviewOn, request.ExpiresOn });

        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.CollectionControls.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return MapControl(replay);
            }
            await EnsureCustomerAsync(businessUnitId, request.CustomerId);
            if (request.ReceivableDocumentId.HasValue)
            {
                var document = await _context.ReceivableDocuments.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == request.ReceivableDocumentId.Value)
                    ?? throw new KeyNotFoundException("Receivable document not found.");
                if (document.CustomerId != request.CustomerId || document.CurrencyId != request.CurrencyId)
                    throw new FinanceConflictException("The disputed document does not match the customer and currency.");
                var open = await GetDocumentOutstandingAsync(businessUnitId, document.Id, DateTime.UtcNow);
                if (Round(request.DisputedAmount!.Value) > open)
                    throw new FinanceConflictException("The disputed amount exceeds the document's current outstanding balance.");
            }
            var duplicate = await _context.CollectionControls.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                x.CustomerId == request.CustomerId && x.CurrencyId == request.CurrencyId &&
                x.ReceivableDocumentId == request.ReceivableDocumentId && x.ControlType == type && x.Status == "Active");
            if (duplicate) throw new FinanceConflictException("An equivalent active collection control already exists.");
            var control = new CollectionControl
            {
                BusinessUnitId = businessUnitId, CustomerId = request.CustomerId, CurrencyId = request.CurrencyId,
                ReceivableDocumentId = request.ReceivableDocumentId, ControlType = type,
                DisputedAmount = request.DisputedAmount.HasValue ? Round(request.DisputedAmount.Value) : null,
                ReasonCode = reasonCode, Reason = reason, EvidenceReference = evidence,
                EffectiveFrom = effectiveFrom, ReviewOn = request.ReviewOn, ExpiresOn = request.ExpiresOn,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            _context.CollectionControls.Add(control);
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "CollectionControl", control.Id, control.Version,
                "Created", actor, "finance.collection-control.created",
                new { control.Id, control.CustomerId, control.CurrencyId, control.ReceivableDocumentId,
                    control.ControlType, control.DisputedAmount, control.Version, IdempotencyKey = idempotencyKey,
                    RequestHash = requestHash });
            await _context.SaveChangesAsync();
            return MapControl(control);
        });
    }

    public async Task<CollectionControlDto> ResolveControlAsync(
        long businessUnitId, long controlId, ResolveCollectionControlRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var control = await LockAsync(_context.CollectionControls, "CollectionControls", controlId, businessUnitId);
            if (control.Status == "Resolved") return MapControl(control);
            if (control.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The collection control changed; reload it.");
            control.Status = "Resolved";
            control.ResolutionReason = RequiredText(request.Reason, "resolution reason", 500, 20);
            control.ResolutionEvidenceReference = RequiredText(request.EvidenceReference, "resolution evidence", 500, 8);
            control.ResolvedBy = Actor(actor);
            control.ResolvedOn = DateTime.UtcNow;
            control.Version++;
            await AuditAndOutboxAsync(businessUnitId, "CollectionControl", control.Id, control.Version,
                "Resolved", actor, "finance.collection-control.resolved",
                new { control.Id, control.CustomerId, control.ControlType, control.Version });
            await _context.SaveChangesAsync();
            return MapControl(control);
        });

    public async Task<IReadOnlyList<CollectionControlDto>> GetControlsAsync(
        long businessUnitId, long? customerId, string? status)
    {
        var query = _context.CollectionControls.Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return (await query.OrderByDescending(x => x.CreatedOn).Take(500).ToListAsync()).Select(MapControl).ToArray();
    }

    public async Task<DunningCaseDto> OpenCaseAsync(
        long businessUnitId, string idempotencyKey, OpenDunningCaseRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var requestHash = Hash(request);
        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.DunningCases.Include(x => x.Promises).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return await MapCaseAsync(replay); }
            var statement = await LockStatementAsync(request.CustomerStatementId, businessUnitId);
            if (statement.Status != CustomerStatementStatuses.Finalized || statement.NetCustomerPosition <= 0)
                throw new FinanceConflictException("A dunning case requires a finalized statement with positive customer exposure.");
            var policy = await _context.DunningPolicies.Include(x => x.Steps).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.DunningPolicyId)
                ?? throw new KeyNotFoundException("Dunning policy not found.");
            if (policy.Status != "Active") throw new FinanceConflictException("The dunning policy is not active.");
            var createdOn = DateTime.UtcNow;
            var current = await BuildStatementSnapshotAsync(businessUnitId, statement.CustomerId,
                statement.CurrencyId, statement.PeriodStart, createdOn);
            if (current.NetCustomerPosition <= 0 || !current.OldestOutstandingDueDate.HasValue ||
                current.OldestOutstandingDueDate.Value.Date.AddDays(policy.GraceDays) >= createdOn.Date)
                throw new FinanceConflictException("A dunning case requires a currently overdue customer balance beyond its grace period.");
            var profile = await _context.CustomerCollectionProfiles.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.CustomerId == statement.CustomerId &&
                x.CurrencyId == statement.CurrencyId);
            if (profile is null || profile.DunningPolicyId != policy.Id)
                throw new FinanceConflictException("The customer has no matching governed collection profile.");
            if (await HasBlockingControlAsync(businessUnitId, statement.CustomerId, statement.CurrencyId, null, createdOn))
                throw new FinanceConflictException("An active collection control blocks opening this dunning case.");
            var oldestDue = current.OldestOutstandingDueDate.Value;
            var dueDays = Math.Max(policy.GraceDays, policy.Steps.Min(x => x.MinimumDaysPastDue));
            var item = new DunningCase
            {
                BusinessUnitId = businessUnitId, CustomerId = statement.CustomerId,
                CurrencyId = statement.CurrencyId, DunningPolicyId = policy.Id,
                CustomerStatementId = statement.Id, ExposureAtOpen = current.NetCustomerPosition,
                CurrentExposure = current.NetCustomerPosition, OldestDueDate = oldestDue,
                NextActionOn = oldestDue.AddDays(dueDays), AssignedTo = OptionalText(request.AssignedTo, "assignee", 255),
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = createdOn
            };
            _context.DunningCases.Add(item);
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "DunningCase", item.Id, item.Version,
                "Opened", actor, "finance.dunning-case.opened",
                new { item.Id, item.CustomerId, item.CurrencyId, item.CustomerStatementId,
                    item.ExposureAtOpen, item.NextActionOn, item.Version });
            await _context.SaveChangesAsync();
            return await MapCaseAsync(item);
        });
    }

    public async Task<DunningCaseDto> TransitionCaseAsync(
        long businessUnitId, long caseId, string action, DunningCaseActionRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var item = await LockCaseAsync(caseId, businessUnitId);
            var target = RequiredChoice(action, "case action", "hold", "dispute", "resume", "resolve", "cancel");
            var targetStatus = target.ToLowerInvariant() switch
            {
                "hold" => DunningCaseStatuses.Held,
                "dispute" => DunningCaseStatuses.Disputed,
                "resume" => DunningCaseStatuses.Open,
                "resolve" => DunningCaseStatuses.Resolved,
                _ => DunningCaseStatuses.Cancelled
            };
            if (item.Status == targetStatus) return await MapCaseAsync(item);
            if (item.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The dunning case changed; reload it.");
            var valid = (item.Status, targetStatus) is
                (DunningCaseStatuses.Open, DunningCaseStatuses.Held) or
                (DunningCaseStatuses.Open, DunningCaseStatuses.Disputed) or
                (DunningCaseStatuses.Held, DunningCaseStatuses.Open) or
                (DunningCaseStatuses.Disputed, DunningCaseStatuses.Open) or
                (DunningCaseStatuses.Open, DunningCaseStatuses.Resolved) or
                (DunningCaseStatuses.Held, DunningCaseStatuses.Resolved) or
                (DunningCaseStatuses.Disputed, DunningCaseStatuses.Resolved) or
                (DunningCaseStatuses.Open, DunningCaseStatuses.Cancelled) or
                (DunningCaseStatuses.Held, DunningCaseStatuses.Cancelled) or
                (DunningCaseStatuses.Disputed, DunningCaseStatuses.Cancelled);
            if (!valid) throw new FinanceConflictException($"The dunning case cannot move from {item.Status} to {targetStatus}.");
            item.Status = targetStatus;
            item.StatusReason = RequiredText(request.Reason, "status reason", 500, 20);
            item.EvidenceReference = RequiredText(request.EvidenceReference, "evidence reference", 500, 8);
            item.UpdatedBy = Actor(actor);
            item.UpdatedOn = DateTime.UtcNow;
            item.Version++;
            if (targetStatus is DunningCaseStatuses.Resolved or DunningCaseStatuses.Cancelled)
            {
                var notices = await _context.DunningNotices.Where(x => x.BusinessUnitId == businessUnitId &&
                    x.DunningCaseId == item.Id && (x.Status == DunningNoticeStatuses.Draft || x.Status == DunningNoticeStatuses.Approved)).ToListAsync();
                foreach (var notice in notices)
                {
                    notice.Status = DunningNoticeStatuses.Cancelled;
                    notice.CancelledBy = Actor(actor); notice.CancelledOn = DateTime.UtcNow;
                    notice.CancellationReason = "The governing dunning case was closed."; notice.Version++;
                }
            }
            await AuditAndOutboxAsync(businessUnitId, "DunningCase", item.Id, item.Version,
                targetStatus, actor, $"finance.dunning-case.{targetStatus.ToLowerInvariant()}",
                new { item.Id, item.CustomerId, item.Status, item.CurrentExposure, item.Version });
            await _context.SaveChangesAsync();
            return await MapCaseAsync(item);
        });

    public async Task<DunningCaseDto> AssignCaseAsync(
        long businessUnitId, long caseId, AssignDunningCaseRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var item = await LockCaseAsync(caseId, businessUnitId);
            if (item.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The dunning case changed; reload it.");
            if (item.Status is DunningCaseStatuses.Resolved or DunningCaseStatuses.Cancelled)
                throw new FinanceConflictException("A closed dunning case cannot be assigned.");
            item.AssignedTo = RequiredText(request.AssignedTo, "assignee", 255, 2);
            item.UpdatedBy = Actor(actor); item.UpdatedOn = DateTime.UtcNow; item.Version++;
            await AuditAndOutboxAsync(businessUnitId, "DunningCase", item.Id, item.Version,
                "Assigned", actor, "finance.dunning-case.assigned",
                new { item.Id, item.AssignedTo, item.Version });
            await _context.SaveChangesAsync();
            return await MapCaseAsync(item);
        });

    public async Task<PromiseToPayDto> CreatePromiseAsync(
        long businessUnitId, long caseId, string idempotencyKey, CreatePromiseToPayRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            ValidateKey(idempotencyKey);
            var requestHash = Hash(new { caseId, request.Amount, request.DueOn, request.EvidenceReference });
            var replay = await _context.PromisesToPay.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return MapPromise(replay); }
            var item = await LockCaseAsync(caseId, businessUnitId);
            if (item.Version != request.ExpectedCaseVersion)
                throw new FinanceConflictException("The dunning case changed; reload it.");
            if (item.Status != DunningCaseStatuses.Open)
                throw new FinanceConflictException("Promises can only be recorded on an open dunning case.");
            await RefreshCaseExposureAsync(item, businessUnitId);
            var amount = Round(request.Amount);
            if (amount <= 0 || amount > item.CurrentExposure)
                throw new ArgumentException("Promise amount must be positive and no greater than current exposure.");
            if (request.DueOn < DateTime.UtcNow.Date)
                throw new ArgumentException("Promise due date cannot be in the past.");
            if (await _context.PromisesToPay.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                    x.DunningCaseId == caseId && x.Status == "Open"))
                throw new FinanceConflictException("The dunning case already has an open promise to pay.");
            var promise = new PromiseToPay
            {
                BusinessUnitId = businessUnitId, DunningCaseId = caseId, Amount = amount,
                PromisedOn = DateTime.UtcNow, DueOn = request.DueOn,
                EvidenceReference = RequiredText(request.EvidenceReference, "promise evidence", 500, 8),
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            _context.PromisesToPay.Add(promise);
            item.PromiseAmount = amount; item.PromiseDueOn = request.DueOn;
            item.NextActionOn = request.DueOn; item.UpdatedBy = Actor(actor); item.UpdatedOn = DateTime.UtcNow; item.Version++;
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "DunningCase", item.Id, item.Version,
                "PromiseRecorded", actor, "finance.promise-to-pay.recorded",
                new { item.Id, PromiseId = promise.Id, promise.Amount, promise.DueOn, item.Version });
            await _context.SaveChangesAsync();
            return MapPromise(promise);
        });

    public async Task<PromiseToPayDto> ClosePromiseAsync(
        long businessUnitId, long promiseId, ClosePromiseToPayRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var promise = await LockAsync(_context.PromisesToPay, "PromisesToPay", promiseId, businessUnitId);
            var status = RequiredChoice(request.Status, "promise status", "Kept", "Broken", "Withdrawn");
            if (promise.Status == status) return MapPromise(promise);
            if (promise.Version != request.ExpectedVersion || promise.Status != "Open")
                throw new FinanceConflictException("Only the current open promise can be closed.");
            var item = await LockCaseAsync(promise.DunningCaseId, businessUnitId);
            if (status == "Kept")
            {
                if (!request.MatchedPaymentId.HasValue)
                    throw new ArgumentException("A matched posted payment is required to mark a promise kept.");
                var payment = await _context.CustomerPayments.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == request.MatchedPaymentId.Value &&
                    x.CustomerId == item.CustomerId && x.CurrencyId == item.CurrencyId &&
                    x.Status == CustomerPaymentStatuses.Posted && x.ReversedOn == null &&
                    x.PaymentDate >= promise.PromisedOn)
                    ?? throw new FinanceConflictException("The matched payment is not an eligible posted receipt for this promise.");
                var refunded = await _context.CustomerRefunds.Where(x => x.BusinessUnitId == businessUnitId &&
                    x.SourcePaymentId == payment.Id && x.ReleasedOn != null && x.ReleasedOn <= DateTime.UtcNow &&
                    (x.ReversedOn == null || x.ReversedOn > DateTime.UtcNow)).SumAsync(x => (decimal?)x.Amount) ?? 0;
                var matchedAmount = Math.Max(0, Round(payment.Amount - refunded));
                if (matchedAmount < promise.Amount)
                    throw new FinanceConflictException("The matched payment does not satisfy the promised amount.");
                if (await _context.PromisesToPay.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                        x.MatchedPaymentId == payment.Id && x.Id != promise.Id))
                    throw new FinanceConflictException("The matched payment is already evidence for another promise.");
                promise.MatchedPaymentId = payment.Id;
                promise.MatchedAmount = matchedAmount;
            }
            else if (request.MatchedPaymentId.HasValue)
            {
                throw new ArgumentException("A matched payment is only valid when a promise is marked kept.");
            }
            promise.Status = status; promise.ClosureEvidenceReference = RequiredText(request.EvidenceReference, "closure evidence", 500, 8);
            promise.ClosedBy = Actor(actor); promise.ClosedOn = DateTime.UtcNow; promise.Version++;
            item.PromiseAmount = null; item.PromiseDueOn = null; item.NextActionOn = DateTime.UtcNow;
            item.UpdatedBy = Actor(actor); item.UpdatedOn = DateTime.UtcNow; item.Version++;
            await AuditAndOutboxAsync(businessUnitId, "DunningCase", item.Id, item.Version,
                $"Promise{status}", actor, $"finance.promise-to-pay.{status.ToLowerInvariant()}",
                new { item.Id, PromiseId = promise.Id, promise.Status, promise.Version, CaseVersion = item.Version });
            await _context.SaveChangesAsync();
            return MapPromise(promise);
        });

    public async Task<IReadOnlyList<DunningCaseDto>> GetCasesAsync(
        long businessUnitId, long? customerId, string? status)
    {
        var query = _context.DunningCases.Include(x => x.Promises).Where(x => x.BusinessUnitId == businessUnitId);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var rows = await query.OrderBy(x => x.NextActionOn).ThenBy(x => x.Id).Take(500).ToListAsync();
        var result = new List<DunningCaseDto>(rows.Count);
        foreach (var row in rows) result.Add(await MapCaseAsync(row));
        return result;
    }

    public async Task<DunningNoticeDto> CreateNoticeAsync(
        long businessUnitId, string idempotencyKey, CreateDunningNoticeRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var requestHash = Hash(request);
        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.DunningNotices.Include(x => x.DeliveryAttempts).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return MapNotice(replay); }
            var item = await LockCaseAsync(request.DunningCaseId, businessUnitId);
            if (item.Status != DunningCaseStatuses.Open || item.CurrentExposure <= 0)
                throw new FinanceConflictException("A notice requires an open case with positive exposure.");
            if (await RefreshCaseExposureAsync(item, businessUnitId))
            {
                item.UpdatedBy = Actor(actor); item.UpdatedOn = DateTime.UtcNow; item.Version++;
            }
            if (item.CurrentExposure <= 0) throw new FinanceConflictException("The dunning case has no current exposure.");
            var policy = await _context.DunningPolicies.Include(x => x.Steps).SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == item.DunningPolicyId);
            if (policy.Status != "Active") throw new FinanceConflictException("The governing dunning policy is not active.");
            var nextStage = item.CurrentStage + 1;
            var step = policy.Steps.SingleOrDefault(x => x.Stage == nextStage)
                ?? throw new FinanceConflictException("The dunning case has reached its maximum stage.");
            var now = DateTime.UtcNow;
            var daysPastDue = (now.Date - item.OldestDueDate.Date).Days - policy.GraceDays;
            if (daysPastDue <= 0 || daysPastDue < step.MinimumDaysPastDue ||
                item.CurrentExposure < Math.Max(policy.MinimumOverdueAmount, step.MinimumAmount))
                throw new FinanceConflictException("The dunning case has not reached the next policy threshold.");
            if (item.NextActionOn > now) throw new FinanceConflictException("The next dunning action is not yet due.");
            var contact = await _context.FinanceCommunicationContacts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.FinanceCommunicationContactId)
                ?? throw new KeyNotFoundException("Communication contact not found.");
            if (contact.CustomerId != item.CustomerId || !contact.IsActive || !contact.IsVerified ||
                contact.Purpose != "Collections" || contact.EffectiveFrom > now || contact.EffectiveTo <= now)
                throw new FinanceConflictException("The selected collections contact is not currently valid.");
            if (!string.Equals(contact.Channel, step.Channel, StringComparison.Ordinal))
                throw new FinanceConflictException("The contact channel does not match the governed policy step.");
            var statement = await _context.CustomerStatements.SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == item.CustomerStatementId);
            var profile = await _context.CustomerCollectionProfiles.SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.CustomerId == item.CustomerId &&
                x.CurrencyId == item.CurrencyId);
            var blocked = await HasBlockingControlAsync(businessUnitId, item.CustomerId, item.CurrencyId, null, now);
            var snapshotHash = Hash(new { item.Id, item.CustomerStatementId, Stage = nextStage,
                item.CurrentExposure, ContactId = contact.Id, step.TemplateVersion });
            var artifact = await BuildNoticeArtifactAsync(item, statement, policy, step, profile.Locale, now);
            var notice = new DunningNotice
            {
                BusinessUnitId = businessUnitId, DunningCaseId = item.Id,
                CustomerStatementId = item.CustomerStatementId, FinanceCommunicationContactId = contact.Id,
                Stage = nextStage, SnapshotExposure = item.CurrentExposure, SnapshotHash = snapshotHash,
                TemplateVersion = step.TemplateVersion, Locale = artifact.Locale, Subject = artifact.Subject,
                ArtifactMediaType = artifact.MediaType, ArtifactContent = artifact.Content,
                ArtifactHash = artifact.Hash, IdempotencyKey = idempotencyKey,
                RequestHash = requestHash, CreatedBy = Actor(actor), CreatedOn = now
            };
            if (blocked)
            {
                notice.Status = DunningNoticeStatuses.Suppressed;
                notice.SuppressionReason = "An active collection control prohibits communication.";
            }
            _context.DunningNotices.Add(notice);
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "DunningNotice", notice.Id, notice.Version,
                notice.Status == DunningNoticeStatuses.Suppressed ? "Suppressed" : "DraftCreated", actor,
                notice.Status == DunningNoticeStatuses.Suppressed ? "finance.dunning-notice.suppressed" : "finance.dunning-notice.draft-created",
                new { notice.Id, notice.DunningCaseId, notice.Stage, notice.SnapshotExposure,
                    notice.SnapshotHash, notice.Status, notice.Version });
            await _context.SaveChangesAsync();
            return MapNotice(notice);
        });
    }

    public async Task<DunningNoticeDto> TransitionNoticeAsync(
        long businessUnitId, long noticeId, string action, DunningNoticeActionRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            var notice = await LockNoticeAsync(noticeId, businessUnitId);
            var normalizedAction = RequiredChoice(action, "notice action", "approve", "release", "retry", "cancel").ToLowerInvariant();
            if (notice.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The dunning notice changed; reload it.");
            var item = await LockCaseAsync(notice.DunningCaseId, businessUnitId);
            var policy = await _context.DunningPolicies.Include(x => x.Steps).SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == item.DunningPolicyId);
            var step = policy.Steps.Single(x => x.Stage == notice.Stage);
            var now = DateTime.UtcNow;
            if (normalizedAction == "approve")
            {
                if (notice.Status == DunningNoticeStatuses.Approved) return MapNotice(notice);
                if (notice.Status != DunningNoticeStatuses.Draft || !step.RequiresApproval)
                    throw new FinanceConflictException("This notice is not awaiting approval.");
                if (string.Equals(notice.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                    throw new FinanceConflictException("Notice approval requires an independent checker.");
                notice.Status = DunningNoticeStatuses.Approved; notice.ApprovedBy = Actor(actor); notice.ApprovedOn = now;
            }
            else if (normalizedAction is "release" or "retry")
            {
                if (normalizedAction == "release")
                {
                    if (notice.Status == DunningNoticeStatuses.Released) return MapNotice(notice);
                    var requiredStatus = step.RequiresApproval ? DunningNoticeStatuses.Approved : DunningNoticeStatuses.Draft;
                    if (notice.Status != requiredStatus)
                        throw new FinanceConflictException("The dunning notice is not ready for release.");
                }
                else
                {
                    if (notice.Status != DunningNoticeStatuses.Failed)
                        throw new FinanceConflictException("Only a failed notice can be retried.");
                    if (notice.DeliveryAttempts.Count >= step.MaximumAttempts)
                        throw new FinanceConflictException("The governed maximum delivery attempts has been reached.");
                }
                if (normalizedAction == "release" &&
                    (string.Equals(notice.CreatedBy, actor, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(notice.ApprovedBy, actor, StringComparison.OrdinalIgnoreCase)))
                    throw new FinanceConflictException("Notice release requires an operator independent from the maker and approver.");
                if (item.Status != DunningCaseStatuses.Open || policy.Status != "Active")
                    throw new FinanceConflictException("The case or policy no longer permits release.");
                await RefreshCaseExposureAsync(item, businessUnitId);
                if (item.CurrentExposure <= 0 || item.CurrentExposure != notice.SnapshotExposure)
                    throw new FinanceConflictException("Exposure changed after notice generation; create a fresh notice.");
                if (await HasBlockingControlAsync(businessUnitId, item.CustomerId, item.CurrencyId, null, now))
                    throw new FinanceConflictException("An active collection control prohibits notice release.");
                var profile = await _context.CustomerCollectionProfiles.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.CustomerId == item.CustomerId && x.CurrencyId == item.CurrencyId)
                    ?? throw new FinanceConflictException("The collection profile no longer exists.");
                if (!profile.AutomaticDeliveryAllowed)
                    throw new FinanceConflictException("The customer profile does not authorize electronic delivery.");
                if (IsQuietHour(now, profile.TimeZoneId, policy.QuietHoursStart, policy.QuietHoursEnd))
                    throw new FinanceConflictException("The customer is currently within governed quiet hours.");
                var contact = await _context.FinanceCommunicationContacts.SingleAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == notice.FinanceCommunicationContactId);
                if (!contact.IsActive || !contact.IsVerified || contact.EffectiveFrom > now || contact.EffectiveTo <= now)
                    throw new FinanceConflictException("The delivery contact is no longer valid.");
                notice.Status = DunningNoticeStatuses.Released; notice.ReleasedBy = Actor(actor); notice.ReleasedOn = now;
                notice.ProviderReference = null; notice.FailureCode = null;
                item.CurrentStage = notice.Stage; item.NextActionOn = now.AddDays(step.WaitDaysAfterPriorStage > 0
                    ? step.WaitDaysAfterPriorStage : policy.CadenceDays); item.UpdatedBy = Actor(actor); item.UpdatedOn = now; item.Version++;
            }
            else
            {
                if (notice.Status == DunningNoticeStatuses.Cancelled) return MapNotice(notice);
                if (notice.Status is DunningNoticeStatuses.Delivered or DunningNoticeStatuses.Cancelled)
                    throw new FinanceConflictException("A delivered or cancelled notice cannot be cancelled again.");
                notice.Status = DunningNoticeStatuses.Cancelled;
                notice.CancellationReason = RequiredText(request.Reason, "cancellation reason", 500, 20);
                notice.CancellationEvidenceReference = RequiredText(request.EvidenceReference, "cancellation evidence", 500, 8);
                notice.CancelledBy = Actor(actor); notice.CancelledOn = now;
            }
            notice.Version++;
            await AuditAndOutboxAsync(businessUnitId, "DunningNotice", notice.Id, notice.Version,
                normalizedAction, actor, $"finance.dunning-notice.{normalizedAction}",
                new { notice.Id, notice.DunningCaseId, notice.Stage, notice.Status,
                    notice.SnapshotHash, notice.Version, CaseVersion = item.Version });
            await _context.SaveChangesAsync();
            return MapNotice(notice);
        });

    public async Task<DunningNoticeDto> RecordDeliveryResultAsync(
        long businessUnitId, long noticeId, bool delivered, DunningDeliveryResultRequest request, string actor)
        => await InSerializableTransactionAsync(async () =>
        {
            VerifyDeliveryProviderSignature(businessUnitId, noticeId, delivered, request);
            if (request.ProviderEventId == Guid.Empty) throw new ArgumentException("Provider event ID is required.");
            var providerReference = RequiredText(request.ProviderReference, "provider reference", 100, 3);
            var signedEvidence = RequiredText(request.SignedEvidenceReference, "signed provider evidence", 500, 8);
            if (request.ProviderOccurredOn.Kind != DateTimeKind.Utc ||
                request.ProviderOccurredOn > DateTime.UtcNow.AddMinutes(5) ||
                request.ProviderOccurredOn < DateTime.UtcNow.AddDays(-7))
                throw new ArgumentException("The provider event timestamp must be recent UTC time.");
            var replay = await _context.DunningDeliveryAttempts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.ProviderEventId == request.ProviderEventId);
            if (replay is not null)
            {
                if (replay.DunningNoticeId != noticeId || replay.Status != (delivered ? "Delivered" : "Failed") ||
                    replay.ProviderReference != providerReference || replay.FailureCode != OptionalText(request.FailureCode, "failure code", 100) ||
                    replay.ProviderOccurredOn != request.ProviderOccurredOn || replay.SignedEvidenceReference != signedEvidence)
                    throw new FinanceConflictException("The provider event was already recorded differently.");
                var existingNotice = await _context.DunningNotices.Include(x => x.DeliveryAttempts).SingleAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == noticeId);
                return MapNotice(existingNotice);
            }
            var notice = await LockNoticeAsync(noticeId, businessUnitId);
            if (notice.Version != request.ExpectedVersion || notice.Status != DunningNoticeStatuses.Released)
                throw new FinanceConflictException("Only the current released notice can receive a delivery result.");
            var failureCode = delivered ? null : RequiredText(request.FailureCode, "failure code", 100, 2);
            var contact = await _context.FinanceCommunicationContacts.SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == notice.FinanceCommunicationContactId);
            var attemptNumber = await _context.DunningDeliveryAttempts.CountAsync(x =>
                x.BusinessUnitId == businessUnitId && x.DunningNoticeId == notice.Id) + 1;
            var attempt = new DunningDeliveryAttempt
            {
                BusinessUnitId = businessUnitId, DunningNoticeId = notice.Id,
                ProviderEventId = request.ProviderEventId, AttemptNumber = attemptNumber,
                Status = delivered ? "Delivered" : "Failed", MaskedDestination = contact.MaskedDestination,
                ArtifactHash = notice.ArtifactHash, TemplateVersion = notice.TemplateVersion,
                ProviderReference = providerReference, FailureCode = failureCode,
                ProviderOccurredOn = request.ProviderOccurredOn, SignedEvidenceReference = signedEvidence,
                ProviderSignature = request.ProviderSignature.Trim().ToLowerInvariant(),
                OccurredOn = DateTime.UtcNow, RecordedBy = Actor(actor)
            };
            _context.DunningDeliveryAttempts.Add(attempt);
            notice.Status = delivered ? DunningNoticeStatuses.Delivered : DunningNoticeStatuses.Failed;
            notice.ProviderReference = providerReference; notice.FailureCode = failureCode;
            notice.DeliveryUpdatedBy = Actor(actor); notice.DeliveryUpdatedOn = DateTime.UtcNow; notice.Version++;
            await _context.SaveChangesAsync();
            await AuditAndOutboxAsync(businessUnitId, "DunningNotice", notice.Id, notice.Version,
                delivered ? "Delivered" : "DeliveryFailed", actor,
                delivered ? "finance.dunning-notice.delivered" : "finance.dunning-notice.delivery-failed",
                new { notice.Id, notice.DunningCaseId, notice.Stage, request.ProviderEventId,
                    ProviderReference = providerReference, FailureCode = failureCode, notice.Version });
            await _context.SaveChangesAsync();
            return MapNotice(notice);
        });

    public async Task<IReadOnlyList<DunningNoticeDto>> GetNoticesAsync(
        long businessUnitId, long? caseId, string? status)
    {
        var query = _context.DunningNotices.Include(x => x.DeliveryAttempts).Where(x => x.BusinessUnitId == businessUnitId);
        if (caseId.HasValue) query = query.Where(x => x.DunningCaseId == caseId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return (await query.OrderByDescending(x => x.CreatedOn).Take(500).ToListAsync()).Select(MapNotice).ToArray();
    }

    public async Task<DunningRunDto> RunDunningAsync(
        long businessUnitId, string idempotencyKey, CreateDunningRunRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        if (request.CutoffAt > DateTime.UtcNow.AddMinutes(1)) throw new ArgumentException("Dunning cutoff cannot be in the future.");
        var requestHash = Hash(request);
        var start = await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.DunningRuns.Include(x => x.Decisions).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                EnsureReplay(replay.RequestHash, requestHash);
                return (Run: replay, Created: false);
            }
            var policy = await _context.DunningPolicies.Include(x => x.Steps).SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == request.DunningPolicyId)
                ?? throw new KeyNotFoundException("Dunning policy not found.");
            if (policy.Status != "Active") throw new FinanceConflictException("Only an active dunning policy can be run.");
            var now = DateTime.UtcNow;
            var run = new DunningRun
            {
                BusinessUnitId = businessUnitId, DunningPolicyId = policy.Id, CutoffAt = request.CutoffAt,
                Status = "Pending", IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = now
            };
            _context.DunningRuns.Add(run);
            await _context.SaveChangesAsync();
            return (Run: run, Created: true);
        });
        if (start.Run.Status is "Completed" or "Failed") return MapRun(start.Run);

        _context.ChangeTracker.Clear();
        var claimToken = await InSerializableTransactionAsync(async () =>
        {
            var claim = await LockAsync(_context.DunningRuns, "DunningRuns", start.Run.Id, businessUnitId);
            var claimNow = DateTime.UtcNow;
            if (claim.Status == "Running" && claim.LeaseUntil > claimNow) return (Guid?)null;
            if (claim.Status != "Pending" && (claim.Status != "Running" || claim.LeaseUntil >= claimNow))
                return (Guid?)null;
            claim.Status = "Running";
            claim.LeaseOwner = Actor(actor);
            claim.LeaseToken = Guid.NewGuid();
            claim.LeaseUntil = claimNow.AddMinutes(5);
            claim.Version++;
            await _context.SaveChangesAsync();
            return claim.LeaseToken;
        });
        if (!claimToken.HasValue)
        {
            _context.ChangeTracker.Clear();
            var inProgress = await _context.DunningRuns.Include(x => x.Decisions).SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.Id == start.Run.Id);
            return MapRun(inProgress);
        }

        try
        {
            _context.ChangeTracker.Clear();
            var completedProfileIds = _context.DunningRunDecisions.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.DunningRunId == start.Run.Id &&
                    x.CustomerCollectionProfileId != null)
                .Select(x => x.CustomerCollectionProfileId!.Value);
            var profileIds = await _context.CustomerCollectionProfiles.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.DunningPolicyId == request.DunningPolicyId)
                .Where(x => !completedProfileIds.Contains(x.Id))
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync();
            foreach (var profileId in profileIds)
            {
                try
                {
                    _context.ChangeTracker.Clear();
                    await InSerializableTransactionAsync(async () =>
                    {
                        var run = await LockAsync(_context.DunningRuns, "DunningRuns", start.Run.Id, businessUnitId);
                        EnsureOwnedRunLease(run, claimToken.Value);
                        var policy = await _context.DunningPolicies.Include(x => x.Steps).SingleAsync(x =>
                            x.BusinessUnitId == businessUnitId && x.Id == run.DunningPolicyId && x.Status == "Active");
                        var profile = await _context.CustomerCollectionProfiles.SingleAsync(x =>
                            x.BusinessUnitId == businessUnitId && x.Id == profileId && x.DunningPolicyId == policy.Id);
                        run.CandidateCount++;
                        await ProcessDunningCandidateAsync(run, profile, policy, request, idempotencyKey, actor);
                        run.LeaseUntil = DateTime.UtcNow.AddMinutes(5);
                        run.Version++;
                        await _context.SaveChangesAsync();
                        return true;
                    });
                }
                catch (Exception candidateException) when (candidateException is not OperationCanceledException)
                {
                    _context.ChangeTracker.Clear();
                    await InSerializableTransactionAsync(async () =>
                    {
                        var run = await LockAsync(_context.DunningRuns, "DunningRuns", start.Run.Id, businessUnitId);
                        EnsureOwnedRunLease(run, claimToken.Value);
                        var profile = await _context.CustomerCollectionProfiles.SingleAsync(x =>
                            x.BusinessUnitId == businessUnitId && x.Id == profileId);
                        run.CandidateCount++;
                        run.FailedCount++;
                        var diagnostic = Hash(new { Type = candidateException.GetType().FullName,
                            Message = candidateException.Message });
                        AddRunDecision(run, profile, null, null, null, "Failed",
                            "CANDIDATE_PROCESSING_FAILED", diagnostic);
                        run.LeaseUntil = DateTime.UtcNow.AddMinutes(5);
                        run.Version++;
                        await _context.SaveChangesAsync();
                        return true;
                    });
                }
            }
            _context.ChangeTracker.Clear();
            return await InSerializableTransactionAsync(async () =>
            {
                var run = await LockAsync(_context.DunningRuns, "DunningRuns", start.Run.Id, businessUnitId);
                EnsureOwnedRunLease(run, claimToken.Value);
                run.Status = "Completed"; run.CompletedOn = DateTime.UtcNow;
                run.CompletionEvidenceReference = $"dunning-run:{run.Id}:{Hash(new { run.CandidateCount, run.NoticeCount, run.SuppressedCount, run.FailedCount })}";
                run.LeaseOwner = null; run.LeaseToken = null; run.LeaseUntil = null; run.Version++;
                await AuditAndOutboxAsync(businessUnitId, "DunningRun", run.Id, run.Version,
                    "Completed", actor, "finance.dunning-run.completed",
                    new { run.Id, run.DunningPolicyId, run.CutoffAt, run.CandidateCount,
                        run.NoticeCount, run.SuppressedCount, run.FailedCount, run.Version });
                await _context.SaveChangesAsync();
                await _context.Entry(run).Collection(x => x.Decisions).LoadAsync();
                return MapRun(run);
            });
        }
        catch (Exception exception)
        {
            _context.ChangeTracker.Clear();
            try
            {
                await InSerializableTransactionAsync(async () =>
                {
                    var failed = await LockAsync(_context.DunningRuns, "DunningRuns", start.Run.Id, businessUnitId);
                    if (failed.Status == "Running" && failed.LeaseToken == claimToken &&
                        failed.LeaseUntil > DateTime.UtcNow)
                    {
                        failed.Status = "Failed";
                        failed.FailedCount++;
                        failed.FailureReason = $"Processing failed: {exception.GetType().Name}";
                        failed.FailureEvidenceReference = $"dunning-run-failure:{failed.Id}:{Hash(new { Type = exception.GetType().FullName })}";
                        failed.CompletedOn = DateTime.UtcNow;
                        failed.LeaseOwner = null; failed.LeaseToken = null; failed.LeaseUntil = null; failed.Version++;
                        await _context.SaveChangesAsync();
                    }
                    return true;
                });
            }
            catch (Exception evidenceException)
            {
                throw new AggregateException("Dunning failed and its failure evidence could not be persisted.", exception, evidenceException);
            }
            throw;
        }
    }

    private static void EnsureOwnedRunLease(DunningRun run, Guid claimToken)
    {
        if (run.Status != "Running" || run.LeaseToken != claimToken || run.LeaseUntil <= DateTime.UtcNow)
            throw new FinanceConflictException("The dunning run lease is no longer owned by this worker.");
    }

    private async Task ProcessDunningCandidateAsync(
        DunningRun run, CustomerCollectionProfile profile, DunningPolicy policy,
        CreateDunningRunRequest request, string idempotencyKey, string actor)
    {
        try
        {
            _ = RequiredToken(profile.Locale, "notice locale", 20);
            EnsureTimeZone(profile.TimeZoneId);
        }
        catch (ArgumentException)
        {
            run.FailedCount++;
            AddRunDecision(run, profile, null, null, null, "Failed", "INVALID_PROFILE_CONFIGURATION");
            return;
        }

        var statement = await _context.CustomerStatements.Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == run.BusinessUnitId && x.CustomerId == profile.CustomerId &&
                x.CurrencyId == profile.CurrencyId && x.Status == CustomerStatementStatuses.Finalized &&
                x.CutoffAt <= request.CutoffAt)
            .OrderByDescending(x => x.CutoffAt).ThenByDescending(x => x.Revision).FirstOrDefaultAsync();
        if (statement is null)
        {
            AddRunDecision(run, profile, null, null, null, "Skipped", "NO_FINAL_STATEMENT");
            return;
        }

        var current = await BuildStatementSnapshotAsync(run.BusinessUnitId, profile.CustomerId,
            profile.CurrencyId, statement.PeriodStart, request.CutoffAt);
        if (current.NetCustomerPosition < policy.MinimumOverdueAmount ||
            !current.OldestOutstandingDueDate.HasValue ||
            current.OldestOutstandingDueDate.Value.Date.AddDays(policy.GraceDays) >= request.CutoffAt.Date)
        {
            AddRunDecision(run, profile, statement, null, null, "Skipped", "NOT_ELIGIBLY_OVERDUE");
            return;
        }

        var blocked = profile.IsOnHold || await HasBlockingControlAsync(run.BusinessUnitId, profile.CustomerId,
            profile.CurrencyId, null, request.CutoffAt);
        if (blocked)
        {
            run.SuppressedCount++;
            AddRunDecision(run, profile, statement, null, null, "Suppressed", "COLLECTION_CONTROL");
            return;
        }
        if (!profile.FinanceCommunicationContactId.HasValue)
        {
            run.SuppressedCount++;
            AddRunDecision(run, profile, statement, null, null, "Suppressed", "CONTACT_NOT_CONFIGURED");
            return;
        }

        var item = await _context.DunningCases.SingleOrDefaultAsync(x => x.BusinessUnitId == run.BusinessUnitId &&
            x.CustomerId == profile.CustomerId && x.CurrencyId == profile.CurrencyId &&
            (x.Status == DunningCaseStatuses.Open || x.Status == DunningCaseStatuses.Held ||
             x.Status == DunningCaseStatuses.Disputed));
        var now = DateTime.UtcNow;
        if (item is null)
        {
            var oldestDue = current.OldestOutstandingDueDate.Value;
            item = new DunningCase
            {
                BusinessUnitId = run.BusinessUnitId, CustomerId = profile.CustomerId,
                CurrencyId = profile.CurrencyId, DunningPolicyId = policy.Id,
                CustomerStatementId = statement.Id, ExposureAtOpen = current.NetCustomerPosition,
                CurrentExposure = current.NetCustomerPosition, OldestDueDate = oldestDue,
                NextActionOn = oldestDue.AddDays(policy.GraceDays), AssignedTo = profile.Collector,
                IdempotencyKey = DerivedKey(idempotencyKey, $"case:{profile.Id}"),
                RequestHash = Hash(new { RunId = run.Id, ProfileId = profile.Id, StatementId = statement.Id }),
                CreatedBy = Actor(actor), CreatedOn = now
            };
            _context.DunningCases.Add(item);
            await _context.SaveChangesAsync();
        }
        if (item.Status != DunningCaseStatuses.Open || item.NextActionOn > request.CutoffAt)
        {
            AddRunDecision(run, profile, statement, item, null, "Skipped", "CASE_NOT_ACTIONABLE");
            return;
        }
        if (await RefreshCaseExposureAsync(item, run.BusinessUnitId, request.CutoffAt))
        {
            item.UpdatedBy = Actor(actor); item.UpdatedOn = now; item.Version++;
        }

        var nextStage = item.CurrentStage + 1;
        var step = policy.Steps.SingleOrDefault(x => x.Stage == nextStage);
        if (step is null)
        {
            AddRunDecision(run, profile, statement, item, null, "Skipped", "MAXIMUM_STAGE_REACHED");
            return;
        }
        var daysPastDue = (request.CutoffAt.Date - item.OldestDueDate.Date).Days - policy.GraceDays;
        if (daysPastDue <= 0 || daysPastDue < step.MinimumDaysPastDue ||
            item.CurrentExposure < Math.Max(policy.MinimumOverdueAmount, step.MinimumAmount))
        {
            AddRunDecision(run, profile, statement, item, null, "Skipped", "POLICY_THRESHOLD_NOT_REACHED");
            return;
        }

        var contact = await _context.FinanceCommunicationContacts.SingleOrDefaultAsync(x =>
            x.BusinessUnitId == run.BusinessUnitId && x.Id == profile.FinanceCommunicationContactId.Value &&
            x.CustomerId == profile.CustomerId && x.IsActive && x.IsVerified && x.Purpose == "Collections" &&
            x.Channel == step.Channel && x.EffectiveFrom <= request.CutoffAt &&
            (x.EffectiveTo == null || x.EffectiveTo > request.CutoffAt));
        if (contact is null)
        {
            run.SuppressedCount++;
            AddRunDecision(run, profile, statement, item, null, "Suppressed", "CONTACT_NOT_ELIGIBLE");
            return;
        }

        var snapshotHash = Hash(new { item.Id, Stage = nextStage, item.CurrentExposure,
            ContactId = contact.Id, step.TemplateVersion });
        if (await _context.DunningNotices.AnyAsync(x => x.BusinessUnitId == run.BusinessUnitId &&
            x.DunningCaseId == item.Id && x.Stage == nextStage && x.SnapshotHash == snapshotHash))
        {
            AddRunDecision(run, profile, statement, item, null, "Skipped", "NOTICE_ALREADY_EXISTS");
            return;
        }

        var artifact = await BuildNoticeArtifactAsync(item, statement, policy, step, profile.Locale, now);
        var notice = new DunningNotice
        {
            BusinessUnitId = run.BusinessUnitId, DunningCaseId = item.Id,
            CustomerStatementId = item.CustomerStatementId, FinanceCommunicationContactId = contact.Id,
            Stage = nextStage, SnapshotExposure = item.CurrentExposure, SnapshotHash = snapshotHash,
            TemplateVersion = step.TemplateVersion, Locale = artifact.Locale, Subject = artifact.Subject,
            ArtifactMediaType = artifact.MediaType, ArtifactContent = artifact.Content,
            ArtifactHash = artifact.Hash,
            IdempotencyKey = DerivedKey(idempotencyKey, $"notice:{item.Id}:{nextStage}"),
            RequestHash = Hash(new { CaseId = item.Id, ContactId = contact.Id, nextStage }),
            CreatedBy = Actor(actor), CreatedOn = now
        };
        _context.DunningNotices.Add(notice);
        await _context.SaveChangesAsync();
        AddRunDecision(run, profile, statement, item, notice, "NoticeCreated", "POLICY_CANDIDATE_CREATED");
        run.NoticeCount++;
    }

    private void AddRunDecision(DunningRun run, CustomerCollectionProfile profile,
        CustomerStatement? statement, DunningCase? item, DunningNotice? notice,
        string outcome, string reasonCode, string? diagnosticFingerprint = null)
        => _context.DunningRunDecisions.Add(new DunningRunDecision
        {
            BusinessUnitId = run.BusinessUnitId, DunningRunId = run.Id,
            CustomerCollectionProfileId = profile.Id,
            CustomerId = profile.CustomerId, CurrencyId = profile.CurrencyId,
            CustomerStatementId = statement?.Id, DunningCaseId = item?.Id,
            DunningNoticeId = notice?.Id, Outcome = outcome, ReasonCode = reasonCode,
            EvidenceHash = Hash(new { RunId = run.Id, ProfileId = profile.Id, StatementId = statement?.Id,
                CaseId = item?.Id, NoticeId = notice?.Id, outcome, reasonCode, diagnosticFingerprint }),
            CreatedOn = DateTime.UtcNow
        });

    public async Task<IReadOnlyList<DunningRunDto>> GetRunsAsync(long businessUnitId)
        => (await _context.DunningRuns.Include(x => x.Decisions).Where(x => x.BusinessUnitId == businessUnitId)
            .OrderByDescending(x => x.CreatedOn).Take(200).ToListAsync()).Select(MapRun).ToArray();

    private async Task<StatementSnapshot> BuildStatementSnapshotAsync(
        long businessUnitId, long customerId, long? currencyId, DateTime periodStart, DateTime cutoffAt)
    {
        var documents = await _context.ReceivableDocuments.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CustomerId == customerId &&
                x.CurrencyId == currencyId && x.IssuedOn != null && x.IssuedOn <= cutoffAt &&
                (x.VoidedOn == null || x.VoidedOn > cutoffAt) && x.DocumentDate <= cutoffAt)
            .OrderBy(x => x.DocumentDate).ThenBy(x => x.Id).ToListAsync();
        var documentIds = documents.Select(x => x.Id).ToArray();
        var allocations = await _context.PaymentAllocations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && documentIds.Contains(x.ReceivableDocumentId) &&
                x.Payment.PaymentDate <= cutoffAt && (x.Payment.ReversedOn == null || x.Payment.ReversedOn > cutoffAt))
            .Select(x => new { x.Id, x.ReceivableDocumentId, x.CustomerPaymentId, x.Amount,
                PaymentVersion = x.Payment.Version, x.Payment.PaymentDate }).ToListAsync();
        var writeOffs = await _context.WriteOffAllocations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && documentIds.Contains(x.ReceivableDocumentId) &&
                x.WriteOff.ApprovedOn != null && x.WriteOff.ApprovedOn <= cutoffAt &&
                x.WriteOff.AccountingDate <= cutoffAt && (x.WriteOff.ReversedOn == null || x.WriteOff.ReversedOn > cutoffAt))
            .Select(x => new { x.Id, x.ReceivableDocumentId, x.ReceivableWriteOffId, x.Amount,
                WriteOffVersion = x.WriteOff.Version, x.WriteOff.AccountingDate,
                x.WriteOff.CommercialCaseId }).ToListAsync();
        var payments = await _context.CustomerPayments.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CustomerId == customerId &&
                x.CurrencyId == currencyId && x.PaymentDate <= cutoffAt &&
                (x.ReversedOn == null || x.ReversedOn > cutoffAt))
            .OrderBy(x => x.PaymentDate).ThenBy(x => x.Id).ToListAsync();
        var refunds = await _context.CustomerRefunds.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CustomerId == customerId &&
                x.CurrencyId == currencyId && x.ReleasedOn != null && x.ReleasedOn <= cutoffAt &&
                (x.ReversedOn == null || x.ReversedOn > cutoffAt))
            .OrderBy(x => x.ReleasedOn).ThenBy(x => x.Id).ToListAsync();
        var events = new List<StatementEvent>();
        foreach (var document in documents)
        {
            var debit = document.DocumentType is ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote
                ? Round(document.TotalAmount) : 0m;
            var credit = document.DocumentType == ReceivableDocumentTypes.CreditNote ? Round(document.TotalAmount) : 0m;
            var applied = Round(allocations.Where(x => x.ReceivableDocumentId == document.Id).Sum(x => x.Amount) +
                writeOffs.Where(x => x.ReceivableDocumentId == document.Id).Sum(x => x.Amount));
            var gross = Round(debit - credit);
            var outstanding = gross > 0 ? Math.Max(0, Round(gross - applied)) : 0;
            events.Add(new StatementEvent(document.DocumentDate, document.DocumentType, document.Id, document.Version,
                document.DocumentNumber ?? $"Document #{document.Id}", document.CommercialCaseId,
                document.DueDate, document.DocumentType, debit, credit, applied, outstanding));
        }
        foreach (var payment in payments)
            events.Add(new StatementEvent(payment.PaymentDate, "Payment", payment.Id, payment.Version,
                payment.ReceiptNumber, payment.CommercialCaseId, null, "Customer payment", 0, Round(payment.Amount),
                Round(allocations.Where(x => x.CustomerPaymentId == payment.Id).Sum(x => x.Amount)), 0));
        foreach (var writeOff in writeOffs)
            events.Add(new StatementEvent(writeOff.AccountingDate, "WriteOff", writeOff.ReceivableWriteOffId,
                writeOff.WriteOffVersion, $"Write-off #{writeOff.ReceivableWriteOffId}", writeOff.CommercialCaseId, null,
                "Receivable write-off", 0, Round(writeOff.Amount), Round(writeOff.Amount), 0));
        foreach (var refund in refunds)
            events.Add(new StatementEvent(refund.ReleasedOn!.Value, "Refund", refund.Id, refund.Version,
                refund.RefundNumber ?? $"Refund #{refund.Id}", refund.CommercialCaseId, null,
                "Customer refund", Round(refund.Amount), 0, 0, 0));
        events = events.OrderBy(x => x.ActivityDate).ThenBy(x => x.SourceType).ThenBy(x => x.SourceId).ToList();
        var opening = Round(events.Where(x => x.ActivityDate < periodStart).Sum(x => x.DebitAmount - x.CreditAmount));
        var periodEvents = events.Where(x => x.ActivityDate >= periodStart).ToList();
        var running = opening;
        var lines = new List<SnapshotLine>(periodEvents.Count);
        var aging = new AgingTotals();
        var sequence = 0;
        foreach (var row in periodEvents)
        {
            running = Round(running + row.DebitAmount - row.CreditAmount);
            var bucket = AgingBucket(row.DueDate, cutoffAt, row.OutstandingAmount);
            lines.Add(new SnapshotLine(++sequence, row.SourceType, row.SourceId, row.SourceVersion,
                row.SourceNumber, row.CommercialCaseId, row.ActivityDate, row.DueDate, row.Description,
                row.DebitAmount, row.CreditAmount, row.AppliedAmount, row.OutstandingAmount, bucket, running));
        }
        var debitTotal = Round(periodEvents.Sum(x => x.DebitAmount));
        var creditTotal = Round(periodEvents.Sum(x => x.CreditAmount));
        var closing = Round(opening + debitTotal - creditTotal);
        var allocatedPayment = Round(allocations.Sum(x => x.Amount));
        var refunded = Round(refunds.Sum(x => x.Amount));
        var unapplied = Math.Max(0, Round(payments.Sum(x => x.Amount) - allocatedPayment - refunded));
        foreach (var document in events.Where(x => x.SourceType is ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote))
            aging.Add(AgingBucket(document.DueDate, cutoffAt, document.OutstandingAmount), document.OutstandingAmount);
        var oldestOutstandingDueDate = events
            .Where(x => (x.SourceType is ReceivableDocumentTypes.Invoice or ReceivableDocumentTypes.DebitNote) &&
                x.OutstandingAmount > 0 && x.DueDate.HasValue)
            .Select(x => x.DueDate!.Value).OrderBy(x => x).Cast<DateTime?>().FirstOrDefault();
        var fingerprint = Hash(new
        {
            Documents = documents.Select(x => new { x.Id, x.Version, x.Status, x.TotalAmount }),
            Allocations = allocations, WriteOffs = writeOffs,
            Payments = payments.Select(x => new { x.Id, x.Version, x.Status, x.Amount }),
            Refunds = refunds.Select(x => new { x.Id, x.Version, x.Status, x.Amount })
        });
        return new StatementSnapshot(opening, debitTotal, creditTotal, unapplied, closing,
            closing, aging, oldestOutstandingDueDate, fingerprint, lines);
    }

    private async Task<bool> RefreshCaseExposureAsync(
        DunningCase item, long businessUnitId, DateTime? cutoffAt = null)
    {
        var statement = await _context.CustomerStatements.Include(x => x.Lines).SingleAsync(x =>
            x.BusinessUnitId == businessUnitId && x.Id == item.CustomerStatementId);
        var current = await BuildStatementSnapshotAsync(businessUnitId, item.CustomerId, item.CurrencyId,
            statement.PeriodStart, cutoffAt ?? DateTime.UtcNow);
        var exposure = Math.Max(0, current.NetCustomerPosition);
        if (item.CurrentExposure == exposure) return false;
        item.CurrentExposure = exposure;
        return true;
    }

    private async Task<bool> HasBlockingControlAsync(
        long businessUnitId, long customerId, long? currencyId, long? documentId, DateTime at)
        => await _context.CollectionControls.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
            x.CustomerId == customerId && x.Status == "Active" && x.EffectiveFrom <= at &&
            (x.ExpiresOn == null || x.ExpiresOn > at) && (x.CurrencyId == null || x.CurrencyId == currencyId) &&
            (documentId == null || x.ReceivableDocumentId == null || x.ReceivableDocumentId == documentId));

    private async Task<decimal> GetDocumentOutstandingAsync(long businessUnitId, long documentId, DateTime cutoff)
    {
        var document = await _context.ReceivableDocuments.SingleAsync(x =>
            x.BusinessUnitId == businessUnitId && x.Id == documentId);
        var applied = await _context.PaymentAllocations.Where(x => x.BusinessUnitId == businessUnitId &&
            x.ReceivableDocumentId == documentId && x.Payment.Status == CustomerPaymentStatuses.Posted &&
            x.Payment.PaymentDate <= cutoff).SumAsync(x => (decimal?)x.Amount) ?? 0;
        var writtenOff = await _context.WriteOffAllocations.Where(x => x.BusinessUnitId == businessUnitId &&
            x.ReceivableDocumentId == documentId && x.WriteOff.Status == FinanceExceptionStatuses.Posted &&
            x.WriteOff.AccountingDate <= cutoff).SumAsync(x => (decimal?)x.Amount) ?? 0;
        return Math.Max(0, Round(document.TotalAmount - applied - writtenOff));
    }

    private async Task<T> LockAsync<T>(DbSet<T> set, string table, long id, long businessUnitId) where T : class
    {
        if (_context.Database.IsNpgsql())
        {
            var sql = table switch
            {
                "FinanceCommunicationContacts" => "SELECT * FROM public.\"FinanceCommunicationContacts\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "CustomerStatements" => "SELECT * FROM public.\"CustomerStatements\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "DunningPolicies" => "SELECT * FROM public.\"DunningPolicies\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "CollectionControls" => "SELECT * FROM public.\"CollectionControls\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "DunningCases" => "SELECT * FROM public.\"DunningCases\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "PromisesToPay" => "SELECT * FROM public.\"PromisesToPay\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "DunningNotices" => "SELECT * FROM public.\"DunningNotices\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                "DunningRuns" => "SELECT * FROM public.\"DunningRuns\" WHERE \"BusinessUnitId\" = {0} AND \"Id\" = {1} FOR UPDATE",
                _ => throw new InvalidOperationException("Unsupported governed financial record type.")
            };
            return await set.FromSqlRaw(sql, businessUnitId, id)
                .SingleOrDefaultAsync() ?? throw new KeyNotFoundException("Financial record not found.");
        }
        var entity = await set.SingleOrDefaultAsync(x => EF.Property<long>(x, "BusinessUnitId") == businessUnitId &&
            EF.Property<long>(x, "Id") == id);
        return entity ?? throw new KeyNotFoundException("Financial record not found.");
    }

    private Task<CustomerStatement> LockStatementAsync(long id, long businessUnitId)
        => LockAsync(_context.CustomerStatements, "CustomerStatements", id, businessUnitId);

    private async Task<DunningPolicy> LockPolicyAsync(long id, long businessUnitId)
    {
        var policy = await LockAsync(_context.DunningPolicies, "DunningPolicies", id, businessUnitId);
        await _context.Entry(policy).Collection(x => x.Steps).LoadAsync();
        return policy;
    }

    private async Task<DunningCase> LockCaseAsync(long id, long businessUnitId)
    {
        var item = await LockAsync(_context.DunningCases, "DunningCases", id, businessUnitId);
        await _context.Entry(item).Collection(x => x.Promises).LoadAsync();
        return item;
    }

    private async Task<DunningNotice> LockNoticeAsync(long id, long businessUnitId)
    {
        var notice = await LockAsync(_context.DunningNotices, "DunningNotices", id, businessUnitId);
        await _context.Entry(notice).Collection(x => x.DeliveryAttempts).LoadAsync();
        return notice;
    }

    private async Task EnsureCustomerAsync(long businessUnitId, long customerId)
    {
        if (!await _context.Customers.AnyAsync(x => x.Id == customerId && (x.Buid == businessUnitId || x.Buid == null)))
            throw new KeyNotFoundException("Customer not found.");
    }

    private async Task<long> AllocateNumberAsync(long businessUnitId, string documentType, int fiscalYear)
    {
        var counter = await _context.LegalDocumentCounters.SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.DocumentType == documentType && x.FiscalYear == fiscalYear);
        if (counter is null)
        {
            counter = new LegalDocumentCounter { BusinessUnitId = businessUnitId, DocumentType = documentType,
                FiscalYear = fiscalYear, NextNumber = 2 };
            _context.LegalDocumentCounters.Add(counter);
            await _context.SaveChangesAsync();
            return 1;
        }
        var number = counter.NextNumber;
        counter.NextNumber++;
        return number;
    }

    private async Task AuditAndOutboxAsync(long businessUnitId, string aggregateType, long aggregateId,
        long aggregateVersion, string action, string actor, string eventType, object detail)
    {
        // PostgreSQL owns finance evidence through definer triggers because the runtime
        // role cannot mutate the append-only audit/outbox tables directly.
        if (_context.Database.IsNpgsql()) return;
        var now = DateTime.UtcNow;
        _context.CommercialFinanceAudits.Add(new CommercialFinanceAudit
        {
            BusinessUnitId = businessUnitId, AggregateType = aggregateType, AggregateId = aggregateId,
            Action = action, Actor = Actor(actor), OccurredOn = now, DetailJson = JsonSerializer.Serialize(detail)
        });
        _context.FinanceOutboxMessages.Add(new FinanceOutboxMessage
        {
            BusinessUnitId = businessUnitId, AggregateType = aggregateType, AggregateId = aggregateId,
            AggregateVersion = aggregateVersion, EventType = eventType,
            Payload = JsonSerializer.Serialize(detail), SchemaVersion = 1,
            OccurredOn = now, AvailableOn = now
        });
        await Task.CompletedTask;
    }

    private async Task<CustomerStatementDto> MapStatementAsync(CustomerStatement statement)
    {
        if (!_context.Entry(statement).Collection(x => x.Lines).IsLoaded)
            await _context.Entry(statement).Collection(x => x.Lines).LoadAsync();
        return new CustomerStatementDto(statement.Id, statement.CustomerId, statement.CurrencyId,
            await CurrencyCodeAsync(statement.CurrencyId), statement.SupersedesStatementId,
            statement.StatementNumber, statement.Status, statement.PeriodStart, statement.CutoffAt,
            statement.CapturedOn, statement.Revision, statement.OpeningBalance, statement.DebitTotal,
            statement.CreditTotal, statement.UnappliedCash, statement.ClosingBalance,
            statement.NetCustomerPosition, statement.AgingCurrent, statement.Aging1To30,
            statement.Aging31To60, statement.Aging61To90, statement.AgingOver90,
            statement.SnapshotHash, statement.ArtifactHash, statement.ArtifactReference,
            statement.GeneratorVersion, statement.TemplateVersion, statement.IssuerNameSnapshot,
            statement.CustomerNameSnapshot, statement.BillingAddressSnapshot, statement.Version,
            statement.CreatedBy, statement.CreatedOn, statement.FinalizedBy, statement.FinalizedOn,
            statement.CancelledBy, statement.CancelledOn, statement.CancellationReason,
            statement.Lines.OrderBy(x => x.Sequence).Select(x => new CustomerStatementLineDto(
                x.Sequence, x.SourceType, x.SourceNumber, x.CommercialCaseId, x.ActivityDate,
                x.DueDate, x.Description, x.DebitAmount, x.CreditAmount, x.AppliedAmount,
                x.OutstandingAmount, x.AgingBucket, x.RunningBalance)).ToArray());
    }

    private static FinanceCommunicationContactDto MapContact(FinanceCommunicationContact x)
        => new(x.Id, x.CustomerId, x.Purpose, x.Channel, x.MaskedDestination, x.IsVerified,
            x.IsActive, x.EffectiveFrom, x.EffectiveTo, x.Version, x.CreatedOn, x.DeactivatedOn);

    private static DunningPolicyDto MapPolicy(DunningPolicy x)
        => new(x.Id, x.PolicyVersion, x.Name, x.Status, x.JurisdictionCode, x.TimeZoneId,
            x.GraceDays, x.CadenceDays, x.MaximumStage, x.MinimumOverdueAmount,
            x.QuietHoursStart, x.QuietHoursEnd, x.TemplateVersion, x.Version, x.CreatedBy,
            x.CreatedOn, x.ApprovedBy, x.ApprovedOn, x.RetiredBy, x.RetiredOn,
            x.Steps.OrderBy(s => s.Stage).Select(s => new DunningPolicyStepDto(s.Id, s.Stage,
                s.MinimumDaysPastDue, s.MinimumAmount, s.WaitDaysAfterPriorStage, s.Channel,
                s.TemplateVersion, s.RequiresApproval, s.EscalationRole, s.MaximumAttempts)).ToArray(),
            x.ActivatedBy, x.ActivatedOn);

    private static CustomerCollectionProfileDto MapProfile(CustomerCollectionProfile x)
        => new(x.Id, x.CustomerId, x.CurrencyId, x.DunningPolicyId,
            x.FinanceCommunicationContactId, x.Locale, x.TimeZoneId, x.Collector,
            x.AutomaticDeliveryAllowed, x.IsOnHold, x.HoldReason, x.Version);

    private static CollectionControlDto MapControl(CollectionControl x)
        => new(x.Id, x.CustomerId, x.CurrencyId, x.ReceivableDocumentId, x.ControlType,
            x.Status, x.DisputedAmount, x.ReasonCode, x.Reason, x.EvidenceReference,
            x.EffectiveFrom, x.ReviewOn, x.ExpiresOn, x.Version, x.CreatedBy, x.CreatedOn,
            x.ResolvedBy, x.ResolvedOn, x.ResolutionReason);

    private async Task<DunningCaseDto> MapCaseAsync(DunningCase x)
    {
        if (!_context.Entry(x).Collection(y => y.Promises).IsLoaded)
            await _context.Entry(x).Collection(y => y.Promises).LoadAsync();
        return new DunningCaseDto(x.Id, x.CustomerId, x.CurrencyId, await CurrencyCodeAsync(x.CurrencyId),
            x.DunningPolicyId, x.CustomerStatementId, x.Status, x.CurrentStage, x.ExposureAtOpen,
            x.CurrentExposure, x.OldestDueDate, x.NextActionOn, x.AssignedTo, x.PromiseAmount,
            x.PromiseDueOn, x.Version, x.CreatedBy, x.CreatedOn, x.UpdatedBy, x.UpdatedOn,
            x.StatusReason, x.Promises.OrderByDescending(p => p.CreatedOn).Select(MapPromise).ToArray());
    }

    private static PromiseToPayDto MapPromise(PromiseToPay x)
        => new(x.Id, x.Amount, x.PromisedOn, x.DueOn, x.Status, x.EvidenceReference,
            x.Version, x.CreatedBy, x.CreatedOn, x.ClosedBy, x.ClosedOn,
            x.MatchedPaymentId, x.MatchedAmount);

    private static DunningNoticeDto MapNotice(DunningNotice x)
        => new(x.Id, x.DunningCaseId, x.CustomerStatementId, x.FinanceCommunicationContactId,
            x.Stage, x.Status, x.SnapshotExposure, x.SnapshotHash, x.TemplateVersion,
            x.Version, x.CreatedBy, x.CreatedOn, x.ApprovedBy, x.ApprovedOn, x.ReleasedBy,
            x.ReleasedOn, x.DeliveryUpdatedOn, x.ProviderReference, x.FailureCode,
            x.SuppressionReason, x.DeliveryAttempts.OrderBy(a => a.AttemptNumber).Select(a =>
                new DunningDeliveryAttemptDto(a.Id, a.ProviderEventId, a.AttemptNumber, a.Status,
                    a.MaskedDestination, a.ArtifactHash, a.TemplateVersion, a.ProviderReference,
                    a.FailureCode, a.OccurredOn, a.ProviderOccurredOn,
                    a.SignedEvidenceReference)).ToArray(), x.Locale, x.Subject,
            x.ArtifactMediaType, x.ArtifactHash, x.ArtifactContent,
            x.CancellationEvidenceReference);

    private static DunningRunDto MapRun(DunningRun x)
        => new(x.Id, x.DunningPolicyId, x.CutoffAt, x.Status, x.CandidateCount,
            x.NoticeCount, x.SuppressedCount, x.FailedCount, x.Version, x.CreatedBy,
            x.CreatedOn, x.CompletedOn, x.CompletionEvidenceReference, x.FailureReason,
            x.FailureEvidenceReference, x.Decisions.OrderBy(d => d.Id).Select(d =>
                new DunningRunDecisionDto(d.Id, d.DunningRunId, d.CustomerId, d.CurrencyId,
                    d.CustomerStatementId, d.DunningCaseId, d.DunningNoticeId, d.Outcome,
                    d.ReasonCode, d.EvidenceHash, d.CreatedOn, d.CustomerCollectionProfileId)).ToArray());

    private async Task<string?> CurrencyCodeAsync(long? currencyId)
        => currencyId.HasValue
            ? await _context.Currencies.Where(x => x.Id == currencyId.Value).Select(x => x.Code).SingleOrDefaultAsync()
            : null;

    private async Task<T> InSerializableTransactionAsync<T>(Func<Task<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                    var result = await action();
                    await transaction.CommitAsync();
                    return result;
                });
            }
            catch (Exception exception) when (attempt < 4 && IsRetryable(exception))
            {
                _context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres && postgres.SqlState is
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
                return true;
        return false;
    }

    private static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("Idempotency-Key is required and must be 128 characters or fewer.");
    }

    private static void EnsureReplay(string storedHash, string requestHash)
    {
        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
            throw new FinanceConflictException("The idempotency key was already used for a different request.");
    }

    private static string Actor(string? value)
        => RequiredText(value, "actor", 255, 2);

    private static string RequiredText(string? value, string subject, int maximum, int minimum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < minimum || normalized.Length > maximum ||
            normalized.Any(char.IsControl))
            throw new ArgumentException($"A valid {subject} between {minimum} and {maximum} characters is required.");
        return normalized;
    }

    private static string? OptionalText(string? value, string subject, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return RequiredText(value, subject, maximum, 1);
    }

    private static string RequiredToken(string? value, string subject, int maximum)
    {
        var normalized = RequiredText(value, subject, maximum, 1);
        if (!Regex.IsMatch(normalized, "^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"The {subject} contains unsupported characters.");
        return normalized;
    }

    private static string RequiredChoice(string? value, string subject, params string[] choices)
    {
        var normalized = value?.Trim();
        var match = choices.FirstOrDefault(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException($"The {subject} is invalid.");
    }

    private static void EnsureTimeZone(string value)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("The time zone is invalid."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("The time zone is invalid."); }
    }

    private static bool IsQuietHour(DateTime utc, string timeZoneId, int start, int end)
    {
        EnsureTimeZone(timeZoneId);
        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).Hour;
        return start == end || (start < end ? hour >= start && hour < end : hour >= start || hour < end);
    }

    private static string AgingBucket(DateTime? dueDate, DateTime cutoff, decimal outstanding)
    {
        if (outstanding <= 0) return "Settled";
        if (!dueDate.HasValue || dueDate.Value.Date >= cutoff.Date) return "Current";
        return (cutoff.Date - dueDate.Value.Date).Days switch
        {
            <= 30 => "1-30", <= 60 => "31-60", <= 90 => "61-90", _ => "90+"
        };
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string BuildStatementArtifact(string issuer, string customer, string billingAddress,
        string currencyCode, DateTime periodStart, DateTime cutoffAt, int revision, StatementSnapshot snapshot)
    {
        static string H(string value) => WebUtility.HtmlEncode(value);
        static string M(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        var html = new StringBuilder(4096);
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Customer statement</title></head><body>")
            .Append("<h1>Customer statement</h1><dl><dt>Statement number</dt><dd>{{STATEMENT_NUMBER}}</dd><dt>Issuer</dt><dd>").Append(H(issuer))
            .Append("</dd><dt>Customer</dt><dd>").Append(H(customer))
            .Append("</dd><dt>Billing address</dt><dd>").Append(H(billingAddress))
            .Append("</dd><dt>Period</dt><dd>").Append(periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" through ").Append(cutoffAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("</dd><dt>Revision</dt><dd>").Append(revision)
            .Append("</dd><dt>Currency</dt><dd>").Append(H(currencyCode))
            .Append("</dd></dl><table><thead><tr><th>Date</th><th>Reference</th><th>Description</th><th>Debit</th><th>Credit</th><th>Applied</th><th>Outstanding</th><th>Age</th><th>Balance</th></tr></thead><tbody>");
        foreach (var line in snapshot.Lines)
            html.Append("<tr><td>").Append(line.ActivityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(H(line.SourceNumber)).Append("</td><td>").Append(H(line.Description))
                .Append("</td><td>").Append(M(line.DebitAmount)).Append("</td><td>").Append(M(line.CreditAmount))
                .Append("</td><td>").Append(M(line.AppliedAmount)).Append("</td><td>").Append(M(line.OutstandingAmount))
                .Append("</td><td>").Append(H(line.AgingBucket)).Append("</td><td>").Append(M(line.RunningBalance)).Append("</td></tr>");
        html.Append("</tbody></table><dl><dt>Opening balance</dt><dd>").Append(M(snapshot.OpeningBalance))
            .Append("</dd><dt>Debits</dt><dd>").Append(M(snapshot.DebitTotal))
            .Append("</dd><dt>Credits</dt><dd>").Append(M(snapshot.CreditTotal))
            .Append("</dd><dt>Closing balance</dt><dd>").Append(M(snapshot.ClosingBalance))
            .Append("</dd><dt>Unapplied cash</dt><dd>").Append(M(snapshot.UnappliedCash))
            .Append("</dd><dt>Net customer position</dt><dd>").Append(M(snapshot.NetCustomerPosition))
            .Append("</dd><dt>Aging current</dt><dd>").Append(M(snapshot.Aging.Current))
            .Append("</dd><dt>Aging 1-30</dt><dd>").Append(M(snapshot.Aging.OneToThirty))
            .Append("</dd><dt>Aging 31-60</dt><dd>").Append(M(snapshot.Aging.ThirtyOneToSixty))
            .Append("</dd><dt>Aging 61-90</dt><dd>").Append(M(snapshot.Aging.SixtyOneToNinety))
            .Append("</dd><dt>Aging 90+</dt><dd>").Append(M(snapshot.Aging.OverNinety))
            .Append("</dd></dl><p>Contact the issuer promptly to dispute any item shown on this statement.</p></body></html>");
        return html.ToString();
    }

    private async Task<NoticeArtifact> BuildNoticeArtifactAsync(
        DunningCase item, CustomerStatement statement, DunningPolicy policy,
        DunningPolicyStep step, string locale, DateTime generatedOn)
    {
        var normalizedLocale = RequiredToken(locale, "notice locale", 20);
        var currency = await CurrencyCodeAsync(item.CurrencyId) ?? "base currency";
        var statementNumber = statement.StatementNumber ?? $"statement-{statement.Id}";
        var subject = $"Payment reminder - {statementNumber}";
        var content = string.Join('\n',
            "Nexora governed collections notice",
            $"Customer: {statement.CustomerNameSnapshot}",
            $"Statement: {statementNumber}",
            $"Outstanding amount: {item.CurrentExposure.ToString("0.00", CultureInfo.InvariantCulture)} {currency}",
            $"Oldest due date: {item.OldestDueDate:yyyy-MM-dd}",
            $"Collection stage: {step.Stage}",
            $"Jurisdiction: {policy.JurisdictionCode}",
            $"Locale: {normalizedLocale}",
            $"Generated at (UTC): {generatedOn:O}",
            "If you dispute this balance, contact the issuer using your established service channel before making payment.",
            $"Policy version: {policy.PolicyVersion}; template: {step.TemplateVersion}.");
        const string mediaType = "text/plain; charset=utf-8";
        var hash = HashText(string.Join('\n', subject, mediaType, normalizedLocale, content));
        return new NoticeArtifact(subject, mediaType, normalizedLocale, content, hash);
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string DerivedKey(string root, string scope)
        => HashText($"{root}\n{scope}");

    private void VerifyContactProviderSignature(long businessUnitId,
        CreateFinanceCommunicationContactRequest request, string purpose, string channel,
        string destinationToken, string maskedDestination, string evidenceReference,
        DateTime effectiveFrom)
    {
        var supplied = request.ProviderSignature?.Trim();
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length != 64)
            throw new ArgumentException("A valid contact verification provider signature is required.");
        var canonical = string.Join('\n', businessUnitId.ToString(CultureInfo.InvariantCulture),
            request.CustomerId.ToString(CultureInfo.InvariantCulture), purpose, channel,
            destinationToken, maskedDestination, UnixMilliseconds(effectiveFrom),
            request.EffectiveTo.HasValue ? UnixMilliseconds(NormalizeUtc(request.EffectiveTo.Value)) : string.Empty,
            evidenceReference, request.VerificationProviderEventId.ToString("D"));
        var expected = HMACSHA256.HashData(_contactVerificationSecret, Encoding.UTF8.GetBytes(canonical));
        byte[] actual;
        try { actual = Convert.FromHexString(supplied); }
        catch (FormatException) { throw new ArgumentException("A valid contact verification provider signature is required."); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new ArgumentException("The contact verification provider signature is invalid.");
    }

    private void VerifyDeliveryProviderSignature(long businessUnitId, long noticeId, bool delivered,
        DunningDeliveryResultRequest request)
    {
        var canonical = string.Join('\n', businessUnitId.ToString(CultureInfo.InvariantCulture),
            noticeId.ToString(CultureInfo.InvariantCulture), delivered.ToString().ToLowerInvariant(),
            request.ProviderEventId.ToString("D"), request.ProviderReference?.Trim(),
            UnixMilliseconds(NormalizeUtc(request.ProviderOccurredOn)), request.FailureCode?.Trim(),
            request.SignedEvidenceReference?.Trim());
        VerifySignature(request.ProviderSignature, _dunningProviderSecret, canonical,
            "The delivery provider signature is invalid.");
    }

    private static void VerifySignature(string? supplied, byte[] secret, string canonical, string message)
    {
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Trim().Length != 64)
            throw new ArgumentException(message);
        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical));
        byte[] actual;
        try { actual = Convert.FromHexString(supplied.Trim()); }
        catch (FormatException) { throw new ArgumentException(message); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new ArgumentException(message);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string UnixMilliseconds(DateTime value)
        => new DateTimeOffset(NormalizeUtc(value)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))).ToLowerInvariant();

    private sealed record StatementEvent(DateTime ActivityDate, string SourceType, long SourceId,
        long SourceVersion, string SourceNumber, long? CommercialCaseId, DateTime? DueDate,
        string Description, decimal DebitAmount, decimal CreditAmount, decimal AppliedAmount,
        decimal OutstandingAmount);
    private sealed record SnapshotLine(int Sequence, string SourceType, long SourceId, long SourceVersion,
        string SourceNumber, long? CommercialCaseId, DateTime ActivityDate, DateTime? DueDate,
        string Description, decimal DebitAmount, decimal CreditAmount, decimal AppliedAmount,
        decimal OutstandingAmount, string AgingBucket, decimal RunningBalance);
    private sealed record StatementSnapshot(decimal OpeningBalance, decimal DebitTotal, decimal CreditTotal,
        decimal UnappliedCash, decimal ClosingBalance, decimal NetCustomerPosition, AgingTotals Aging,
        DateTime? OldestOutstandingDueDate, string SourceFingerprint, IReadOnlyList<SnapshotLine> Lines);
    private sealed record NoticeArtifact(string Subject, string MediaType, string Locale, string Content, string Hash);
    private sealed class AgingTotals
    {
        public decimal Current { get; private set; }
        public decimal OneToThirty { get; private set; }
        public decimal ThirtyOneToSixty { get; private set; }
        public decimal SixtyOneToNinety { get; private set; }
        public decimal OverNinety { get; private set; }
        public void Add(string bucket, decimal value)
        {
            if (value <= 0) return;
            switch (bucket)
            {
                case "Current": Current += value; break;
                case "1-30": OneToThirty += value; break;
                case "31-60": ThirtyOneToSixty += value; break;
                case "61-90": SixtyOneToNinety += value; break;
                case "90+": OverNinety += value; break;
            }
        }
    }

    public Task<DunningPolicyDto> ApprovePolicyAsync(long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor)
        => TransitionPolicyAsync(businessUnitId, policyId, request, actor, "Approved");

    public Task<DunningPolicyDto> ActivatePolicyAsync(long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor)
        => TransitionPolicyAsync(businessUnitId, policyId, request, actor, "Active");

    public Task<DunningPolicyDto> RetirePolicyAsync(long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor)
        => TransitionPolicyAsync(businessUnitId, policyId, request, actor, "Retired");

    public async Task<IReadOnlyList<DunningPolicyDto>> GetPoliciesAsync(long businessUnitId, string? status)
    {
        var query = _context.DunningPolicies.Include(x => x.Steps).Where(x => x.BusinessUnitId == businessUnitId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        return (await query.OrderByDescending(x => x.PolicyVersion).ToListAsync()).Select(MapPolicy).ToArray();
    }

    private async Task<DunningPolicyDto> TransitionPolicyAsync(
        long businessUnitId, long policyId, DunningPolicyActionRequest request, string actor, string target)
        => await InSerializableTransactionAsync(async () =>
        {
            var policy = await LockPolicyAsync(policyId, businessUnitId);
            if (policy.Status == target) return MapPolicy(policy);
            if (policy.Version != request.ExpectedVersion)
                throw new FinanceConflictException("The dunning policy changed; reload it.");
            var allowed = (policy.Status, target) is ("Draft", "Approved") or ("Approved", "Active") or ("Active", "Retired");
            if (!allowed) throw new FinanceConflictException($"Dunning policy cannot move from {policy.Status} to {target}.");
            if (target == "Approved" && string.Equals(policy.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new FinanceConflictException("Dunning policy approval requires an independent checker.");
            if (target == "Active" && string.Equals(policy.ApprovedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new FinanceConflictException("Dunning policy activation requires an independent operator.");
            if (target == "Active" && string.Equals(policy.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new FinanceConflictException("Dunning policy activation requires an operator independent from the maker.");
            if (target == "Active")
            {
                var prior = await _context.DunningPolicies.Where(x => x.BusinessUnitId == businessUnitId && x.Status == "Active" && x.Id != policy.Id).ToListAsync();
                foreach (var row in prior) { row.Status = "Retired"; row.RetiredBy = Actor(actor); row.RetiredOn = DateTime.UtcNow; row.Version++; }
                // Retire first so the immediate partial unique index never observes two active policies.
                if (prior.Count > 0) await _context.SaveChangesAsync();
            }
            policy.Status = target;
            if (target == "Approved") { policy.ApprovedBy = Actor(actor); policy.ApprovedOn = DateTime.UtcNow; }
            if (target == "Active") { policy.ActivatedBy = Actor(actor); policy.ActivatedOn = DateTime.UtcNow; }
            if (target == "Retired") { policy.RetiredBy = Actor(actor); policy.RetiredOn = DateTime.UtcNow; }
            policy.Version++;
            await AuditAndOutboxAsync(businessUnitId, "DunningPolicy", policy.Id, policy.Version,
                target, actor, $"finance.dunning-policy.{target.ToLowerInvariant()}",
                new { policy.Id, policy.PolicyVersion, policy.Status, policy.Version });
            await _context.SaveChangesAsync();
            return MapPolicy(policy);
        });
}
