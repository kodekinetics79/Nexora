using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the BUYER receives: the quote PDF and the covering e-mail (pilot audit 2026-09-28, lane
/// quote-doc). Each test reads the rendered PDF's text or the composed e-mail, because the defects
/// were all things a procurement evaluator reads on paper:
/// <list type="bullet">
/// <item>CB-01 / UX-02 / D-12: stock terms that contradicted the quote ("valid for 30 days" beside a
/// different Valid Until, "taxes not included" beside a VAT line, "Net 30").</item>
/// <item>CB-03: the rep's own "Remarks / Terms" never printed.</item>
/// <item>UX-03 / CB-08: the buyer's material number, maker and part number vanished.</item>
/// <item>CB-05 / CB-14 / CB-17: e-mail subject without the buyer's RFQ number, "Your RFQ Reference"
/// falling back to our own number, a VAT-inclusive "Total" with no named contact.</item>
/// <item>CB-06 / CB-12: no signature or stamp block; revisions that did not say what they replace.</item>
/// <item>CB-09 / CB-10 / CB-11: grand total above its own arithmetic, unit price x qty not equal to
/// the printed line total, 2.5 printed as 3.</item>
/// <item>HT-10: SHIP TO "Address not on file" while the RFQ named the delivery point.</item>
/// </list>
/// </summary>
public sealed class QuoteBuyerDocumentTests
{
    private const long Tenant = 97_301;
    private const long CurrencyId = 97_302;
    private const long OwnerId = 97_303;
    private const long RoleId = 97_304;
    private const long CustomerId = 97_305;
    private const long SentStatusId = 97_306;
    private const long DraftStatusId = 97_307;

    // ------------------------------------------------------------------ terms (CB-01, P0 #2)

    [Fact]
    public async Task The_terms_are_generated_from_the_quote_and_never_contradict_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, QuoteDocumentText.LegacySeededTerms);

        Assert.Contains("Commercial terms", text);
        Assert.Contains("Prices are valid until Dec 27, 2026.", text);
        Assert.Contains("Valid Until: Dec 27, 2026", text);
        Assert.Contains("Prices are in SAR and exclude VAT. VAT 15% is shown separately.", text);
        Assert.Contains("Delivery to: SEC Materials East Plant-East Operating Area.", text);
        Assert.Contains("Delivery times are counted from the date we receive your purchase order.", text);
        // The legacy seed counts as no terms at all: every contradicting clause is gone.
        Assert.DoesNotContain("valid for 30 days", text);
        Assert.DoesNotContain("not included unless specified", text);
        Assert.DoesNotContain("Net 30", text);
        Assert.DoesNotContain("property of the seller", text);
        Assert.DoesNotContain("Additional terms", text);
        Assert.DoesNotContain("Terms & Conditions", text);
    }

    [Fact]
    public async Task The_tenants_own_clauses_print_after_the_generated_ones_as_additional_terms()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, "Warranty 12 months from acceptance at site.");

        Assert.Contains("Additional terms", text);
        Assert.Contains("Warranty 12 months from acceptance at site.", text);
        Assert.True(text.IndexOf("Commercial terms", StringComparison.Ordinal)
            < text.IndexOf("Additional terms", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(QuoteDocumentText.LegacySeededTerms)]
    [InlineData("1. Prices are valid for 30 days from the date of the quote.\r\n2. Payment terms: Net 30 days from invoice date.\r\n3. Delivery dates are estimates and subject to confirmation.\r\n4. All products remain the property of the seller until fully paid.\r\n5. Any applicable taxes or duties are not included unless specified.\r\n6. Warranty and liability are as per the manufacturer's standard terms.\r\n7. This quote is confidential and intended solely for the recipient.  ")]
    public void The_legacy_seed_in_any_line_ending_counts_as_no_additional_terms(string? stored) =>
        Assert.Null(QuoteDocumentText.AdditionalTerms(stored));

    [Fact]
    public void An_edited_version_of_the_seed_is_the_tenants_choice_and_prints()
    {
        var edited = QuoteDocumentText.LegacySeededTerms.Replace("Net 30", "Net 60");
        Assert.Equal(edited, QuoteDocumentText.AdditionalTerms(edited));
    }

    // ------------------------------------------------------------------ rep's notes (P0 #3)

    [Fact]
    public async Task The_reps_notes_print_on_the_pdf_and_in_the_email()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape { HeaderRemarks = "Delivery within 4 weeks, DAP Dammam." });
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, null);
        var email = await Service(context, null).GetEmailDraftAsync(quoteId, Tenant);

        Assert.Contains("Notes", text);
        Assert.Contains("Delivery within 4 weeks, DAP Dammam.", text);
        Assert.Contains("Notes:\nDelivery within 4 weeks, DAP Dammam.", email.Body);
    }

    [Fact]
    public async Task The_internal_draft_marker_never_reaches_the_buyer()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape { HeaderRemarks = QuoteDocumentText.DraftReviewPlaceholder });
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, null);
        var email = await Service(context, null).GetEmailDraftAsync(quoteId, Tenant);

        Assert.DoesNotContain("Commercial Review Required", text);
        Assert.DoesNotContain("Commercial Review Required", email.Body);
        Assert.DoesNotContain("Notes", email.Body);
    }

    // ------------------------------------------------------------------ lines (UX-03, CB-10, CB-11)

    [Fact]
    public async Task Each_line_prints_the_buyers_material_maker_and_part_number()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, null);

        Assert.Contains("Material: 905750742 · Make: SAFT · Part no.: LS14500-AX", text);
        // A line with only a maker prints only that.
        Assert.Contains("Make: ABB", text);
    }

    [Fact]
    public async Task Quantities_keep_their_decimals_and_qty_times_unit_price_is_the_printed_line_total()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, null);

        // 12 x 2,338.54 = 28,062.48 on paper; 2.5 M x 100.00 = 250.00, and 2.5 is not printed as 3.
        Assert.Contains("2,338.54", text);
        Assert.Contains("28,062.48", text);
        Assert.Contains("2.5", text);
        Assert.Contains("250.00", text);
    }

    [Theory]
    [InlineData(2338.541625, 2338.54)]
    [InlineData(1.005, 1.01)]
    [InlineData(10, 10)]
    [InlineData(0.004, 0.004)] // a positive price is never rounded to zero
    public void Unit_prices_are_held_at_the_scale_they_print_at(decimal stored, decimal expected) =>
        Assert.Equal(expected, QuoteService.RoundUnitPrice(stored));

    [Fact]
    public async Task A_quote_saved_with_a_six_decimal_price_stores_the_printed_price_and_its_total()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedTenant(context);
        await context.SaveChangesAsync();

        var created = await Service(context, null).CreateQuoteAsync(new ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteCreateRequestDTO
        {
            BusinessUnitId = Tenant, CustomerId = CustomerId, CurrencyId = CurrencyId, CreatedBy = "rep",
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(60),
            QuoteItems =
            {
                new ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteItemCreateRequestDTO
                {
                    ItemDescription = "Contactor", Quantity = 12, UnitPrice = 2338.541625m, TotalAmount = 0
                }
            }
        });

        var line = Assert.Single(created.QuoteItems);
        Assert.Equal(2338.54m, line.UnitPrice);
        Assert.Equal(28062.48m, line.TaxableBase);
    }

    // ------------------------------------------------------------------ totals (CB-09)

    [Fact]
    public async Task The_arithmetic_prints_above_the_grand_total_and_stays_on_its_page()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedTenant(context);
        await context.SaveChangesAsync();
        var service = Service(context, null);

        // Walk the line count across the page boundary: whichever count pushes the totals block to
        // the foot of a page, every row of it must land on the same page as GRAND TOTAL.
        for (var lines = 6; lines <= 22; lines++)
        {
            var quoteId = SeedQuote(context, new QuoteShape { Id = 97_500 + lines * 50, ExtraLines = lines });
            await Attest(context, quoteId);
            using var pdf = PdfDocument.Open(await service.GenerateQuotePdfAsync(quoteId, Tenant));
            var pages = pdf.GetPages().Select(page => page.Text).ToList();
            var totalPage = pages.FindIndex(page => page.Contains("GRAND TOTAL", StringComparison.Ordinal));
            Assert.True(totalPage >= 0);
            var page = pages[totalPage];
            Assert.Contains("Subtotal", page);
            Assert.Contains("VAT 15%", page);
            Assert.True(page.IndexOf("Subtotal", StringComparison.Ordinal) < page.IndexOf("Total excluding VAT", StringComparison.Ordinal));
            Assert.True(page.IndexOf("Total excluding VAT", StringComparison.Ordinal) < page.IndexOf("GRAND TOTAL", StringComparison.Ordinal),
                $"{lines} lines: the grand total printed above its own arithmetic.");
            // CB-06: every page is a price page, so every page carries the signature and stamp block.
            Assert.All(pages, text => Assert.Contains("Company stamp", text));
        }
    }

    // ------------------------------------------------------------------ identity on the page

    [Fact]
    public async Task Your_RFQ_reference_is_the_buyers_number_and_never_ours()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var promoted = SeedQuote(context, new QuoteShape { Id = 97_600, CustomerRfqReference = "C001832155", LeadRfqNo = "LEAD-NO" });
        var leadOnly = SeedQuote(context, new QuoteShape { Id = 97_650, CustomerRfqReference = null, LeadRfqNo = "C001701152" });
        var unknown = SeedQuote(context, new QuoteShape { Id = 97_700, CustomerRfqReference = null, LeadRfqNo = null });
        foreach (var id in new[] { promoted, leadOnly, unknown }) await Attest(context, id);

        var promotedText = await PdfTextAsync(context, promoted, null);
        var leadText = await PdfTextAsync(context, leadOnly, null);
        var unknownText = await PdfTextAsync(context, unknown, null);

        Assert.Contains("Your RFQ Reference: C001832155", promotedText);
        Assert.Contains("Your RFQ Reference: C001701152", leadText);
        Assert.Contains("Your RFQ Reference: —", unknownText);
        // Rfq.Rfqno is Nexora's serial; it is never presented as the buyer's reference.
        Assert.DoesNotContain("NXR-RFQ", promotedText + leadText + unknownText);
    }

    [Fact]
    public async Task Ship_to_is_the_delivery_point_on_the_enquiry()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, null);

        var shipTo = text.IndexOf("SHIP TO", StringComparison.Ordinal);
        Assert.True(shipTo >= 0);
        Assert.Contains("SEC Materials East Plant-East Operating Area", text[shipTo..]);
        // BILL TO still states the gap honestly; SHIP TO no longer repeats it.
        Assert.Equal(1, CountOf(text, "Address not on file"));
    }

    [Fact]
    public async Task The_price_pages_carry_a_signature_and_stamp_block_naming_the_signer()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());
        await Attest(context, quoteId);

        var text = await PdfTextAsync(context, quoteId, null);

        Assert.Contains("For and on behalf of Noor and Sons Trading Co.", text);
        Assert.Contains("Salim Haddad, Sales Representative", text);
        Assert.Contains("Authorised signature", text);
        Assert.Contains("Company stamp", text);
        Assert.Contains("Prepared by Salim Haddad", text);
        Assert.DoesNotContain("Generated by", text);
    }

    [Fact]
    public async Task A_revision_says_which_quotation_it_supersedes()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var original = SeedQuote(context, new QuoteShape { Id = 97_800, QuoteNo = "QT-0926-0004", SentOn = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc) });
        var revision = SeedQuote(context, new QuoteShape { Id = 97_850, QuoteNo = "QT-0926-0004-R2", RevisionOf = original });
        await Attest(context, revision);

        var text = await PdfTextAsync(context, revision, null);
        var email = await Service(context, null).GetEmailDraftAsync(revision, Tenant);

        Assert.Contains("Supersedes: QT-0926-0004 dated Sep 17, 2026", text);
        Assert.Contains("This quotation supersedes QT-0926-0004 dated Sep 17, 2026, which is withdrawn.", text);
        Assert.Contains("This quotation supersedes QT-0926-0004 dated Sep 17, 2026, which is withdrawn.", email.Body);
    }

    // ------------------------------------------------------------------ e-mail (CB-05, CB-17)

    [Fact]
    public async Task The_email_subject_leads_with_the_buyers_RFQ_number_and_the_body_states_totals_ex_VAT_and_a_named_contact()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape());

        var email = await Service(context, null).GetEmailDraftAsync(quoteId, Tenant);

        Assert.Equal("RFQ C001832155 – Quotation QT-DOC-97400 – Noor Sons", email.Subject);
        Assert.Contains("Your RFQ reference: C001832155", email.Body);
        Assert.Contains("Total excl. VAT: SAR 28,437.48", email.Body);
        Assert.Contains("VAT 15%: SAR 4,265.62", email.Body);
        Assert.Contains("Total incl. VAT: SAR 32,703.10", email.Body);
        Assert.Contains("Salim Haddad, Sales Representative", email.Body);
        Assert.Contains("salim@noorsons.example", email.Body);
        Assert.Contains("+966 13 800 0000", email.Body);
        Assert.DoesNotContain("Total: ", email.Body);
    }

    [Fact]
    public async Task Without_a_buyer_reference_the_subject_keeps_its_old_form_and_never_uses_our_serial()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, new QuoteShape { CustomerRfqReference = null, LeadRfqNo = null });

        var email = await Service(context, null).GetEmailDraftAsync(quoteId, Tenant);

        Assert.Equal("Quote #QT-DOC-97400 from Noor Sons", email.Subject);
        Assert.DoesNotContain("NXR-RFQ", email.Body);
        Assert.DoesNotContain("Your RFQ reference", email.Body);
    }

    // ------------------------------------------------------------------ representative sample

    /// <summary>
    /// Writes the text of one representative quote PDF when QUOTE_DOC_SAMPLE_PATH is set, so a
    /// reviewer can read what the buyer gets without opening a PDF. Always renders and checks it.
    /// </summary>
    [Fact]
    public async Task Representative_quote_renders_and_can_be_written_out_for_review()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var original = SeedQuote(context, new QuoteShape { Id = 97_900, QuoteNo = "QT-0926-0004", SentOn = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc) });
        var quoteId = SeedQuote(context, new QuoteShape
        {
            Id = 97_950, QuoteNo = "QT-0926-0004-R2", RevisionOf = original,
            HeaderRemarks = "Offer based on your revision 3. Delivery DAP your warehouse."
        });
        await Attest(context, quoteId);

        var pdf = await Service(context, "Warranty: 12 months from acceptance at site.").GenerateQuotePdfAsync(quoteId, Tenant);
        using var document = PdfDocument.Open(pdf);
        var text = string.Join("\n\n---- page break ----\n\n",
            document.GetPages().Select(page => string.Join("\n", page.GetWords().GroupBy(word => Math.Round(word.BoundingBox.Bottom))
                .OrderByDescending(line => line.Key)
                .Select(line => string.Join(" ", line.OrderBy(word => word.BoundingBox.Left).Select(word => word.Text))))));
        var email = await Service(context, null).GetEmailDraftAsync(quoteId, Tenant);

        Assert.Contains("GRAND TOTAL", text);
        var path = Environment.GetEnvironmentVariable("QUOTE_DOC_SAMPLE_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllBytesAsync(Path.ChangeExtension(path, ".pdf"), pdf);
            await File.WriteAllTextAsync(path,
                $"=== PDF text (QT-0926-0004-R2, {document.NumberOfPages} page(s)) ===\n\n{text}\n\n"
                + $"=== E-mail ===\nSubject: {email.Subject}\n\n{email.Body}\n");
        }
    }

    // ================================================================== fixture

    private sealed class QuoteShape
    {
        public long Id { get; init; } = 97_400;
        public string? QuoteNo { get; init; }
        public string? CustomerRfqReference { get; init; } = "C001832155";
        public string? LeadRfqNo { get; init; } = "C001832155";
        public string? HeaderRemarks { get; init; }
        public int ExtraLines { get; init; }
        public DateTime? SentOn { get; init; }
        public long? RevisionOf { get; init; }
    }

    private static void SeedTenant(ErpRfqAutomationContext context)
    {
        if (context.BusinessUnits.Local.Any(x => x.Id == Tenant) || context.BusinessUnits.Any(x => x.Id == Tenant)) return;
        var unit = Seed.EnsureBusinessUnit(context, Tenant);
        unit.BusinessUnitName = "Noor Sons";
        unit.LegalName = "Noor and Sons Trading Co.";
        unit.CommercialRegistrationNumber = "2050012345";
        unit.TaxRegistrationNumber = "310123456700003";
        context.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = RoleId, BusinessUnitId = Tenant, SetupType = "Role", SetupCode = "SALES_REP",
                SetupValue = "Sales Representative", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            },
            new SetupMaster
            {
                SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = "SENT",
                SetupValue = "Sent", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            },
            new SetupMaster
            {
                SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = "DRAFT",
                SetupValue = "Draft", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            });
        context.Users.Add(new User
        {
            Id = OwnerId, FirstName = "Salim", LastName = "Haddad", Email = "salim@noorsons.example",
            PasswordHash = "x", ImageUrl = "n/a", Buid = Tenant, RoleId = RoleId, IsActive = true,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        Seed.Customer(context, CustomerId, Tenant, "Saudi Electricity Company");
        context.QuoteConfigurations.Add(new QuoteConfiguration
        {
            BusinessUnitId = Tenant, CompanyAddress = "King Fahd Road, Al Khobar 34423",
            CompanyPhone = "+966 13 800 0000", CompanyEmail = "sales@noorsons.example"
        });
    }

    /// <summary>
    /// A priced, taxed quote the renderer accepts: an SEC battery at 12 x 2,338.54, a cable at
    /// 2.5 M x 100.00, and a maker-only line, plus <see cref="QuoteShape.ExtraLines"/> filler lines.
    /// </summary>
    private static long SeedQuote(ErpRfqAutomationContext context, QuoteShape shape)
    {
        SeedTenant(context);
        var baseId = shape.Id;
        var lead = Seed.Lead(context, baseId + 1, Tenant);
        lead.Rfqno = shape.LeadRfqNo;
        context.Rfqs.Add(new Rfq
        {
            Id = baseId + 2, Rfqno = $"NXR-RFQ-1-2026-{baseId:D8}", LeadId = lead.Id, BusinessUnitId = Tenant,
            CustomerRfqReference = shape.CustomerRfqReference,
            DeliveryLocation = "SEC Materials East Plant-East Operating Area",
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });

        var quote = new Quote
        {
            Id = baseId + 3,
            QuoteNo = shape.QuoteNo ?? $"QT-DOC-{baseId}",
            Rfqid = baseId + 2,
            CustomerId = CustomerId,
            CurrencyId = CurrencyId,
            BusinessUnitId = Tenant,
            OwnerUserId = OwnerId,
            HeaderRemarks = shape.HeaderRemarks,
            QuoteDate = new DateTime(2026, 9, 27),
            ValidUntil = new DateTime(2026, 12, 27),
            SentOn = shape.SentOn,
            StatusId = shape.SentOn is null ? null : SentStatusId,
            RevisionOfQuoteId = shape.RevisionOf,
            RevisionNo = shape.RevisionOf is null ? 1 : 2,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        var lineId = baseId + 10;
        void Line(string? reference, string description, decimal quantity, string uom, decimal unitPrice,
            string? material, string? maker, string? part, int? leadTime)
        {
            var net = Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
            var tax = Math.Round(net * 0.15m, 2, MidpointRounding.AwayFromZero);
            quote.QuoteItems.Add(new QuoteItem
            {
                Id = lineId++, CustomerLineRef = reference, ItemDescription = description, Quantity = quantity,
                UnitOfMeasure = uom, UnitPrice = unitPrice, TaxAmount = tax, TaxRatePercentApplied = 15m,
                TaxCategory = QuoteLineTaxCategories.Standard, TotalAmount = net + tax, DeliveryLeadTime = leadTime,
                CustomerMaterialCode = material, ManufacturerName = maker, ManufacturerPartNumber = part,
                CreatedBy = "seed", CreatedDate = DateTime.UtcNow
            });
        }
        Line("10", "Battery, lithium thionyl chloride 3.6 V", 12m, "EA", 2338.54m, "905750742", "SAFT", "LS14500-AX", 28);
        Line("20", "Cable, instrumentation 2C x 1.5 mm2", 2.5m, "M", 100m, null, null, null, 14);
        Line("30", "Temperature sensor PT100", 1m, "EA", 125m, null, "ABB", null, null);
        for (var index = 0; index < shape.ExtraLines; index++)
            Line($"{40 + index * 10}", $"Filler line {index + 1}", 1m, "EA", 10m, $"MAT-{index}", null, null, 7);
        quote.TotalAmount = quote.QuoteItems.Sum(item => item.TotalAmount);
        context.Quotes.Add(quote);
        context.SaveChanges();
        return quote.Id;
    }

    private static Task Attest(ErpRfqAutomationContext context, long quoteId) =>
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quoteId, Tenant, ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-DOC", null, "tests", default);

    private static QuoteService Service(ErpRfqAutomationContext context, string? terms) =>
        new(context, null!, new StubConfig(new QuoteConfiguration
        {
            BusinessUnitId = Tenant,
            CompanyAddress = "King Fahd Road, Al Khobar 34423",
            CompanyPhone = "+966 13 800 0000",
            CompanyEmail = "sales@noorsons.example",
            TermsAndConditions = terms
        }));

    private static async Task<string> PdfTextAsync(ErpRfqAutomationContext context, long quoteId, string? terms)
    {
        using var document = PdfDocument.Open(await Service(context, terms).GenerateQuotePdfAsync(quoteId, Tenant));
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private sealed class StubConfig(QuoteConfiguration configuration) : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId) => Task.FromResult<QuoteConfiguration?>(configuration);
        public Task AddAsync(QuoteConfiguration c) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration c) => Task.CompletedTask;
    }
}
