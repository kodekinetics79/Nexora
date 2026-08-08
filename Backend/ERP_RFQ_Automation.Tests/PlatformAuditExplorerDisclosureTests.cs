using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Mvc;
using static ERP_RFQ_Automation.Tests.PlatformSupportFixture;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Finding R6, as a property of the module rather than of one column.
///
/// <para>The rule: an audit entry's PAYLOAD is disclosed only to a caller who could have performed
/// the write. The risk in fixing it this way is not that the rule is wrong, it is that it gets
/// applied at three of the four surfaces that republish a payload — so the surfaces are enumerated
/// here and each is driven with a tier that must be refused and a tier that must be served.</para>
///
/// <para><c>RedTeamAuditDisclosureTests</c> holds the end-to-end version, driving the real
/// Owner|BillingAdmin billing endpoint. These tests drive the disclosure boundary directly, so a
/// regression names the surface that regressed.</para>
/// </summary>
public sealed class PlatformAuditExplorerDisclosureTests
{
    private const string BillingSecret = "Free until the Q4 renewal is signed.";
    private const string BillingVerb = "billing.tenant.commercial-terms";

    /// <summary>
    /// The exact <c>{before, after}</c> shape <c>PlatformBillingController.SetTenantCommercialTerms</c>
    /// records, with the restricted commentary in the "after" half.
    /// </summary>
    private const string BillingMetadata =
        "{\"before\":{\"BillingMode\":\"Billable\",\"BillingModeReason\":null}," +
        "\"after\":{\"BillingMode\":\"Partner\",\"BillingModeReason\":\"" + BillingSecret + "\"}}";

    // ---- surface 1: the entry detail (the route the auditor took) ----------

    [Theory]
    [InlineData(PlatformRole.Owner, true)]
    [InlineData(PlatformRole.BillingAdmin, true)]
    [InlineData(PlatformRole.SupportAdmin, false)]
    [InlineData(PlatformRole.ReadOnlyOps, false)]
    public async Task Entry_detail_discloses_a_billing_payload_only_to_the_billing_tier(
        PlatformRole role, bool expected)
    {
        // SupportAdmin is refused alongside ReadOnlyOps deliberately. The platform policies are not
        // a ladder: Billing is Owner|BillingAdmin and TenantAdmin is Owner|SupportAdmin, and neither
        // contains the other. That incomparability IS the Sec9 separation of duties, and a fix that
        // let support read commercial commentary would have quietly dissolved it.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, $"disclosure-entry-{role}".ToLowerInvariant());
        var row = await SeedAuditAsync(db, BillingVerb, tenantId, OwnerActorId, BillingMetadata);

        await using var context = db.ContextFor(null);
        var detail = Detail(await Explorer(context, Actor(role: role)).Entry(row.Id, CancellationToken.None));

        Assert.Equal(expected, detail.MetadataDisclosed);
        Assert.Equal(PlatformPolicies.Billing, detail.MetadataPolicy);
        if (expected)
        {
            Assert.Contains(BillingSecret, detail.Metadata!.Value.GetRawText(), StringComparison.Ordinal);
            Assert.Contains(detail.Changes, c => c.After == BillingSecret);
        }
        else
        {
            Assert.Null(detail.Metadata);
            Assert.Null(detail.Before);
            Assert.Null(detail.After);
            Assert.DoesNotContain(detail.Changes, c => c.Before == BillingSecret || c.After == BillingSecret);
        }
    }

