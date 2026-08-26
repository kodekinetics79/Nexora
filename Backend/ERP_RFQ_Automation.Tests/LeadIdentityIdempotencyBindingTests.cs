using System.Text.Json;
using ERP_RFQ_Automation.LeadIdentity;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadIdentityIdempotencyBindingTests
{
    [Fact]
    public void Ingestion_replay_is_bound_to_the_original_payload()
    {
        var intake = Intake(externalSourceId: "message-1", contentHash: "hash-1");
        var occurrence = new LeadIngestionOccurrence
        {
            SourceChannel = intake.SourceChannel,
            ExternalSourceId = intake.ExternalSourceId,
            ContentHash = intake.ContentHash,
            LogicalInquiryFingerprint = "fingerprint-1"
        };

        LeadIdentityIdempotencyBinding.EnsureReconciliationReplay(occurrence, intake, "fingerprint-1");

        var conflict = Assert.Throws<InvalidOperationException>(() =>
            LeadIdentityIdempotencyBinding.EnsureReconciliationReplay(
                occurrence, Intake(externalSourceId: "message-2", contentHash: "hash-2"), "fingerprint-2"));
        Assert.Contains("different inquiry payload", conflict.Message);
    }

    [Fact]
    public void Match_review_replay_is_bound_to_occurrence_candidate_action_and_version()
    {
        var request = new MatchDecisionRequest("revision", 44, 3, "Buyer sent an amendment.", "match-key");
        var audit = new LeadIdentityAuditEvent
        {
            EventType = "POSSIBLE_MATCH_DECIDED",
            OccurrenceId = 17,
            PayloadJson = JsonSerializer.Serialize(new
            {
                request.Action, request.Reason, request.CandidateLeadId, request.ExpectedVersion
            })
        };

        LeadIdentityIdempotencyBinding.EnsureMatchDecisionReplay(audit, 17, request);

        Assert.Throws<InvalidOperationException>(() => LeadIdentityIdempotencyBinding.EnsureMatchDecisionReplay(
            audit, 17, request with { Action = "exact_duplicate" }));
        Assert.Throws<InvalidOperationException>(() => LeadIdentityIdempotencyBinding.EnsureMatchDecisionReplay(
            audit, 18, request));
    }

    [Fact]
    public void Human_revision_replay_cannot_return_a_revision_from_another_lead()
    {
        var occurrence = new LeadIngestionOccurrence
        {
            LeadId = 9,
            RecordKind = LeadOccurrenceRecordKind.IdentityBaseline,
            SourceChannel = "HumanCorrection"
        };

        LeadIdentityIdempotencyBinding.EnsureHumanRevisionReplay(occurrence, 9);
        Assert.Throws<InvalidOperationException>(() =>
            LeadIdentityIdempotencyBinding.EnsureHumanRevisionReplay(occurrence, 10));
    }

    private static LeadIntakeDescriptor Intake(string externalSourceId, string contentHash) => new(
        BatchId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        SourceChannel: "Email",
        IdempotencyKey: "ingestion-key",
        ExternalSourceId: externalSourceId,
        EmailThreadId: "thread-1",
        SourceSystem: "ConfiguredMailbox",
        Sender: "buyer@example.test",
        Subject: "RFQ",
        OriginalFileName: null,
        MimeType: "message/rfc822",
        FileSize: 100,
        ContentHash: contentHash,
        SourceDocumentId: 1,
        ExtractionJobId: 2,
        SourceReceivedAtUtc: DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
        IngestedAtUtc: DateTimeOffset.Parse("2026-08-25T12:01:00Z"),
        ProcessingPath: LeadProcessingPath.Deterministic,
        ExternalAiUsed: false,
        ExternalCost: null,
        ActorType: "Service",
        ActorId: "mailbox-worker",
        CorrelationId: "correlation-1");
}
