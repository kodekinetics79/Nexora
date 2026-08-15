using ERP_RFQ_Automation.Ingestion.Assembly;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The gate between stored evidence and scheduling.
///
/// <para>Scheduling works from the PERSISTED component rows, but the bytes come from a plan —
/// held in memory at capture, re-derived from the stored <c>.eml</c> on recovery. If those two
/// views are not the same message, the scheduler hands the queue one part's bytes under another
/// part's identity. Every disagreement is reported as a typed verdict and none is repaired,
/// because a silently substituted component produces a Lead traceable to nothing the customer
/// sent.</para>
/// </summary>
public class EmailComponentManifestVerifierTests
{
    private const string Key = "email:m1:part:1";

    private static EmailInquiryComponent Persisted(
        string key = Key, int ordinal = 0,
        EmailInquiryComponentKind kind = EmailInquiryComponentKind.Attachment,
        EmailInquiryComponentStatus status = EmailInquiryComponentStatus.Pending,
        string? fileName = "boq.pdf", string? mime = "application/pdf",
        string? hash = "abc123", long? size = 100, int depth = 0, string? reason = null)
        => new()
        {
            ComponentKey = key, Ordinal = ordinal, Kind = kind, Status = status,
            FileName = fileName, MimeType = mime, ContentHash = hash, ByteSize = size,
            NestingDepth = depth, ReasonCode = reason
        };

    private static EmailInquiryComponentPlan Planned(
        string key = Key, int ordinal = 0,
        EmailInquiryComponentKind kind = EmailInquiryComponentKind.Attachment,
        EmailInquiryComponentDisposition disposition = EmailInquiryComponentDisposition.Process,
        string? fileName = "boq.pdf", string? mime = "application/pdf",
        string hash = "abc123", long size = 100, int depth = 0, string? reason = null)
        => new(key, kind, ordinal, fileName, mime, size, hash, disposition, reason, null, depth,
            ReadOnlyMemory<byte>.Empty);

    private static EmailInquiryManifest Manifest(params EmailInquiryComponentPlan[] plans)
        => new("m1", plans, false, EmailInquiryManifestPlanner.ContractVersion);

    private static EmailManifestVerification Verify(
        EmailInquiryComponent[] persisted, EmailInquiryManifest plan, int? expected = null, int? version = null)
        => EmailComponentManifestVerifier.Verify(
            version ?? EmailInquiryManifestPlanner.ContractVersion,
            expected ?? persisted.Length, persisted, plan);

