using System.Reflection;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadParticipationPromotionBoundaryTests
{
    [Fact]
    public void Frontend_decision_workbench_routes_are_present()
    {
        var routes = typeof(LeadParticipationController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()
                .Select(attribute => $"{string.Join(',', attribute.HttpMethods)} {attribute.Template}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("GET decision-workbench", routes);
        Assert.Contains("PUT fit-assessment", routes);
        Assert.Contains("PUT participation", routes);
        Assert.Contains("POST promote-to-rfq", routes);
    }

    [Fact]
    public async Task Commercial_decision_records_are_append_only_in_application_runtime()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(9901);
        var assessment = new LeadFitAssessment
        {
            Id = 77,
            BusinessUnitId = 9901,
            LeadId = 1,
            LeadRevisionId = 2,
            Sequence = 1,
            PolicyVersion = "test",
            Recommendation = "FIT",
            IsActionable = true,
            AssessmentJson = "{}",
            IdempotencyKey = "fit:test",
            RequestHash = new string('a', 64),
            AssessedBy = "reviewer@example.com",
            AssessedAtUtc = DateTimeOffset.UtcNow
        };
        context.Attach(assessment);
        context.Entry(assessment).State = EntityState.Modified;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_document_intelligence_or_legacy_conversion_source_can_insert_a_formal_rfq()
    {
        var root = FindRepositoryRoot();
        var forbidden = new[]
        {
            "Backend/ERP_RFQ_Automation/Intelligence/Conversion/LeadConversionIntelligence.cs",
            "Backend/ERP_RFQ_Automation/Services/RfqUploaderService.cs",
            "Backend/ERP_RFQ_Automation/Services/ManualUploadService.cs",
            "Backend/ERP_RFQ_Automation/Repositories/LeadRepository.cs"
        };

        foreach (var relative in forbidden)
            Assert.DoesNotContain(".Rfqs.Add(", File.ReadAllText(Path.Combine(root, relative)), StringComparison.Ordinal);

        var production = Path.Combine(root, "Backend/ERP_RFQ_Automation");
        var insertFiles = Directory.EnumerateFiles(production, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(".Rfqs.Add(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .OrderBy(file => file)
            .ToArray();

        Assert.Equal(new[]
        {
            "Backend/ERP_RFQ_Automation/CommercialCases/Promotion/RfqPromotionService.cs"
        }, insertFiles);
    }

    [Fact]
    public void Focused_migration_contains_only_the_intended_schema_slice()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(root,
            "Backend/ERP_RFQ_Automation/MigrationsBaseline/20260825043000_LeadParticipationAndRfqPromotion.cs"));

        Assert.DoesNotContain("AlterColumn", migration, StringComparison.Ordinal);
        Assert.Contains("LeadFitAssessments", migration, StringComparison.Ordinal);
        Assert.Contains("LeadParticipationDecisions", migration, StringComparison.Ordinal);
        Assert.Contains("LeadLineParticipationDecisions", migration, StringComparison.Ordinal);
        Assert.Contains("RfqPromotions", migration, StringComparison.Ordinal);
        Assert.Contains("TR_LeadRevisions_AppendOnly", migration, StringComparison.Ordinal);
        Assert.Contains("TR_LeadItemRevisions_AppendOnly", migration, StringComparison.Ordinal);
        Assert.Contains("CK_RFQ_LeadPromotionLineage", migration, StringComparison.Ordinal);
        Assert.Contains("SourceBusinessUnitId", migration, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (\"SourceBusinessUnitId\", \"SourceLeadItemRevisionId\", \"SourceLeadRevisionId\", \"SourceLeadId\")", migration, StringComparison.Ordinal);
        foreach (var table in new[] { "LeadFitAssessments", "LeadParticipationDecisions", "LeadLineParticipationDecisions", "RfqPromotions" })
        {
            Assert.Contains($"ALTER TABLE public.\"{table}\" ENABLE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
            Assert.Contains($"ALTER TABLE public.\"{table}\" FORCE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        }
        Assert.Contains("TO nexora_tenant_app", migration, StringComparison.Ordinal);
        Assert.Contains("nexora_tenant_purge", migration, StringComparison.Ordinal);
        Assert.Contains("FK_LeadParticipationDecisions_FitConsistency", migration, StringComparison.Ordinal);
        Assert.Contains("FK_RfqPromotions_DecisionConsistency", migration, StringComparison.Ordinal);
        Assert.Contains("FK_RFQ_PromotionConsistency", migration, StringComparison.Ordinal);
        Assert.Contains("FK_RFQItems_ParentSourceConsistency", migration, StringComparison.Ordinal);
        Assert.Contains("FK_LeadLineParticipationDecisions_DecisionConsistency", migration, StringComparison.Ordinal);
        Assert.Contains("FK_LeadLineParticipationDecisions_RevisionLineConsistency", migration, StringComparison.Ordinal);
        Assert.Contains("CK_LeadLineParticipationDecisions_BidCommercialIdentity", migration, StringComparison.Ordinal);
        Assert.Contains("CK_LeadLineParticipationDecisions_NoBidReason", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("CK_LeadLineParticipationDecisions_Reason", migration, StringComparison.Ordinal);
        Assert.Contains("\"Choice\" <> 'Bid'", migration, StringComparison.Ordinal);
        Assert.Contains("\"UomId\" IS NOT NULL", migration, StringComparison.Ordinal);
        Assert.Contains("\"CurrencyId\" IS NOT NULL", migration, StringComparison.Ordinal);
        Assert.True(
            migration.LastIndexOf("DROP CONSTRAINT IF EXISTS \"FK_LeadItems_EvidenceSource\"", StringComparison.Ordinal)
            < migration.LastIndexOf("DROP CONSTRAINT IF EXISTS \"AK_LeadItems_Lead_Id\"", StringComparison.Ordinal),
            "Rollback must drop the dependent evidence-source FK before its alternate key.");
        Assert.Contains("\"LeadId\" bigint NOT NULL", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_model_and_hand_written_migration_use_the_same_tables_and_lineage_columns()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);
        var expectedTables = new Dictionary<Type, string>
        {
            [typeof(LeadFitAssessment)] = "LeadFitAssessments",
            [typeof(LeadParticipationDecision)] = "LeadParticipationDecisions",
            [typeof(LeadLineParticipationDecision)] = "LeadLineParticipationDecisions",
            [typeof(RfqPromotion)] = "RfqPromotions"
        };
        var migration = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/MigrationsBaseline/20260825043000_LeadParticipationAndRfqPromotion.cs"));

        foreach (var (type, table) in expectedTables)
        {
            Assert.Equal(table, context.Model.FindEntityType(type)?.GetTableName());
            Assert.Contains($"CREATE TABLE \"{table}\"", migration, StringComparison.Ordinal);
        }

        Assert.NotNull(context.Model.FindEntityType(typeof(Rfq))?.FindProperty(nameof(Rfq.PromotionId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Rfq))?.FindProperty(nameof(Rfq.SourceLeadRevisionId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Rfq))?.FindProperty(nameof(Rfq.ParticipationDecisionId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Rfqitem))?.FindProperty(nameof(Rfqitem.SourceLeadItemRevisionId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Rfqitem))?.FindProperty(nameof(Rfqitem.SourceBusinessUnitId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Rfqitem))?.FindProperty(nameof(Rfqitem.SourceLeadId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(Rfqitem))?.FindProperty(nameof(Rfqitem.SourceLeadRevisionId)));
        Assert.NotNull(context.Model.FindEntityType(typeof(LeadItemRevision))?.FindProperty(nameof(LeadItemRevision.LeadItemId)));
        var participationLineType = context.Model.FindEntityType(typeof(LeadLineParticipationDecision))!;
        Assert.NotNull(participationLineType.FindProperty(nameof(LeadLineParticipationDecision.LeadId)));
        Assert.NotNull(participationLineType.FindProperty(nameof(LeadLineParticipationDecision.LeadRevisionId)));
        Assert.Contains(participationLineType.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(LeadParticipationDecision)
            && fk.Properties.Select(x => x.Name).SequenceEqual(new[]
            {
                nameof(LeadLineParticipationDecision.BusinessUnitId),
                nameof(LeadLineParticipationDecision.ParticipationDecisionId),
                nameof(LeadLineParticipationDecision.LeadId),
                nameof(LeadLineParticipationDecision.LeadRevisionId)
            }));
        Assert.Contains(participationLineType.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(LeadItemRevision)
            && fk.Properties.Select(x => x.Name).SequenceEqual(new[]
            {
                nameof(LeadLineParticipationDecision.BusinessUnitId),
                nameof(LeadLineParticipationDecision.LeadItemRevisionId),
                nameof(LeadLineParticipationDecision.LeadRevisionId),
                nameof(LeadLineParticipationDecision.LeadId)
            }));
        Assert.Contains("\"PromotionId\" bigint", migration, StringComparison.Ordinal);
        Assert.Contains("\"SourceLeadRevisionId\" bigint", migration, StringComparison.Ordinal);
        Assert.Contains("\"ParticipationDecisionId\" bigint", migration, StringComparison.Ordinal);
        Assert.Contains("\"SourceLeadItemRevisionId\" bigint", migration, StringComparison.Ordinal);
        Assert.Contains("\"SourceBusinessUnitId\" bigint", migration, StringComparison.Ordinal);
        var lineType = context.Model.FindEntityType(typeof(Rfqitem))!;
        var revisionLineForeignKey = Assert.Single(lineType.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(LeadItemRevision));
        Assert.Equal(
            new[] { nameof(Rfqitem.SourceBusinessUnitId), nameof(Rfqitem.SourceLeadItemRevisionId), nameof(Rfqitem.SourceLeadRevisionId), nameof(Rfqitem.SourceLeadId) },
            revisionLineForeignKey.Properties.Select(x => x.Name));
        // The nullable promoted-parent tuple is deliberately database-only. Modelling it as
        // an EF alternate key makes ordinary legacy RFQs with null lineage unsaveable.
        Assert.Contains("FK_RFQItems_ParentSourceConsistency", migration, StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (\"SourceBusinessUnitId\", \"RFQID\", \"SourceLeadId\", \"SourceLeadRevisionId\")",
            migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_requires_each_governed_criterion_exactly_once()
    {
        Assert.True(LeadParticipationService.HasExactGovernedFitCriteria(
            new[] { "eligibility", "capability", "delivery", "compliance", "commercial" }));
        Assert.False(LeadParticipationService.HasExactGovernedFitCriteria(new[] { "X" }));
        Assert.False(LeadParticipationService.HasExactGovernedFitCriteria(
            new[] { "eligibility", "eligibility", "delivery", "compliance", "commercial" }));
        Assert.False(LeadParticipationService.HasExactGovernedFitCriteria(
            new[] { "eligibility", "capability", "delivery", "compliance", "commercial", "extra" }));
    }

    [Fact]
    public void Promotion_and_workbench_never_map_revision_lines_by_position()
    {
        var root = FindRepositoryRoot();
        var promotion = File.ReadAllText(Path.Combine(root,
            "Backend/ERP_RFQ_Automation/CommercialCases/Promotion/RfqPromotionService.cs"));
        var workbench = File.ReadAllText(Path.Combine(root,
            "Backend/ERP_RFQ_Automation/CommercialCases/Participation/LeadDecisionWorkbenchService.cs"));

        Assert.Contains("revisionLine.LeadItemId", promotion, StringComparison.Ordinal);
        Assert.Contains("item.LeadItemId", workbench, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber - 1", promotion, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber - 1", workbench, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_and_participation_unique_key_races_replay_only_through_hash_verification()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/CommercialCases/Participation/LeadParticipationService.cs"));
        Assert.True(source.Split("catch (DbUpdateException)", StringSplitOptions.None).Length - 1 >= 2);
        Assert.Contains("return FitReplay(replay, requestHash)", source, StringComparison.Ordinal);
        Assert.Contains("return DecisionReplay(replay, requestHash)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FIT", new[] { "PASS", "NOT_APPLICABLE" }, true)]
    [InlineData("CONDITIONAL", new[] { "PASS", "PASS" }, true)]
    [InlineData("FIT", new[] { "PASS", "UNKNOWN" }, false)]
    [InlineData("CONDITIONAL", new[] { "PASS", "CONCERN" }, false)]
    [InlineData("NOT_FIT", new[] { "PASS", "PASS" }, false)]
    public void Fit_actionability_fails_closed_for_unresolved_or_concern_criteria(
        string overall, string[] criteria, bool expected)
    {
        Assert.Equal(expected, LeadParticipationService.IsHumanFitActionable(overall, criteria));
    }

    [Fact]
    public void Compiled_migration_assembly_discovers_the_participation_promotion_migration()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);

        Assert.Contains("20260825043000_LeadParticipationAndRfqPromotion", context.Database.GetMigrations());
    }

    [Fact]
    public void Source_document_download_is_record_addressed_tenant_authorized_and_digest_verified()
    {
        var route = typeof(FileController).GetMethod(nameof(FileController.DownloadSourceDocument))!
            .GetCustomAttributes<HttpMethodAttribute>()
            .Single();
        Assert.Equal("source-document/{sourceDocumentId:long}", route.Template);

        var root = FindRepositoryRoot();
        var controller = File.ReadAllText(Path.Combine(root,
            "Backend/ERP_RFQ_Automation/Controllers/FileController.cs"));
        Assert.Contains("link.SourceDocumentId == sourceDocumentId", controller, StringComparison.Ordinal);
        Assert.Contains("occurrence.SourceDocumentId == sourceDocumentId", controller, StringComparison.Ordinal);
        Assert.Contains("document.PurgeState != EvidencePurgeState.Present", controller, StringComparison.Ordinal);
        Assert.Contains("job.StoragePath, document.ContentHash", controller, StringComparison.Ordinal);
        var sourceMethod = controller[(controller.IndexOf("DownloadSourceDocument", StringComparison.Ordinal))..];
        Assert.DoesNotContain("OriginalFileName ==", sourceMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Promotion_requires_current_occurrence_retained_source_evidence_for_each_bid_line()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/CommercialCases/Promotion/RfqPromotionService.cs"));
        Assert.Contains("x.OccurrenceId == currentOccurrenceId", source, StringComparison.Ordinal);
        Assert.Contains("currentSourceDocumentIds.Contains(field.ExtractionRun.SourceDocumentId)", source, StringComparison.Ordinal);
        Assert.Contains("field.ExtractionRun.SourceDocument.PurgeState == EvidencePurgeState.Present", source, StringComparison.Ordinal);
        Assert.Contains("field.ExtractionRun.SourceDocument.ExtractionJobId == job.Id", source, StringComparison.Ordinal);
        Assert.Contains("_evidenceStorage.OpenVerifiedReadAsync", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("_evidenceStorage.OpenVerifiedReadAsync", StringComparison.Ordinal)
            < source.IndexOf("_db.Rfqs.Add", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Backend", "ERP_RFQ_Automation")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nexora repository root.");
    }
}
