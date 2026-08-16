using System;
using System.Collections.Generic;
using System.Linq;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// The verifier's verdict, as a TYPE rather than a sentence.
///
/// <para>Callers branch on this to decide whether a message may be scheduled, held or sent to
/// review. Making them parse free text to reach that decision is how a hold becomes a
/// substring match that silently stops matching after someone improves the wording.</para>
/// </summary>
public enum EmailManifestVerdict
{
    /// <summary>The stored original matches what was recorded. Safe to proceed.</summary>
    Compatible,

    /// <summary>Captured under a planner contract this build does not understand. Fail closed.</summary>
    ManifestVersionUnsupported,

    /// <summary>The raw evidence does not hash to what was recorded. Nothing may be trusted.</summary>
    RawEvidenceHashMismatch,

    /// <summary>A persisted component has no counterpart in the re-plan.</summary>
    ComponentMissing,

    /// <summary>The re-plan produced a component the persisted manifest does not know about.</summary>
    UnexpectedComponent,

    /// <summary>Two persisted components share one key — identity is not unique.</summary>
    DuplicateComponentKey,

    /// <summary>Ordinals are not a dense 0..n-1 sequence, so a component is missing or doubled.</summary>
    NonDenseOrdinals,

    /// <summary>Same key, but ordinal, kind or nesting depth differ — it is a different part.</summary>
    ComponentIdentityMismatch,

    /// <summary>Same part, but filename, MIME type, disposition, hash, size or reason differ.</summary>
    ComponentMetadataMismatch,

    /// <summary>Persisted component count disagrees with the assembly's expected count.</summary>
    ExpectedCountMismatch
}

/// <param name="ComponentKey">Null when the disagreement is about the manifest as a whole.</param>
public sealed record EmailManifestMismatch(
    EmailManifestVerdict Kind, string? ComponentKey, string Detail);