    [Fact]
    public void An_unchanged_message_is_Compatible()
    {
        var result = Verify([Persisted()], Manifest(Planned()));

        Assert.Equal(EmailManifestVerdict.Compatible, result.Verdict);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void An_unknown_contract_version_fails_closed_and_reports_alone()
    {
        // Also carries a hash difference. When the planning rules changed, every downstream
        // difference is a consequence of that one fact and listing them would bury it.
        var result = Verify([Persisted(hash: "old")], Manifest(Planned(hash: "new")), version: 99);

        Assert.Equal(EmailManifestVerdict.ManifestVersionUnsupported, result.Verdict);
        Assert.Single(result.Mismatches);
    }

    [Fact]
    public void A_duplicate_component_key_is_refused_before_anything_is_compared()
    {
        var result = Verify([Persisted(ordinal: 0), Persisted(ordinal: 1)],
            Manifest(Planned(ordinal: 0), Planned("email:m1:part:2", 1)));

        Assert.Equal(EmailManifestVerdict.DuplicateComponentKey, result.Verdict);
    }

    [Fact]
    public void Non_dense_ordinals_are_refused()
    {
        var result = Verify(
            [Persisted(ordinal: 0), Persisted("email:m1:part:2", ordinal: 5)],
            Manifest(Planned(ordinal: 0), Planned("email:m1:part:2", 5)));

        Assert.Equal(EmailManifestVerdict.NonDenseOrdinals, result.Verdict);
    }

    [Fact]
    public void A_row_count_disagreeing_with_the_recorded_expectation_is_refused()
    {
        var result = Verify([Persisted()], Manifest(Planned()), expected: 3);

        Assert.Equal(EmailManifestVerdict.ExpectedCountMismatch, result.Verdict);
    }

    [Fact]
    public void A_persisted_part_absent_from_the_stored_original_is_ComponentMissing()
    {
        var result = Verify([Persisted()], Manifest(Planned("email:m1:part:9")));

        Assert.Equal(EmailManifestVerdict.ComponentMissing, result.Verdict);
    }

    [Fact]
    public void A_part_in_the_original_that_was_never_recorded_is_UnexpectedComponent()
    {
        var result = Verify([Persisted()], Manifest(Planned(), Planned("email:m1:part:2", 1)));

        Assert.Contains(result.Mismatches, m => m.Kind == EmailManifestVerdict.UnexpectedComponent);
    }

    [Theory]
    [InlineData(1, EmailInquiryComponentKind.Attachment, 0)]   // moved position
    [InlineData(0, EmailInquiryComponentKind.Body, 0)]          // changed kind
    [InlineData(0, EmailInquiryComponentKind.Attachment, 2)]    // changed depth
    public void A_changed_position_kind_or_depth_is_an_identity_mismatch(
        int ordinal, EmailInquiryComponentKind kind, int depth)
    {
        var result = Verify([Persisted()], Manifest(Planned(ordinal: ordinal, kind: kind, depth: depth)));

        Assert.Equal(EmailManifestVerdict.ComponentIdentityMismatch, result.Verdict);
    }

    [Fact]
    public void Renaming_a_file_is_caught_even_though_hash_and_size_are_untouched()
    {
        // quote.pdf -> quote.htm keeps the bytes identical but re-routes the file through a
        // different inspection path and a different extractor.
        var result = Verify([Persisted(fileName: "quote.pdf", mime: "application/pdf")],
            Manifest(Planned(fileName: "quote.htm", mime: "text/html")));

        Assert.Equal(EmailManifestVerdict.ComponentMetadataMismatch, result.Verdict);
    }

    [Fact]
    public void Changed_content_is_a_metadata_mismatch()
    {
        var result = Verify([Persisted(hash: "recorded")], Manifest(Planned(hash: "different")));

        Assert.Equal(EmailManifestVerdict.ComponentMetadataMismatch, result.Verdict);
    }

    [Fact]
    public void A_skipped_part_that_re_plans_as_processable_is_caught()
    {
        // The substitution that previously passed clean: a Skip row carries no hash or size, so
        // with only content compared there was nothing to notice.
        var result = Verify(
            [Persisted(status: EmailInquiryComponentStatus.Skipped, hash: null, size: 0,
                       reason: EmailInquirySkipReasons.UnsupportedFileType)],
            Manifest(Planned(disposition: EmailInquiryComponentDisposition.Process, reason: null)));

        Assert.Equal(EmailManifestVerdict.ComponentMetadataMismatch, result.Verdict);
    }

    [Fact]
    public void A_skipped_part_that_is_genuinely_unchanged_produces_no_mismatch()
    {
        // The false-alarm guard. Parts deliberately never decoded carry no hash or size, and
        // comparing empties would hold every replay of a message with an unsupported attachment.
        var result = Verify(
            [Persisted(status: EmailInquiryComponentStatus.Skipped, hash: null, size: 0,
                       reason: EmailInquirySkipReasons.UnsupportedFileType)],
            Manifest(Planned(disposition: EmailInquiryComponentDisposition.Skip,
                             hash: string.Empty, size: 0,
                             reason: EmailInquirySkipReasons.UnsupportedFileType)));

        Assert.Equal(EmailManifestVerdict.Compatible, result.Verdict);
    }

    [Fact]
    public void A_structural_container_is_verified_structurally_and_stays_Compatible()
    {
        var result = Verify(
            [Persisted(kind: EmailInquiryComponentKind.EmbeddedMessage,
                       status: EmailInquiryComponentStatus.StructuralOnly,
                       fileName: "fwd.eml", mime: "message/rfc822", hash: "h", size: 500, depth: 1,
                       reason: EmailInquirySkipReasons.StructuralContainer)],
            Manifest(Planned(kind: EmailInquiryComponentKind.EmbeddedMessage,
                             disposition: EmailInquiryComponentDisposition.StructuralContainer,
                             fileName: "fwd.eml", mime: "message/rfc822", hash: "h", size: 500, depth: 1,
                             reason: EmailInquirySkipReasons.StructuralContainer)));

        Assert.Equal(EmailManifestVerdict.Compatible, result.Verdict);
    }

    [Fact]
    public void A_processable_part_may_advance_through_its_lifecycle_without_a_mismatch()
    {
        foreach (var status in new[]
                 {
                     EmailInquiryComponentStatus.Pending,
                     EmailInquiryComponentStatus.Extracting,
                     EmailInquiryComponentStatus.Completed,
                     EmailInquiryComponentStatus.FailedRecoverable
                 })
        {
            var result = Verify([Persisted(status: status)], Manifest(Planned()));
            Assert.Equal(EmailManifestVerdict.Compatible, result.Verdict);
        }
    }

    [Fact]
    public void Filenames_differing_only_by_unicode_form_or_case_are_not_a_mismatch()
    {
        // The same name arrives composed or decomposed from different clients; neither is wrong.
        var result = Verify(
            [Persisted(fileName: "Devis-été.pdf")],
            Manifest(Planned(fileName: "devis-été.pdf")));

        Assert.Equal(EmailManifestVerdict.Compatible, result.Verdict);
    }

    [Fact]
    public void Mime_types_differing_only_by_parameters_are_not_a_mismatch()
    {
        var result = Verify(
            [Persisted(mime: "application/pdf")],
            Manifest(Planned(mime: "application/pdf; name=boq.pdf")));

        Assert.Equal(EmailManifestVerdict.Compatible, result.Verdict);
    }

    [Fact]
    public void A_raw_evidence_hash_mismatch_is_its_own_typed_verdict()
    {
        var result = EmailComponentManifestVerifier.RawEvidenceMismatch(
            "The stored original does not match its recorded fingerprint.");

        Assert.Equal(EmailManifestVerdict.RawEvidenceHashMismatch, result.Verdict);
        Assert.False(result.IsCompatible);
    }

    [Fact]
    public void Nothing_is_ever_repaired()
    {
        // The verifier reports. It has no mutating surface at all — a caller cannot ask it to
        // reconcile, and there is no overload that returns a corrected set.
        var persisted = Persisted(hash: "recorded");
        Verify([persisted], Manifest(Planned(hash: "different")));

        Assert.Equal("recorded", persisted.ContentHash);
        Assert.Equal(Key, persisted.ComponentKey);
    }
}
