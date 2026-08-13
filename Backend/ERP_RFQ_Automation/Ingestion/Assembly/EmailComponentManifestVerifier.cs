using System;
using System.Collections.Generic;
using System.Linq;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>Why a persisted manifest and a freshly planned one disagree.</summary>
public enum EmailManifestMismatchKind
{
    /// <summary>The planner contract changed since this message was captured.</summary>
    ContractVersion,

    /// <summary>A persisted component has no counterpart in the plan.</summary>
    MissingFromPlan,

    /// <summary>The plan produced a component the persisted manifest does not know about.</summary>
    UnexpectedInPlan,

    /// <summary>Same key, different position — ordering is part of identity.</summary>
    Ordinal,

    /// <summary>Same key, different kind of part.</summary>
    Kind,

    /// <summary>Same key, different bytes.</summary>
    ContentHash,

    /// <summary>Same key, different size.</summary>
    ByteSize
}

/// <param name="ComponentKey">Null when the disagreement is about the manifest as a whole.</param>
public sealed record EmailManifestMismatch(
    EmailManifestMismatchKind Kind, string? ComponentKey, string Detail);

/// <summary>
/// Compares the components a message was captured with against the components a fresh plan of
/// the same raw bytes produces.
///
/// <para><b>Why this exists.</b> Scheduling always works from the PERSISTED component rows —
/// they are the identity of record — but the bytes to schedule come from a plan held in memory
/// (first capture) or re-derived from the stored <c>.eml</c> (recovery). Those two views must
/// be the same message or the scheduler would hand the queue one part's bytes under another
/// part's identity. That is not a theoretical concern: it is exactly what happens if the
/// planner's rules change while messages are mid-flight, or if stored evidence is altered.</para>
///
/// <para>Any disagreement is reported, never repaired. Silently dropping, substituting or
/// reordering a component would produce a Lead built from content nobody can trace back to what
/// the customer actually sent.</para>
/// </summary>
public static class EmailComponentManifestVerifier
{
    public static IReadOnlyList<EmailManifestMismatch> Verify(
        int persistedManifestVersion,
        IReadOnlyCollection<EmailInquiryComponent> persisted,
        EmailInquiryManifest plan)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(plan);

        var mismatches = new List<EmailManifestMismatch>();

        // Checked first and reported alone: when the rules themselves changed, every downstream
        // difference is a consequence of that one fact, and listing them all would bury it.
        if (persistedManifestVersion != plan.ContractVersion)
        {
            mismatches.Add(new EmailManifestMismatch(
                EmailManifestMismatchKind.ContractVersion, null,
                $"This message was captured under manifest contract v{persistedManifestVersion} "
                + $"and re-planned under v{plan.ContractVersion}."));
            return mismatches;
        }

        var planned = plan.Components.ToDictionary(c => c.ComponentKey, StringComparer.Ordinal);

        foreach (var component in persisted.OrderBy(c => c.Ordinal))
        {
            if (!planned.TryGetValue(component.ComponentKey, out var candidate))
            {
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestMismatchKind.MissingFromPlan, component.ComponentKey,
                    "This part was recorded when the message arrived but is not present in the "
                    + "stored original."));
                continue;
            }

            if (candidate.Ordinal != component.Ordinal)
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestMismatchKind.Ordinal, component.ComponentKey,
                    $"Recorded at position {component.Ordinal}, found at position {candidate.Ordinal}."));

            if (candidate.Kind != component.Kind)
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestMismatchKind.Kind, component.ComponentKey,
                    $"Recorded as {component.Kind}, found as {candidate.Kind}."));

            // Skipped and ignored parts were never decoded, so they carry no hash or size to
            // compare. Comparing empties would manufacture a mismatch on every replay of a
            // message that happened to contain an unsupported attachment.
            if (component.ContentHash is { Length: > 0 } recordedHash
                && !string.Equals(recordedHash, candidate.ContentHash, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestMismatchKind.ContentHash, component.ComponentKey,
                    "The content of this part does not match what was recorded when it arrived."));

            if (component.ByteSize is { } recordedSize && recordedSize > 0
                && recordedSize != candidate.ByteSize)
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestMismatchKind.ByteSize, component.ComponentKey,
                    $"Recorded as {recordedSize} bytes, found as {candidate.ByteSize} bytes."));
        }

        var persistedKeys = persisted.Select(c => c.ComponentKey).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in plan.Components.Where(c => !persistedKeys.Contains(c.ComponentKey)))
            mismatches.Add(new EmailManifestMismatch(
                EmailManifestMismatchKind.UnexpectedInPlan, extra.ComponentKey,
                "The stored original contains a part that was not recorded when the message arrived."));

        return mismatches;
    }

    /// <summary>One operator-safe sentence summarising a set of mismatches.</summary>
    public static string Describe(IReadOnlyList<EmailManifestMismatch> mismatches)
        => mismatches.Count == 0
            ? "The stored original matches what was recorded when this message arrived."
            : $"The stored original no longer matches what was recorded when this message "
              + $"arrived ({mismatches.Count} difference(s)); it is held for review rather than "
              + "processed. First difference: " + mismatches[0].Detail;
}
