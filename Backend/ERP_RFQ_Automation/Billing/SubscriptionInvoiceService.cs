using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Billing.Accounting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Billing;

public sealed record CreateSubscriptionInvoice(
    long StatementId,
    decimal TaxRatePercent,
    string TaxTreatment,
    string SellerLegalName,
    string SellerTaxNumber);

public sealed class SubscriptionInvoiceService(
    ErpRfqAutomationContext db,
    AccountingOutboxService? accountingOutbox = null)
{
    public async Task<SubscriptionInvoice> CreateDraftAsync(
        CreateSubscriptionInvoice request, string actor, CancellationToken ct = default)
    {
        if (request.TaxRatePercent is < 0 or > 100)
            throw new BillingConflictException("Tax rate must be between 0 and 100 percent.");
        if (string.IsNullOrWhiteSpace(request.TaxTreatment)
            || string.IsNullOrWhiteSpace(request.SellerLegalName)
            || string.IsNullOrWhiteSpace(request.SellerTaxNumber))
            throw new BillingConflictException("Seller identity, seller tax number and tax treatment are required.");

        // The controller owns the encompassing audit transaction. Serialize one statement inside
        // that transaction so concurrent HTTP retries cannot abort it on the unique constraint
        // before the audit row is written.
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
        {
            var lockKey = unchecked(0x4E58494E00000000L ^ request.StatementId); // "NXIN" namespace
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})", ct);
        }

        var sellerSnapshot = SellerSnapshot(request);

        var existing = await db.Set<SubscriptionInvoice>().AsNoTracking()
            .FirstOrDefaultAsync(i => i.BillingStatementId == request.StatementId, ct);
        if (existing is not null)
        {
            if (existing.TaxRatePercent != request.TaxRatePercent
                || !string.Equals(existing.TaxTreatment, request.TaxTreatment.Trim(), StringComparison.Ordinal)
                || !string.Equals(existing.SellerSnapshotJson, sellerSnapshot, StringComparison.Ordinal))
                throw new BillingConflictException(
                    "This billing statement already has an invoice with different tax or seller terms.");
            return existing;
        }

        var statement = await db.Set<BillingStatement>().AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == request.StatementId, ct)
            ?? throw new BillingNotFoundException($"Billing statement {request.StatementId} does not exist.");
        if (statement.Status != BillingStatementStatus.Final)
            throw new BillingConflictException("Only a Final billing statement can produce an invoice.");
        var lineTotal = statement.Lines.Sum(line => line.Amount);
        if (lineTotal != statement.TotalAmount)
            throw new BillingConflictException(
                $"The Final statement cannot be invoiced because its line total ({lineTotal:0.00}) " +
                $"does not reconcile to its header total ({statement.TotalAmount:0.00}).");

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == statement.TenantId, ct);
        if (string.IsNullOrWhiteSpace(tenant.LegalName) || string.IsNullOrWhiteSpace(tenant.BillingContactEmail))
            throw new BillingConflictException("The tenant requires legal identity and an invoice recipient before invoicing.");

        var evidence = CanonicalizeJson(JsonSerializer.Serialize(new
        {
            statement.Id,
            statement.TenantId,
            statement.PeriodStartUtc,
            statement.PeriodEndUtc,
            statement.RateCardId,
            statement.Currency,
            statement.TotalAmount,
            statement.FinalizedAtUtc,
            statement.FinalizedBy,
            lines = statement.Lines.OrderBy(l => l.MeterKey).ThenBy(l => l.Id).Select(l => new
            {
                l.MeterKey, l.Description, l.MeteredQuantity, l.IncludedQuantity,
                l.BillableQuantity, l.UnitPrice, l.Amount, l.SourceNote, l.CoverageNote
            })
        }));
        var now = DateTime.UtcNow;
        var tax = decimal.Round(statement.TotalAmount * request.TaxRatePercent / 100m, 2,
            MidpointRounding.AwayFromZero);
        var invoice = new SubscriptionInvoice
        {
            TenantId = tenant.Id,
            BillingStatementId = statement.Id,
            InvoiceNumber = $"DRAFT-{Guid.NewGuid():N}",
            Currency = statement.Currency,
            Subtotal = statement.TotalAmount,
            TaxRatePercent = request.TaxRatePercent,
            TaxAmount = tax,
            TotalAmount = statement.TotalAmount + tax,
            IssuedAtUtc = now,
            DueAtUtc = now.AddDays(tenant.PaymentTermsDays ?? 30),
            SellerSnapshotJson = sellerSnapshot,
            BuyerSnapshotJson = JsonSerializer.Serialize(new
            {
                tenant.LegalName, tenant.RegistrationNumber, tenant.TaxNumber, tenant.CountryCode,
                tenant.BillingAddress, tenant.BillingContactName, tenant.BillingContactEmail,
                tenant.PurchaseOrderReference
            }),
            TaxTreatment = request.TaxTreatment.Trim(),
            SourceEvidenceJson = evidence,
            SourceEvidenceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant(),
            CreatedBy = actor,
            CreatedAtUtc = now
        };
        db.Set<SubscriptionInvoice>().Add(invoice);
        try
        {
            await db.SaveChangesAsync(ct);
            return invoice;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_SubscriptionInvoices_BillingStatementId"
            })
        {
            db.ChangeTracker.Clear();
            return await db.Set<SubscriptionInvoice>().AsNoTracking()
                .FirstAsync(i => i.BillingStatementId == request.StatementId, ct);
        }
    }

    public async Task<SubscriptionInvoice> FinalizeAsync(
        long id, string actor, CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(() => FinalizeAsync(id, actor, ct), ct);
        await AcquireInvoiceLockAsync(id, ct);
        var invoice = await db.Set<SubscriptionInvoice>().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new BillingNotFoundException($"Subscription invoice {id} does not exist.");
        if (invoice.Status != SubscriptionInvoiceStatus.Draft)
            return invoice;
        if (string.Equals(invoice.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new BillingConflictException("The invoice maker cannot finalize the same invoice.");
        var computedEvidenceHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(CanonicalizeJson(invoice.SourceEvidenceJson)))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computedEvidenceHash),
                Encoding.ASCII.GetBytes(invoice.SourceEvidenceSha256 ?? string.Empty)))
            throw new BillingConflictException("The invoice source evidence hash does not match its frozen evidence.");

        invoice.InvoiceNumber = $"NX-{invoice.IssuedAtUtc:yyyyMM}-{invoice.Id:D8}";
        invoice.Status = SubscriptionInvoiceStatus.Finalized;
        invoice.FinalizedBy = actor;
        invoice.FinalizedAtUtc = DateTime.UtcNow;
        invoice.Version++;
        if (accountingOutbox is not null)
            await accountingOutbox.EnqueueInvoiceExportAsync(invoice, ct);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<SubscriptionCreditNote> CreditAsync(
        long id, decimal amount, string reason, string actor, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(
                () => CreditAsync(id, amount, reason, actor, idempotencyKey, ct), ct);
        var key = RequiredIdempotencyKey(idempotencyKey);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new BillingConflictException("A credit reason of at least 5 characters is required.");
        var normalizedReason = reason.Trim();
        await AcquireInvoiceLockAsync(id, ct);
        var replay = await db.Set<SubscriptionCreditNote>().AsNoTracking()
            .FirstOrDefaultAsync(value => value.IdempotencyKey == key, ct);
        if (replay is not null)
        {
            if (replay.SubscriptionInvoiceId != id || replay.Amount != amount
                || !string.Equals(replay.Reason, normalizedReason, StringComparison.Ordinal))
                throw new BillingConflictException("The credit idempotency key was already used for different details.");
            return replay;
        }

        var invoice = await db.Set<SubscriptionInvoice>().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new BillingNotFoundException($"Subscription invoice {id} does not exist.");
        if (invoice.Status is SubscriptionInvoiceStatus.Draft or SubscriptionInvoiceStatus.Void)
            throw new BillingConflictException("Only a posted invoice can receive a credit note.");
        if (amount <= 0 || invoice.CreditedAmount + amount > invoice.TotalAmount - invoice.PaidAmount)
            throw new BillingConflictException("Credit amount exceeds the invoice's remaining balance.");
        var credit = new SubscriptionCreditNote
        {
            SubscriptionInvoiceId = id,
            CreditNumber = $"NC-{DateTime.UtcNow:yyyyMM}-{Guid.NewGuid():N}",
            IdempotencyKey = key,
            Amount = amount,
            Reason = normalizedReason,
            CreatedBy = actor,
            CreatedAtUtc = DateTime.UtcNow
        };
        invoice.CreditedAmount += amount;
        invoice.Status = SubscriptionInvoiceStatus.Corrected;
        invoice.Version++;
        db.Set<SubscriptionCreditNote>().Add(credit);
        await db.SaveChangesAsync(ct);
        return credit;
    }

    public async Task<SubscriptionPayment> RecordPaymentAsync(
        long id, decimal amount, string reference, DateTime receivedAtUtc, string actor,
        CancellationToken ct = default)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            return await InTransactionAsync(
                () => RecordPaymentAsync(id, amount, reference, receivedAtUtc, actor, ct), ct);
        receivedAtUtc = NormalizePostgreSqlTimestamp(receivedAtUtc);
        if (amount <= 0 || string.IsNullOrWhiteSpace(reference))
            throw new BillingConflictException("A positive amount and external payment reference are required.");
        if (receivedAtUtc == default || receivedAtUtc > DateTime.UtcNow.AddMinutes(5))
            throw new BillingConflictException("Payment received time is required and cannot be in the future.");
        await AcquireInvoiceLockAsync(id, ct);
        var replay = await db.Set<SubscriptionPayment>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.ExternalReference == reference.Trim(), ct);
        if (replay is not null)
        {
            if (replay.SubscriptionInvoiceId != id || replay.Amount != amount
                || replay.ReceivedAtUtc != receivedAtUtc)
                throw new BillingConflictException("The payment reference was already used for different payment details.");
            return replay;
        }

        var invoice = await db.Set<SubscriptionInvoice>().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new BillingNotFoundException($"Subscription invoice {id} does not exist.");
        if (invoice.Status is SubscriptionInvoiceStatus.Draft or SubscriptionInvoiceStatus.Void)
            throw new BillingConflictException("Only a posted invoice can receive payment.");
        var outstanding = invoice.TotalAmount - invoice.CreditedAmount - invoice.PaidAmount;
        if (amount > outstanding)
            throw new BillingConflictException("Payment amount exceeds the invoice's outstanding balance.");

        var payment = new SubscriptionPayment
        {
            SubscriptionInvoiceId = id,
            ExternalReference = reference.Trim(),
            Amount = amount,
            ReceivedAtUtc = receivedAtUtc,
            RecordedBy = actor,
            RecordedAtUtc = DateTime.UtcNow
        };
        invoice.PaidAmount += amount;
        invoice.Status = invoice.PaidAmount + invoice.CreditedAmount == invoice.TotalAmount
            ? SubscriptionInvoiceStatus.Paid
            : SubscriptionInvoiceStatus.PartiallyPaid;
        invoice.Version++;
        db.Set<SubscriptionPayment>().Add(payment);
        await db.SaveChangesAsync(ct);
        return payment;
    }

    private static string SellerSnapshot(CreateSubscriptionInvoice request) => JsonSerializer.Serialize(new
    {
        legalName = request.SellerLegalName.Trim(), taxNumber = request.SellerTaxNumber.Trim()
    });

    // PostgreSQL jsonb intentionally does not preserve object-property order. Hash a stable
    // semantic representation so the same frozen evidence verifies after a database round trip.
    private static string CanonicalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(document.RootElement, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }
            writer.WriteEndObject();
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
            return;
        }

        element.WriteTo(writer);
    }

    private async Task AcquireInvoiceLockAsync(long invoiceId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
        {
            var lockKey = unchecked(0x4E58415200000000L ^ invoiceId); // "NXAR" namespace
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
            db.ChangeTracker.Clear();
        }
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private static DateTime NormalizePostgreSqlTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    private static string RequiredIdempotencyKey(string value)
    {
        var key = value?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new BillingConflictException("A credit idempotency key of at most 128 characters is required.");
        return key;
    }
}
