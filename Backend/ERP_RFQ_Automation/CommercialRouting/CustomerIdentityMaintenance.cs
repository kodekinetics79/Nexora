using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialRouting;

public static class CustomerIdentityMaintenance
{
    private static readonly string[] ManagedSources =
        ["MigrationBackfill", "CustomerProfile", "CustomerContact", "CustomerImport"];

    public static async Task EnsureAuthoritativeValuesAvailableAsync(
        ErpRfqAutomationContext db,
        long businessUnitId,
        long? customerId,
        IEnumerable<(CustomerIdentifierType Type, string? Value)> values,
        CancellationToken cancellationToken = default)
    {
        var normalized = values
            .Where(value => value.Type is CustomerIdentifierType.Email or CustomerIdentifierType.Phone)
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .Select(value => (value.Type, Value: RoutingValueNormalizer.Normalize(value.Type, value.Value!)))
            .Where(value => value.Value.Length > 0)
            .Distinct()
            .OrderBy(value => value.Type)
            .ThenBy(value => value.Value, StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return;

        if (db.Database.IsNpgsql())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Customer identity validation requires a transaction.");

            foreach (var value in normalized)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({$"customer-authoritative:{businessUnitId}:{value.Type}:{value.Value}"}, 0))",
                    cancellationToken);
            }
        }

        foreach (var value in normalized)
        {
            var conflict = await db.Set<CustomerIdentifier>().AsNoTracking().AnyAsync(identifier =>
                identifier.BusinessUnitId == businessUnitId
                && identifier.IdentifierType == value.Type
                && identifier.NormalizedValue == value.Value
                && identifier.EffectiveTo == null
                && (!customerId.HasValue || identifier.CustomerId != customerId.Value), cancellationToken);
            if (conflict)
                throw new CustomerIdentityConflictException(
                    "The email or phone number is already linked to another customer in this tenant.");
        }
    }

    public static async Task SynchronizeAsync(
        ErpRfqAutomationContext db,
        long businessUnitId,
        long customerId,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (businessUnitId <= 0 || customerId <= 0)
            throw new ArgumentOutOfRangeException(nameof(customerId));
        if (!ManagedSources.Contains(source, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported customer identity source.", nameof(source));

        if (db.Database.IsNpgsql())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("Customer identity synchronization requires a transaction.");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({$"customer-identity:{businessUnitId}:{customerId}"}, 0))",
                cancellationToken);
        }

        var customer = await db.Customers.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Buid == businessUnitId && c.Id == customerId, cancellationToken)
            ?? throw new InvalidOperationException("The tenant-owned customer was not found.");
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.CustomerId == customerId && c.IsActive != false)
            .Select(c => new { c.Email, c.PhoneNo, c.MobileNo })
            .ToListAsync(cancellationToken);

        var expected = new Dictionary<(CustomerIdentifierType Type, string Value), ExpectedIdentity>();
        if (customer.IsActive != false)
        {
            Add(CustomerIdentifierType.ErpAccount, customer.DocId, true, 1m, "CustomerProfile");
            Add(CustomerIdentifierType.CustomerName, customer.Name, true, 0.9m, "CustomerProfile");
            AddEmail(customer.ContactEmail, "CustomerProfile");
            foreach (var contact in contacts)
            {
                AddEmail(contact.Email, "CustomerContact");
                Add(CustomerIdentifierType.Phone, contact.MobileNo, true, 0.95m, "CustomerContact");
                Add(CustomerIdentifierType.Phone, contact.PhoneNo, true, 0.95m, "CustomerContact");
            }
        }

        var active = await db.Set<CustomerIdentifier>()
            .Where(i => i.BusinessUnitId == businessUnitId && i.CustomerId == customerId && i.EffectiveTo == null)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var expirable = customer.IsActive == false
            ? active
            : active.Where(i => ManagedSources.Contains(i.Source, StringComparer.Ordinal));
        foreach (var identifier in expirable)
        {
            if (!expected.ContainsKey((identifier.IdentifierType, identifier.NormalizedValue)))
                identifier.EffectiveTo = now;
        }

        foreach (var item in expected.Values)
        {
            var current = active.SingleOrDefault(i =>
                i.IdentifierType == item.Type && i.NormalizedValue == item.NormalizedValue);
            if (current is not null)
            {
                if (ManagedSources.Contains(current.Source, StringComparer.Ordinal))
                {
                    current.DisplayValue = item.DisplayValue;
                    current.IsVerified = item.IsVerified;
                    current.Confidence = item.Confidence;
                    current.Source = item.Source;
                }
                continue;
            }

            db.Add(new CustomerIdentifier
            {
                BusinessUnitId = businessUnitId,
                CustomerId = customerId,
                IdentifierType = item.Type,
                NormalizedValue = item.NormalizedValue,
                DisplayValue = item.DisplayValue,
                IsVerified = item.IsVerified,
                Confidence = item.Confidence,
                Source = item.Source,
                EffectiveFrom = now
            });
        }

        void AddEmail(string? value, string identitySource)
        {
            Add(CustomerIdentifierType.Email, value, true, 1m, identitySource);
            var domain = RoutingValueNormalizer.DomainFromEmail(value);
            Add(CustomerIdentifierType.Domain, domain, true, 0.95m, identitySource);
        }

        void Add(CustomerIdentifierType type, string? value, bool verified, decimal confidence, string identitySource)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var display = value.Trim();
            var normalized = RoutingValueNormalizer.Normalize(type, display);
            if (normalized.Length == 0) return;
            expected.TryAdd((type, normalized), new ExpectedIdentity(
                type, normalized, display, verified, confidence,
                source == "CustomerImport" ? source : identitySource));
        }
    }

    private sealed record ExpectedIdentity(
        CustomerIdentifierType Type,
        string NormalizedValue,
        string DisplayValue,
        bool IsVerified,
        decimal Confidence,
        string Source);
}

public sealed class CustomerIdentityConflictException(string message) : InvalidOperationException(message);
