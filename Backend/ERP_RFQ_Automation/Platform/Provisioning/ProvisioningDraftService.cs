using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Provisioning;

public enum ProvisioningDraftOutcome
{
    Saved,
    NotFound,

    /// <summary>Somebody else saved over it. 409, with the current version so the client can reload.</summary>
    VersionConflict,

    /// <summary>The payload carried something a draft must not hold. 400.</summary>
    Rejected
}

public sealed record ProvisioningDraftResult(
    ProvisioningDraftOutcome Outcome, ProvisioningDraft? Draft, string? Error = null);

/// <summary>
/// Save-and-come-back for the provisioning wizard.
///
/// <para><b>Ownership is the access rule, and a stranger's draft is a 404 rather than a 403.</b>
/// The distinction matters: a 403 confirms that a draft with that id exists, which turns the id
/// space into a directory of which customers the platform team is currently onboarding. There is
/// no reason for one operator to be able to enumerate another's pipeline.</para>
/// </summary>
public interface IProvisioningDraftService
{
    Task<IReadOnlyList<ProvisioningDraft>> ListAsync(
        string ownerEmail, bool includeSubmitted, CancellationToken ct = default);

    Task<ProvisioningDraft?> GetAsync(long id, string ownerEmail, CancellationToken ct = default);

    Task<ProvisioningDraftResult> CreateAsync(
        SaveProvisioningDraftRequest request, string ownerEmail, long? ownerPlatformUserId,
        CancellationToken ct = default);

    Task<ProvisioningDraftResult> UpdateAsync(
        long id, SaveProvisioningDraftRequest request, string ownerEmail, CancellationToken ct = default);

    Task<bool> DeleteAsync(long id, string ownerEmail, CancellationToken ct = default);

    /// <summary>Marks a draft as having produced an execution, so the console can retire it
    /// without discarding what the operator typed.</summary>
    Task MarkSubmittedAsync(
        long id, string ownerEmail, long executionId, CancellationToken ct = default);
}

public sealed class ProvisioningDraftService : IProvisioningDraftService
{
    private readonly ErpRfqAutomationContext _db;

    public ProvisioningDraftService(ErpRfqAutomationContext db) => _db = db;

    public async Task<IReadOnlyList<ProvisioningDraft>> ListAsync(
        string ownerEmail, bool includeSubmitted, CancellationToken ct = default)
    {
        var owner = Owner(ownerEmail);
        var query = _db.Set<ProvisioningDraft>().AsNoTracking()
            .Where(d => d.OwnerEmail == owner);

        if (!includeSubmitted)
            query = query.Where(d => d.SubmittedExecutionId == null);

        // Projected without the payload. A list of twenty drafts should not carry twenty full
        // company profiles across the wire, and the console's list only renders name and dates.
        return await query
            .OrderByDescending(d => d.UpdatedOn)
            .Select(d => new ProvisioningDraft
            {
                Id = d.Id,
                Name = d.Name,
                OwnerEmail = d.OwnerEmail,
                OwnerPlatformUserId = d.OwnerPlatformUserId,
                CreatedOn = d.CreatedOn,
                UpdatedOn = d.UpdatedOn,
                SubmittedExecutionId = d.SubmittedExecutionId,
                Version = d.Version,
                // Not read. The property is non-nullable on the entity, so it is filled with a
                // marker rather than left null; the DTO projection drops it entirely.
                Payload = string.Empty
            })
            .ToListAsync(ct);
    }

