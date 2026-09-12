using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence.Learning;

/// <summary>
/// One spelling a tenant's reviewer taught the parser: "this label on this customer's documents
/// means this field". Table <c>header_spellings</c>, one row per tenant and spelling.
///
/// <para><b>Why a table and not a bigger built-in list.</b> The built-in vocabulary can only ever
/// hold the spellings we have seen. A customer that heads its closing-date column "BCD", or writes
/// its labels in Arabic, sends the same export every time; the first time a rep confirms the date
/// against the document, the label's meaning is known for that tenant for good, and the second
/// file goes straight through. The knowledge is the tenant's — learned from its reviewers, scoped
/// to it, and never shared.</para>
///
/// <para><b>Overlay, not authority.</b> Rows are read through <see cref="RfqHeaderVocabulary.WithLearned"/>,
/// which drops any learned spelling the built-in list already knows. A mistaken confirmation
/// can add a spelling; it can never re-route one the parser already reads correctly.</para>
/// </summary>
public sealed class HeaderSpelling
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>The spelling after <see cref="RfqHeaderVocabulary.Normalize"/>: the lookup key.</summary>
    public string Spelling { get; set; } = string.Empty;

    /// <summary>The label exactly as the customer wrote it, for the reviewer's eyes.</summary>
    public string OriginalLabel { get; set; } = string.Empty;

    /// <summary>The <see cref="RfqSpreadsheetFields"/> name the spelling maps to.</summary>
    public string Field { get; set; } = string.Empty;

    public long? LearnedFromLeadId { get; set; }
    public long? LearnedFromReviewAuditId { get; set; }

    /// <summary>How many reviews have confirmed this reading.</summary>
    public int ObservationCount { get; set; } = 1;

    public DateTime CreatedOn { get; set; }
    public DateTime LastObservedOn { get; set; }
}

public static class HeaderSpellingModelBuilderExtensions
{
    public static ModelBuilder AddHeaderSpellings(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HeaderSpelling>(entity =>
        {
            entity.ToTable("header_spellings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Spelling).HasMaxLength(200).IsRequired();
            entity.Property(x => x.OriginalLabel).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Field).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ObservationCount).HasDefaultValue(1);
            entity.HasIndex(x => new { x.BusinessUnitId, x.Spelling })
                .IsUnique()
                .HasDatabaseName("ux_header_spellings_tenant_spelling");
            entity.HasIndex(x => new { x.BusinessUnitId, x.LearnedFromLeadId })
                .HasFilter("\"LearnedFromLeadId\" IS NOT NULL")
                .HasDatabaseName("ix_header_spellings_learned_from_lead");
        });
        return modelBuilder;
    }
}

/// <summary>The vocabulary a tenant's documents are read with: built-in plus what its reviewers taught.</summary>
public interface ITenantHeaderVocabulary
{
    Task<RfqHeaderVocabulary> ForBusinessUnitAsync(long businessUnitId, CancellationToken ct = default);
}

public sealed class TenantHeaderVocabulary : ITenantHeaderVocabulary
{
    private readonly ErpRfqAutomationContext _db;

    public TenantHeaderVocabulary(ErpRfqAutomationContext db) => _db = db;

    public async Task<RfqHeaderVocabulary> ForBusinessUnitAsync(long businessUnitId, CancellationToken ct = default)
    {
        // The extraction worker reads outside a pushed tenant scope, so the predicate is explicit
        // rather than left to the query filter.
        var learned = await _db.Set<HeaderSpelling>()
            .AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .OrderBy(x => x.Id)
            .Select(x => new LearnedHeaderSpelling(x.Spelling, x.Field))
            .ToListAsync(ct);

        return learned.Count == 0 ? RfqHeaderVocabulary.Builtin : RfqHeaderVocabulary.Builtin.WithLearned(learned);
    }
}