/// <param name="Verdict">
/// The single typed answer. <see cref="EmailManifestVerdict.Compatible"/> is the ONLY value that
/// permits scheduling; every other value holds the message.
/// </param>
/// <param name="Mismatches">Everything found, for the operator record. Never auto-repaired.</param>
public sealed record EmailManifestVerification(
    EmailManifestVerdict Verdict, IReadOnlyList<EmailManifestMismatch> Mismatches)
{
    public bool IsCompatible => Verdict == EmailManifestVerdict.Compatible;
}

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
    /// <summary>
    /// Compares the persisted canonical set against a fresh plan and returns ONE typed verdict.
    ///
    /// <para>Nothing is repaired. A disagreement means the stored original is not the message we
    /// recorded, and processing a subset of a message we cannot vouch for would produce a Lead
    /// traceable to nothing the customer sent.</para>
    /// </summary>
    /// <param name="expectedComponentCount">
    /// The assembly's recorded expectation. Compared separately from the row count so that
    /// "rows were lost" and "the plan changed" stay distinguishable.
    /// </param>
    public static EmailManifestVerification Verify(
        int persistedManifestVersion,
        int expectedComponentCount,
        IReadOnlyCollection<EmailInquiryComponent> persisted,
        EmailInquiryManifest plan)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(plan);

        var mismatches = new List<EmailManifestMismatch>();

        // Reported ALONE and first: when the planning rules themselves changed, every downstream
        // difference is a consequence of that one fact and listing them would bury it.
        if (persistedManifestVersion != plan.ContractVersion)
            return Single(EmailManifestVerdict.ManifestVersionUnsupported, null,
                $"This message was captured under manifest contract v{persistedManifestVersion} "
                + $"and this build plans v{plan.ContractVersion}.");

        // Structural integrity of the persisted set, before any comparison against the plan.
        var duplicate = persisted.GroupBy(c => c.ComponentKey, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return Single(EmailManifestVerdict.DuplicateComponentKey, duplicate.Key,
                "This part is recorded more than once, so its identity is ambiguous.");

        var ordinals = persisted.Select(c => c.Ordinal).OrderBy(o => o).ToList();
        if (!ordinals.SequenceEqual(Enumerable.Range(0, persisted.Count)))
            return Single(EmailManifestVerdict.NonDenseOrdinals, null,
                "The recorded parts of this message are not a complete numbered sequence, so one "
                + "is missing or duplicated.");

        if (expectedComponentCount != persisted.Count)
            return Single(EmailManifestVerdict.ExpectedCountMismatch, null,
                $"This message was recorded as having {expectedComponentCount} part(s) but "
                + $"{persisted.Count} are stored.");

        var planned = plan.Components.ToDictionary(c => c.ComponentKey, StringComparer.Ordinal);
        var identityBroken = false;
        var metadataBroken = false;

        foreach (var component in persisted.OrderBy(c => c.Ordinal))
        {
            if (!planned.TryGetValue(component.ComponentKey, out var candidate))
            {
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMissing, component.ComponentKey,
                    "This part was recorded when the message arrived but is not present in the "
                    + "stored original."));
                continue;
            }

            // IDENTITY — position, kind and depth say WHICH part this is. A difference here means
            // the key now names something else, which is the substitution that must never pass.
            if (candidate.Ordinal != component.Ordinal)
            {
                identityBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentIdentityMismatch, component.ComponentKey,
                    $"Recorded at position {component.Ordinal}, found at position {candidate.Ordinal}."));
            }
            if (candidate.Kind != component.Kind)
            {
                identityBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentIdentityMismatch, component.ComponentKey,
                    $"Recorded as {component.Kind}, found as {candidate.Kind}."));
            }
            if (candidate.NestingDepth != component.NestingDepth)
            {
                identityBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentIdentityMismatch, component.ComponentKey,
                    $"Recorded {component.NestingDepth} level(s) deep, found at {candidate.NestingDepth}."));
            }

            // METADATA — same part, different description. Filename and MIME type are compared
            // because renaming quote.pdf to quote.htm leaves hash and size untouched while
            // re-routing the file through a different inspection path and a different extractor.
            if (!NameEquals(component.FileName, candidate.FileName))
            {
                metadataBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMetadataMismatch, component.ComponentKey,
                    "The name of this part does not match what was recorded when it arrived."));
            }
            if (!MimeEquals(component.MimeType, candidate.MimeType))
            {
                metadataBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMetadataMismatch, component.ComponentKey,
                    "The type of this part does not match what was recorded when it arrived."));
            }
            if (!DispositionMatches(component.Status, candidate.Disposition))
            {
                metadataBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMetadataMismatch, component.ComponentKey,
                    "This part was recorded with a different handling decision than the stored "
                    + "original now produces."));
            }
            // Process components acquire runtime reason codes (queue outage, extraction hold)
            // after capture. Those describe processing state, not a changed MIME disposition,
            // and must not make a byte-identical replay fail manifest verification. Capture-time
            // Skip/Ignore/Structural decisions remain exact.
            if (candidate.Disposition != EmailInquiryComponentDisposition.Process
                && !string.Equals(component.ReasonCode, candidate.ReasonCode, StringComparison.Ordinal))
            {
                metadataBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMetadataMismatch, component.ComponentKey,
                    "The reason recorded for this part no longer matches."));
            }

            // Content is compared only where it EXISTS. Skipped, ignored and structural parts
            // were deliberately never decoded, so they carry no hash or size — comparing empties
            // would manufacture a mismatch on every replay of a message containing an
            // unsupported attachment. They are still fully checked structurally above.
            if (component.ContentHash is { Length: > 0 } recordedHash
                && !string.Equals(recordedHash, candidate.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                metadataBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMetadataMismatch, component.ComponentKey,
                    "The content of this part does not match what was recorded when it arrived."));
            }
            if (component.ByteSize is { } recordedSize && recordedSize > 0
                && recordedSize != candidate.ByteSize)
            {
                metadataBroken = true;
                mismatches.Add(new EmailManifestMismatch(
                    EmailManifestVerdict.ComponentMetadataMismatch, component.ComponentKey,
                    $"Recorded as {recordedSize} bytes, found as {candidate.ByteSize} bytes."));
            }
        }

        var persistedKeys = persisted.Select(c => c.ComponentKey).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in plan.Components.Where(c => !persistedKeys.Contains(c.ComponentKey)))
            mismatches.Add(new EmailManifestMismatch(
                EmailManifestVerdict.UnexpectedComponent, extra.ComponentKey,
                "The stored original contains a part that was not recorded when the message arrived."));

        if (mismatches.Count == 0)
            return new EmailManifestVerification(EmailManifestVerdict.Compatible, mismatches);

        // Most severe wins: a missing or extra part outranks a changed description, because the
        // set itself is wrong rather than one entry within it.
        var verdict =
            mismatches.Any(m => m.Kind == EmailManifestVerdict.ComponentMissing) ? EmailManifestVerdict.ComponentMissing
            : mismatches.Any(m => m.Kind == EmailManifestVerdict.UnexpectedComponent) ? EmailManifestVerdict.UnexpectedComponent
            : identityBroken ? EmailManifestVerdict.ComponentIdentityMismatch
            : metadataBroken ? EmailManifestVerdict.ComponentMetadataMismatch
            : mismatches[0].Kind;

        return new EmailManifestVerification(verdict, mismatches);
    }

    /// <summary>Raw evidence must hash to what was recorded before any re-plan is trusted.</summary>
    public static EmailManifestVerification RawEvidenceMismatch(string detail)
        => Single(EmailManifestVerdict.RawEvidenceHashMismatch, null, detail);

    private static EmailManifestVerification Single(
        EmailManifestVerdict verdict, string? key, string detail)
        => new(verdict, [new EmailManifestMismatch(verdict, key, detail)]);

    /// <summary>
    /// Filenames are compared case-insensitively after Unicode normalization, because the same
    /// name can arrive in composed or decomposed form from different clients and neither is
    /// wrong. Nulls are equal to nulls.
    /// </summary>
    private static bool NameEquals(string? left, string? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return string.Equals(
            left.Normalize(System.Text.NormalizationForm.FormC).Trim(),
            right.Normalize(System.Text.NormalizationForm.FormC).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Media type and subtype only — parameter order varies between senders.</summary>
    private static bool MimeEquals(string? left, string? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return string.Equals(Base(left), Base(right), StringComparison.OrdinalIgnoreCase);

        static string Base(string value)
        {
            var separator = value.IndexOf(';');
            return (separator < 0 ? value : value[..separator]).Trim();
        }
    }

    /// <summary>Whether a persisted status still corresponds to the plan's disposition.</summary>
    private static bool DispositionMatches(
        EmailInquiryComponentStatus status, EmailInquiryComponentDisposition disposition)
        => disposition switch
        {
            EmailInquiryComponentDisposition.IgnoreInlineAsset => status == EmailInquiryComponentStatus.Ignored,
            // Both ignore dispositions land on the same status. They are separate dispositions
            // because the EVIDENCE differs — measured size plus a cid reference, versus an
            // unambiguous filename — and an operator asking why a part went unread deserves the
            // real answer.
            EmailInquiryComponentDisposition.IgnoreNonCommercial => status == EmailInquiryComponentStatus.Ignored,
            EmailInquiryComponentDisposition.StructuralContainer => status == EmailInquiryComponentStatus.StructuralOnly,
            EmailInquiryComponentDisposition.Skip => status == EmailInquiryComponentStatus.Skipped,
            // A Process component legitimately advances through Pending -> Extracting ->
            // Completed, and may be held or refused. What it must never be is one of the
            // dispositions that were decided at capture and can no longer change.
            _ => status is not (EmailInquiryComponentStatus.Ignored
                or EmailInquiryComponentStatus.StructuralOnly
                or EmailInquiryComponentStatus.Skipped)
        };

    /// <summary>One operator-safe sentence summarising a verification.</summary>
    public static string Describe(IReadOnlyList<EmailManifestMismatch> mismatches)
        => mismatches.Count == 0
            ? "The stored original matches what was recorded when this message arrived."
            : $"The stored original no longer matches what was recorded when this message "
              + $"arrived ({mismatches.Count} difference(s)); it is held for review rather than "
              + "processed. First difference: " + mismatches[0].Detail;
}
