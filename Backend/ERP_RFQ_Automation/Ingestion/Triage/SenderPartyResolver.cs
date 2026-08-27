using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <summary>
/// Resolves whether an inbound sender is a known CUSTOMER or a known SUPPLIER of this tenant,
/// from master data only. It answers null far more often than it answers — and that is the
/// point: <c>SenderPartyType</c> is used to STOP a message only in combination with supplier
/// document vocabulary, so an unknown sender always keeps its right to be extracted.
///
/// This is a read-only lookup that deliberately shares no code with the in-flight
/// customer-identity resolver: triage must not depend on, or perturb, lead-level matching.
/// </summary>
public static class SenderPartyResolver
{
    public const string Customer = "customer";
    public const string Supplier = "supplier";

    /// <summary>Consumer domains where a domain match proves nothing about the organisation.</summary>
    private static readonly string[] PublicMailDomains =
    {
        "gmail.com", "googlemail.com", "hotmail.com", "outlook.com", "live.com", "yahoo.com",
        "yahoo.co.uk", "aol.com", "icloud.com", "me.com", "protonmail.com", "proton.me",
        "gmx.com", "mail.com", "yandex.com", "zoho.com", "rediffmail.com", "emirates.net.ae",
        "eim.ae", "qq.com", "163.com"
    };

    public static string? ExtractDomain(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1) return null;
        return address[(at + 1)..].Trim().Trim('>').ToLowerInvariant();
    }

    public static bool IsPublicMailDomain(string? domain)
        => !string.IsNullOrWhiteSpace(domain)
           && PublicMailDomains.Contains(domain.Trim().ToLowerInvariant());

    /// <summary>
    /// "customer", "supplier", or null when unknown OR ambiguous. Ambiguity resolves to null on
    /// purpose: a counterparty that is both must never be silently treated as a supplier, which
    /// is the only direction that can stop a real enquiry.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        ErpRfqAutomationContext context, long businessUnitId, string? fromAddress,
        CancellationToken ct = default)
    {
        var address = fromAddress?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(address)) return null;

        var isCustomer = false;
        var isSupplier = false;

        try
        {
            var contacts = await context.Contacts.AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId && c.Email != null
                    && c.Email.ToLower() == address)
                .Select(c => new { c.CustomerId, c.SupplierId })
                .ToListAsync(ct);
            isCustomer |= contacts.Any(c => c.CustomerId != null);
            isSupplier |= contacts.Any(c => c.SupplierId != null);

            isCustomer |= await context.Customers.AsNoTracking().AnyAsync(
                c => c.Buid == businessUnitId && c.ContactEmail != null
                    && c.ContactEmail.ToLower() == address, ct);
            isSupplier |= await context.Suppliers.AsNoTracking().AnyAsync(
                s => s.Buid == businessUnitId && s.ContactEmail != null
                    && s.ContactEmail.ToLower() == address, ct);

            if (!isCustomer && !isSupplier)
            {
                var domain = ExtractDomain(address);
                if (!string.IsNullOrWhiteSpace(domain) && !IsPublicMailDomain(domain))
                {
                    var suffix = "@" + domain;
                    isCustomer = await context.Contacts.AsNoTracking().AnyAsync(
                            c => c.BusinessUnitId == businessUnitId && c.CustomerId != null
                                && c.Email != null && c.Email.ToLower().EndsWith(suffix), ct)
                        || await context.Customers.AsNoTracking().AnyAsync(
                            c => c.Buid == businessUnitId && c.ContactEmail != null
                                && c.ContactEmail.ToLower().EndsWith(suffix), ct);
                    isSupplier = await context.Contacts.AsNoTracking().AnyAsync(
                            c => c.BusinessUnitId == businessUnitId && c.SupplierId != null
                                && c.Email != null && c.Email.ToLower().EndsWith(suffix), ct)
                        || await context.Suppliers.AsNoTracking().AnyAsync(
                            s => s.Buid == businessUnitId && s.ContactEmail != null
                                && s.ContactEmail.ToLower().EndsWith(suffix), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Master data being unavailable must never stop mail: an unresolved sender is a
            // perfectly good triage input (it can only ever produce "extract and flag").
            return null;
        }

        if (isCustomer && isSupplier) return null;
        if (isCustomer) return Customer;
        if (isSupplier) return Supplier;
        return null;
    }
}