    [Fact]
    public async Task A_withheld_payload_still_names_which_fields_changed()
    {
        // The line the fix draws is at VALUES, not at the existence of the change. "The commercial
        // terms moved, and these are the terms that moved" is the operator journey the coordinator
        // asked not to break; field names are entity property names, i.e. schema, not content.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "disclosure-fields");
        var row = await SeedAuditAsync(db, BillingVerb, tenantId, OwnerActorId, BillingMetadata);

        await using var context = db.ContextFor(null);
        var detail = Detail(await Explorer(context, Actor(role: PlatformRole.ReadOnlyOps))
            .Entry(row.Id, CancellationToken.None));

        Assert.False(detail.MetadataDisclosed);
        Assert.Equal(["BillingMode", "BillingModeReason"],
            detail.Changes.Select(c => c.Field).OrderBy(f => f, StringComparer.Ordinal));
        Assert.All(detail.Changes, c =>
        {
            Assert.Null(c.Before);
            Assert.Null(c.After);
        });

        // Identity, attribution and outcome are untouched: this tier can still answer "what happened
        // to this customer, when, and who did it".
        Assert.Equal(BillingVerb, detail.Action);
        Assert.Equal(tenantId, detail.TenantId);
        Assert.Equal(OwnerActorId, detail.ActorPlatformUserId);
        Assert.Equal(PlatformAuditResults.Success, detail.Result);
    }

    // ---- surface 2: the paged query ----------------------------------------

    [Fact]
    public async Task The_query_list_withholds_and_discloses_row_by_row_within_one_page()
    {
        // One page routinely mixes verbs from several writers, so the decision cannot be per-request.
        // A lifecycle payload and a billing payload sit side by side here and must resolve
        // differently for the same caller.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "disclosure-list");
        await SeedAuditAsync(db, BillingVerb, tenantId, OwnerActorId, BillingMetadata);
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId,
            """{"from":"Active","to":"Suspended","reason":"Non-payment"}""");
        await SeedAuditAsync(db, "platform.login", null, OwnerActorId, """{"email":"owner@example.test"}""");

        await using var context = db.ContextFor(null);
        var support = Page(await Explorer(context, Actor(role: PlatformRole.SupportAdmin)).Query(
            null, null, null, null, null, null, null, null, null, null, 1, 50, CancellationToken.None));

        var billing = Assert.Single(support.Items, e => e.Action == BillingVerb);
        Assert.False(billing.MetadataDisclosed);
        Assert.Null(billing.Metadata);

        // A support tier reads support-and-lifecycle payloads in full — the fix must not have made
        // the explorer useless to the tier that answers customer questions.
        var lifecycle = Assert.Single(support.Items, e => e.Action == "tenant.suspend");
        Assert.True(lifecycle.MetadataDisclosed);
        Assert.Contains("Non-payment", lifecycle.Metadata!.Value.GetRawText(), StringComparison.Ordinal);