    public Task<ProvisioningDraft?> GetAsync(long id, string ownerEmail, CancellationToken ct = default)
    {
        var owner = Owner(ownerEmail);
        return _db.Set<ProvisioningDraft>().AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.OwnerEmail == owner, ct);
    }

    public async Task<ProvisioningDraftResult> CreateAsync(
        SaveProvisioningDraftRequest request, string ownerEmail, long? ownerPlatformUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Reject(request.Payload) is string rejection)
            return new ProvisioningDraftResult(ProvisioningDraftOutcome.Rejected, null, rejection);

        var now = DateTime.UtcNow;
        var draft = new ProvisioningDraft
        {
            Name = ResolveName(request),
            OwnerEmail = Owner(ownerEmail),
            OwnerPlatformUserId = ownerPlatformUserId,
            Payload = ProvisioningRequestCanonicalizer.Redact(request.Payload),
            CreatedOn = now,
            UpdatedOn = now,
            Version = 1
        };

        _db.Set<ProvisioningDraft>().Add(draft);
        await _db.SaveChangesAsync(ct);
        return new ProvisioningDraftResult(ProvisioningDraftOutcome.Saved, draft);
    }

    public async Task<ProvisioningDraftResult> UpdateAsync(
        long id, SaveProvisioningDraftRequest request, string ownerEmail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Reject(request.Payload) is string rejection)
            return new ProvisioningDraftResult(ProvisioningDraftOutcome.Rejected, null, rejection);

        var owner = Owner(ownerEmail);
        var draft = await _db.Set<ProvisioningDraft>()
            .FirstOrDefaultAsync(d => d.Id == id && d.OwnerEmail == owner, ct);
        if (draft is null)
            return new ProvisioningDraftResult(ProvisioningDraftOutcome.NotFound, null);

        // Version is required on update. Two tabs open on one draft is not a hypothetical — a
        // wizard that autosaves while an operator edits the same record in another window is the
        // ordinary case, and last-write-wins silently discards a tax number somebody typed.
        if (request.Version is not long expected)
            return new ProvisioningDraftResult(
                ProvisioningDraftOutcome.VersionConflict, draft,
                "A version is required when updating a draft. Reload it and save again.");

        if (expected != draft.Version)
            return new ProvisioningDraftResult(
                ProvisioningDraftOutcome.VersionConflict, draft,
                $"This draft was changed elsewhere (you have version {expected}, the server has " +
                $"{draft.Version}). Reload it before saving so nothing typed in the other window " +
                "is lost.");

        draft.Name = ResolveName(request, draft.Name);
        draft.Payload = ProvisioningRequestCanonicalizer.Redact(request.Payload);
        draft.UpdatedOn = DateTime.UtcNow;
        draft.Version++;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The read-then-check above lost a race with another save between the two. The
            // concurrency token is what makes that a refusal instead of a silent overwrite.
            return new ProvisioningDraftResult(
                ProvisioningDraftOutcome.VersionConflict, draft,
                "This draft was saved by another window while you were saving. Reload and try again.");
        }

        return new ProvisioningDraftResult(ProvisioningDraftOutcome.Saved, draft);
    }

    public async Task<bool> DeleteAsync(long id, string ownerEmail, CancellationToken ct = default)
    {
        var owner = Owner(ownerEmail);
        var deleted = await _db.Set<ProvisioningDraft>()
            .Where(d => d.Id == id && d.OwnerEmail == owner)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    public async Task MarkSubmittedAsync(
        long id, string ownerEmail, long executionId, CancellationToken ct = default)
    {
        var owner = Owner(ownerEmail);
        await _db.Set<ProvisioningDraft>()
            .Where(d => d.Id == id && d.OwnerEmail == owner && d.SubmittedExecutionId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.SubmittedExecutionId, executionId)
                .SetProperty(d => d.UpdatedOn, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Refused, not stripped. A caller who sent a credential believes it was saved, and a draft
    /// that silently discarded it would leave them expecting a password to work that was never
    /// stored. The credential path is separate on purpose: on the invite path none exists until
    /// the customer chooses one, and on the password path it is supplied at submit and hashed
    /// immediately.
    /// </summary>
    private static string? Reject(ProvisionTenantRequest? payload)
    {
        if (payload is null)
            return "A draft needs a payload, even an empty one.";

        if (!string.IsNullOrEmpty(payload.AdminPassword))
            return "adminPassword cannot be saved in a draft. Drafts are stored server-side and hold " +
                   "no credentials by design; supply the password at submit, or use the invite " +
                   "activation path where no credential exists until the administrator chooses one.";

        return null;
    }

    private static string ResolveName(SaveProvisioningDraftRequest request, string? fallback = null)
    {
        var explicitName = request.Name?.Trim();
        if (!string.IsNullOrEmpty(explicitName))
            return Truncate(explicitName);

        var fromPayload = request.Payload?.Name?.Trim();
        if (!string.IsNullOrEmpty(fromPayload))
            return Truncate(fromPayload);

        return fallback is { Length: > 0 } ? fallback : "Untitled tenant";
    }

    private static string Owner(string ownerEmail)
        => string.IsNullOrWhiteSpace(ownerEmail)
            ? throw new ArgumentException("A draft owner is required.", nameof(ownerEmail))
            : ownerEmail.Trim();

    private static string Truncate(string value) => value.Length <= 256 ? value : value[..256];
}
