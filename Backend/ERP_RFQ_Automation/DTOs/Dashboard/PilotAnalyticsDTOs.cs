using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.Dashboard;

// ════════════════════════════════════════════════════════════════════════════
// Pilot analytics. Three views, chosen because every one of them is computable
// from data that already exists in production and none of them needs customer
// identity, a product catalog, FX rates, or lifecycle events — the four things
// the pilot tenant will not have on day one.
//
// Every rate on these payloads ships with its numerator and denominator. A bare
// percentage is not reportable: "98%" and "49 of 50" are the same number, but
// only one of them lets a reader see that the sample is fifty.
// ════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------- deadline board

/// <param name="Key">Stable bucket key for the client: overdue|today|days_1_3|days_4_7|days_8_30|later|unknown.</param>
/// <param name="Leads">Open leads in this bucket.</param>
/// <param name="LineItems">Line items across those leads — the actual work the bucket represents.</param>
public sealed record DeadlineBucketDTO(
    string Key,
    string Label,
    int Leads,
    int LineItems);

public sealed record DeadlineLeadDTO(
    long LeadId,
    string? Rfqno,
    string? BuyersName,
    DateTime? BidClosingDate,
    int? DaysLeft,
    string Bucket,
    int LineItems,
    bool AwaitingReview,
    bool LateIngested);

/// <summary>
/// Forward-looking workload: which enquiries close when, and how much line-item work
/// each one carries. Sorted by urgency, not by arrival.
/// </summary>
/// <param name="LeadsWithoutClosingDate">
/// Open leads carrying no usable bid closing date. Reported as its own number rather
/// than folded into a "later" bucket — a missing deadline is a data gap the reviewer
/// can close by asking the buyer, not a comfortable deadline.
/// </param>
/// <param name="LateIngestedExcludedLeads">
/// Open leads that entered Nexora AFTER their own bid closing date. They are shown in
/// the overdue bucket but excluded from any aging judgement, because arriving late is
/// not the same as being handled late. Disclosed here so the exclusion is never silent.
/// </param>
public sealed record DeadlineBoardDTO(
    DateTime GeneratedAt,
    int OpenLeads,
    int OpenLineItems,
    int LeadsWithoutClosingDate,
    int LateIngestedExcludedLeads,
    IReadOnlyList<DeadlineBucketDTO> Buckets,
    IReadOnlyList<DeadlineLeadDTO> Leads);

// ---------------------------------------------------------------- brand demand

/// <param name="Manufacturer">Display form — the most frequent raw spelling in this group.</param>
/// <param name="NormalizedKey">The key the grouping was performed on.</param>
/// <param name="Variants">How many distinct raw spellings collapsed into this group.</param>
/// <param name="Documents">Distinct source enquiries that asked for this brand — the honest denominator.</param>
/// <param name="TotalQuantity">Summed line quantities. Units are NOT comparable across lines; see the payload note.</param>
public sealed record BrandDemandRowDTO(
    string Manufacturer,
    string NormalizedKey,
    int Variants,
    int Lines,
    int Documents,
    long TotalQuantity,
    decimal LineSharePercent);

/// <param name="LinesWithoutManufacturer">
/// Lines carrying no manufacturer at all. Part of the denominator and stated, because
/// concentration computed only over the lines that happen to name a brand would
/// overstate how much of the book those brands represent.
/// </param>
/// <param name="QuantityCaveat">
/// Quantities are summed across mixed units of measure and are indicative only.
/// </param>
public sealed record BrandDemandDTO(
    DateTime GeneratedAt,
    DateTime? From,
    DateTime? To,
    int TotalLines,
    int LinesWithManufacturer,
    int LinesWithoutManufacturer,
    int DistinctManufacturers,
    int DistinctRawSpellings,
    decimal TopFiveLineSharePercent,
    IReadOnlyList<BrandDemandRowDTO> Rows,
    string QuantityCaveat);

// ---------------------------------------------------------------- yield & funnel

/// <param name="Numerator">Items that reached this stage.</param>
/// <param name="Denominator">Items that entered the PREVIOUS stage. Null on the first stage.</param>
public sealed record FunnelStageDTO(
    string Key,
    string Label,
    long Numerator,
    long? Denominator,
    decimal? StagePercent,
    string Definition);

/// <param name="Covered">Records carrying the artefact.</param>
/// <param name="Total">Records that could carry it.</param>
public sealed record CoverageTileDTO(
    string Key,
    string Label,
    long Covered,
    long Total,
    decimal? Percent,
    string Definition);

/// <summary>
/// Document yield and the review funnel in one payload, because yield and quality are
/// one question: a document that produced no lead is not a fast extraction, it is a
/// lost enquiry, and a line with no preserved source column is not a clean line.
/// </summary>
public sealed record DocumentYieldDTO(
    DateTime GeneratedAt,
    DateTime From,
    DateTime To,
    IReadOnlyList<FunnelStageDTO> Stages,
    IReadOnlyList<CoverageTileDTO> Coverage,
    int DocumentsProducingMostLines,
    decimal? LineShareOfTopDocumentsPercent,
    string Concentration);