        // Operator sign-in telemetry is the one deliberate PlatformScope entry: its writer is the
        // [AllowAnonymous] login endpoint, so there is no writer's authority to inherit.
        var login = Assert.Single(support.Items, e => e.Action == "platform.login");
        Assert.True(login.MetadataDisclosed);
        Assert.Equal(PlatformPolicies.PlatformScope, login.MetadataPolicy);
    }

    [Fact]
    public async Task Withholding_never_removes_a_row_from_the_result_or_the_count()
    {
        // Filtering rows out instead of emptying them would make the explorer lie about how much
        // happened to a customer, and would let a caller infer the restricted rows by differencing
        // two tiers' totals.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "disclosure-counts");
        await SeedAuditAsync(db, BillingVerb, tenantId, OwnerActorId, BillingMetadata);
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId, """{"from":"Active","to":"Suspended"}""");

        await using var context = db.ContextFor(null);
        var asOwner = Page(await Explorer(context, Actor(role: PlatformRole.Owner)).Query(
            tenantId, null, null, null, null, null, null, null, null, null, 1, 50, CancellationToken.None));
        var asReadOnly = Page(await Explorer(context, Actor(role: PlatformRole.ReadOnlyOps)).Query(
            tenantId, null, null, null, null, null, null, null, null, null, 1, 50, CancellationToken.None));

        Assert.Equal(asOwner.TotalCount, asReadOnly.TotalCount);
        Assert.Equal(asOwner.Items.Select(e => e.Id), asReadOnly.Items.Select(e => e.Id));
    }

    // ---- surface 3: the tenant timeline ------------------------------------

    [Fact]
    public async Task The_tenant_timeline_withholds_the_same_payloads_the_explorer_does()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "disclosure-timeline");
        await SeedAuditAsync(db, BillingVerb, tenantId, OwnerActorId, BillingMetadata);

        await using var context = db.ContextFor(null);
        var result = await Explorer(context, Actor(role: PlatformRole.ReadOnlyOps))
            .TenantTimeline(tenantId, null, null, 100, CancellationToken.None);
        var entries = Assert.IsType<List<TenantTimelineEntryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        var billing = Assert.Single(entries, e => e.Action == BillingVerb);
        Assert.False(billing.MetadataDisclosed);
        Assert.Null(billing.Metadata);
        Assert.Equal(PlatformPolicies.Billing, billing.MetadataPolicy);

        // The entry itself remains on the timeline: a customer's page must not silently omit that
        // their commercial terms were changed, and a caller must not be able to infer the restricted
        // rows by differencing two tiers' timelines.
        var asOwner = Assert.IsType<List<TenantTimelineEntryDto>>(Assert.IsType<OkObjectResult>(
            (await Explorer(context, Actor(role: PlatformRole.Owner))
                .TenantTimeline(tenantId, null, null, 100, CancellationToken.None)).Result).Value);
        Assert.Equal(asOwner.Select(e => e.Id), entries.Select(e => e.Id));
        Assert.True(Assert.Single(asOwner, e => e.Action == BillingVerb).MetadataDisclosed);
    }

    // ---- surface 4: the tenant operations summary --------------------------

    [Fact]
    public async Task The_operations_summary_withholds_payloads_but_keeps_its_counts_honest()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "disclosure-summary");
        await SeedAuditAsync(db, BillingVerb, tenantId, OwnerActorId, BillingMetadata);
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId,
            """{"from":"Active","to":"Suspended","reason":"Non-payment"}""");

        await using var context = db.ContextFor(null);
        var summary = Assert.IsType<TenantOperationsSummaryDto>(Assert.IsType<OkObjectResult>(
            (await Operations(context, Actor(role: PlatformRole.ReadOnlyOps))
                .OperationsSummary(tenantId, CancellationToken.None)).Result).Value);

        // Both rows are counted and both are listed; only one of them carries its payload.
        Assert.Equal(2, summary.Audit.EntryCountLast30Days);
        Assert.Equal(2, summary.Audit.RecentEntries.Count);
        Assert.False(Assert.Single(summary.Audit.RecentEntries, e => e.Action == BillingVerb).MetadataDisclosed);
        Assert.DoesNotContain(BillingSecret,
            string.Join('\n', summary.Audit.RecentEntries.Select(e => e.Metadata?.GetRawText() ?? string.Empty)));
    }

    // ---- surface 5: the per-ticket timeline --------------------------------

    [Fact]
    public async Task A_support_operator_reads_the_ticket_timeline_and_authorized_audit_payloads()
    {
        // The endpoint itself is TenantAdmin-gated because notes are customer content. Once that
        // gate succeeds, the same policy authorizes the support transition payloads.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "disclosure-ticket");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using (var working = db.ContextFor(null))
        {
            await Tickets(working).AddNote(ticketId,
                new AddSupportTicketNoteRequest { Body = "Certificate expired." }, CancellationToken.None);
            await Tickets(working).Transition(ticketId, new TransitionSupportTicketRequest
            {
                Status = nameof(SupportTicketStatus.Resolved),
                Reason = "Rotated the certificate",
                Resolution = "Reissued"
            }, CancellationToken.None);
        }
        await RaiseTicketAsync(db, tenantId); // remains open so redacted telemetry counts stay provable

        await using var context = db.ContextFor(null);
        var asSupport = Assert.IsType<SupportTicketTimelineDto>(Assert.IsType<OkObjectResult>(
            (await Tickets(context, Actor(role: PlatformRole.SupportAdmin))
                .Timeline(ticketId, CancellationToken.None)).Result).Value);
        var note = Assert.Single(asSupport.Entries, e => e.Kind == "note");
        Assert.Equal("Certificate expired.", note.Body);
        var disclosed = Assert.Single(asSupport.Entries,
            e => e.Action == PlatformSupportTicketsController.Actions.Transition);
        Assert.True(disclosed.MetadataDisclosed);
        Assert.Contains("Rotated the certificate", disclosed.Metadata!.Value.GetRawText(), StringComparison.Ordinal);

        var readOnlyTimeline = Assert.IsType<List<TenantTimelineEntryDto>>(Assert.IsType<OkObjectResult>(
            (await Explorer(context, Actor(role: PlatformRole.ReadOnlyOps))
                .TenantTimeline(tenantId, null, null, 100, CancellationToken.None)).Result).Value);
        Assert.DoesNotContain(readOnlyTimeline, entry => entry.Kind == "ticket");

        var readOnlySummary = Assert.IsType<TenantOperationsSummaryDto>(Assert.IsType<OkObjectResult>(
            (await Operations(context, Actor(role: PlatformRole.ReadOnlyOps))
                .OperationsSummary(tenantId, CancellationToken.None)).Result).Value);
        Assert.True(readOnlySummary.Support.OpenTicketCount > 0);
        Assert.Empty(readOnlySummary.Support.RecentTickets);
    }

    // ---- the table itself ---------------------------------------------------

    [Fact]
    public void Every_registered_verb_maps_to_a_real_platform_policy()
    {
        var known = new[]
        {
            PlatformPolicies.PlatformScope, PlatformPolicies.Owner,
            PlatformPolicies.TenantAdmin, PlatformPolicies.Billing, PlatformPolicies.Impersonate
        };

        Assert.NotEmpty(PlatformAuditDisclosure.KnownActions);
        foreach (var action in PlatformAuditDisclosure.KnownActions)
            Assert.Contains(PlatformAuditDisclosure.RequiredPolicyFor(action), known);
    }

    [Fact]
    public void Only_operator_sign_in_telemetry_is_registered_at_the_bare_platform_scope()
    {
        // The whole point of R6 is that PlatformScope is not a meaningful restriction on a payload,
        // so any future entry at that level is a decision that has to be argued for. Naming the two
        // exceptions here makes adding a third fail a test rather than pass a review.
        var open = PlatformAuditDisclosure.KnownActions
            .Where(a => PlatformAuditDisclosure.RequiredPolicyFor(a) == PlatformPolicies.PlatformScope)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["platform.login", "platform.login.failed"], open);
    }

    [Fact]
    public void Every_verb_this_module_writes_is_registered()
    {
        // Not a general drift guard — unknown verbs already fail closed. This pins the verbs THIS
        // module writes, because they are the ones whose over-restriction would break the desk's own
        // journey without anybody noticing until an operator complained.
        foreach (var action in new[]
                 {
                     PlatformSupportTicketsController.Actions.Create,
                     PlatformSupportTicketsController.Actions.Note,
                     PlatformSupportTicketsController.Actions.Transition,
                     PlatformSupportTicketsController.Actions.Assign,
                     PlatformSupportTicketsController.Actions.Severity,
                     PlatformSupportTicketsController.Actions.Link,
                     PlatformSupportTicketsController.Actions.Unlink
                 })
            Assert.Equal(PlatformPolicies.TenantAdmin, PlatformAuditDisclosure.RequiredPolicyFor(action));

        // Erasure is an Owner operation on the offboarding controller, so its trail is Owner-only.
        Assert.Equal(PlatformPolicies.Owner,
            PlatformAuditDisclosure.RequiredPolicyFor(SupportTicketRedactionService.AuditAction));
    }

    // ---- helpers -----------------------------------------------------------

    private static PlatformAuditEntryDetailDto Detail(ActionResult<PlatformAuditEntryDetailDto> result)
        => Assert.IsType<PlatformAuditEntryDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static PagedResultDto<PlatformAuditEntryDto> Page(
        ActionResult<PagedResultDto<PlatformAuditEntryDto>> result)
        => Assert.IsType<PagedResultDto<PlatformAuditEntryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
}
