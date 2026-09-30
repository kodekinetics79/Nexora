using ERP_RFQ_Automation.Extraction.Templates;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.QuoteTerms;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Buyer-terms slice 2 (HT-03, CB-02, CB-07): the buyer's validity floor and currency rule become
/// the draft's defaults, and a quote that breaks them — or goes out with estimates, "price to
/// follow" lines or no delivery time — is warned about before it is sent, never blocked.
/// </summary>
public sealed class BuyerQuoteTermsTests
{
    private static readonly DateTime Today = new(2026, 9, 28);

    [Fact]
    public void SEC_valid_for_90_days_from_bid_due_date_is_90_days_from_closing()
    {
        var rule = BuyerQuoteTermRules.ParseValidity("6.Quotation must be valid for 90 days from bid due date.");

        Assert.NotNull(rule);
        Assert.Equal(90, rule!.Days);
        Assert.Equal(BuyerValidityRule.FromClosing, rule.Basis);
        Assert.Contains("90 days from bid due date", rule.Sentence);
    }

    [Fact]
    public void The_draft_defaults_to_closing_plus_90_for_the_SEC_sentence()
    {
        var rule = BuyerQuoteTermRules.ParseValidity("Quotation must be valid for 90 days from bid due date");
        var closing = new DateTime(2026, 9, 30);

        // SEC C001832155 closed 30 Sep: the quote must hold until 29 Dec, not today + 30 (28 Oct).
        Assert.Equal(new DateTime(2026, 12, 29), BuyerQuoteTermRules.DefaultValidUntil(Today, closing, rule));
    }

    [Fact]
    public void Without_a_buyer_floor_the_draft_is_valid_for_the_ordinary_30_days()
        => Assert.Equal(Today.AddDays(30), BuyerQuoteTermRules.DefaultValidUntil(Today, new DateTime(2026, 10, 8), null));

    [Fact]
    public void A_short_buyer_floor_never_shortens_the_ordinary_30_days()
    {
        var rule = BuyerQuoteTermRules.ParseValidity("Offer validity: 10 days from the date of submission");
        Assert.Equal(Today.AddDays(30), BuyerQuoteTermRules.DefaultValidUntil(Today, null, rule));
    }

    [Theory]
    [InlineData("Bidders shall keep their offer with a validity of at least sixty (60) days from the bid closing date.", 60, "CLOSING")]
    [InlineData("The quotation shall be valid for at least 60 days from the date of submission.", 60, "SUBMISSION")]
    [InlineData("4.6 Quotation Validity (minimum 90 days)", 90, "UNSTATED")]
    [InlineData("Offer validity: 120 days", 120, "UNSTATED")]
    public void Validity_is_read_from_the_buyers_own_wording(string sentence, int days, string basis)
    {
        var rule = BuyerQuoteTermRules.ParseValidity(sentence);
        Assert.NotNull(rule);
        Assert.Equal(days, rule!.Days);
        Assert.Equal(basis, rule.Basis);
    }

    [Fact]
    public void Two_statements_keep_the_longer_floor_and_the_one_that_says_what_it_counts_from()
    {
        var rule = BuyerQuoteTermRules.ParseValidity(
            "4.6 Quotation Validity (minimum 90 days)\n6.Quotation must be valid for 90 days from bid due date");
        Assert.Equal(90, rule!.Days);
        Assert.Equal(BuyerValidityRule.FromClosing, rule.Basis);
    }

    [Fact]
    public void A_document_with_no_validity_sentence_yields_no_rule()
    {
        Assert.Null(BuyerQuoteTermRules.ParseValidity("Delivery within 90 days of the purchase order."));
        Assert.Null(BuyerQuoteTermRules.ParseValidity(null));
    }

    [Fact]
    public void The_word_readers_validity_term_is_understood()
    {
        var rule = BuyerQuoteTermRules.ParseValidityTerm("At least 60 days after closing", null);
        Assert.Equal(60, rule!.Days);
        Assert.Equal(BuyerValidityRule.FromClosing, rule.Basis);
    }

    [Fact]
    public void Aramco_USD_or_SAR_with_a_SAR_only_tenant_defaults_the_quote_to_SAR()
    {
        var allowed = BuyerQuoteTermRules.ParseAllowedCurrencies("USD or SAR");
        Assert.Equal(new[] { "USD", "SAR" }, allowed);
        Assert.Equal("SAR", BuyerQuoteTermRules.DefaultCurrency(allowed, new[] { "SAR" }, "SAR"));
        // Both held: the tenant's base currency wins when the buyer allows it.
        Assert.Equal("SAR", BuyerQuoteTermRules.DefaultCurrency(allowed, new[] { "USD", "SAR" }, "SAR"));
        // Nothing the tenant quotes in: no default, the rep chooses.
        Assert.Null(BuyerQuoteTermRules.DefaultCurrency(new[] { "EUR" }, new[] { "SAR" }, "SAR"));
        Assert.Null(BuyerQuoteTermRules.DefaultCurrency(Array.Empty<string>(), new[] { "SAR" }, "SAR"));
    }

    [Fact]
    public void A_currency_rule_in_running_text_is_read()
    {
        var rule = BuyerQuoteTermRules.ParseCurrencyRule("Local vendors MUST bid in SAR only. Foreign vendors may quote in USD.");
        Assert.NotNull(rule);
        Assert.Equal(new[] { "SAR" }, rule!.Value.Codes);
    }

    /// <summary>Every SEC RFP arrives as an SAP Ariba print saved as ".doc" that is really HTML.</summary>
    [Fact]
    public void An_Ariba_print_saved_as_doc_is_read_as_HTML()
    {
        const string html = "<!-- class: ariba.sourcing.rfxui.PrintRFXEncode -->\n<html><body><table>"
            + "<tr><td>2</td><td>Local vendors MUST bid in SAR only</td></tr>"
            + "<tr><td>4.6</td><td>Quotation Validity (minimum 90 days)</td></tr>"
            + "<tr><td>5 Header Text</td><td>Important points.5.Any change in the price after bid close will be rejected."
            + "6.Quotation must be valid for 90 days from bid due date.7.For local vendor, the delivery of material&nbsp;must be delivered.</td></tr>"
            + "</table></body></html>";

        var terms = BuyerQuoteTermsService.Read("doc", System.Text.Encoding.UTF8.GetBytes(html), "SE  RFP C001817585.doc");

        Assert.Equal(90, terms.Validity!.Days);
        Assert.Equal(BuyerValidityRule.FromClosing, terms.Validity.Basis);
        Assert.Equal("Quotation must be valid for 90 days from bid due date.", terms.Validity.Sentence);
        Assert.Equal(new[] { "SAR" }, terms.AllowedCurrencies);
    }

    [Fact]
    public void Terms_from_the_word_table_reader_and_the_text_combine()
    {
        var terms = BuyerQuoteTermsService.FromTermsAndText(new[]
        {
            new BuyerTerms.Term("delivery_terms", "Delivery terms", "AMC/SAC; also price VDD/VTC as an alternative", "…"),
            new BuyerTerms.Term("agreement", "Agreement", "2-year agreement; also offer 3-year as an alternative", "…"),
            new BuyerTerms.Term("quote_currency", "Quote in", "USD or SAR", "submitted in either USD or SAR"),
            new BuyerTerms.Term("validity", "Quote valid for", "At least 60 days after closing",
                "Bidders shall keep a validity of at least sixty (60) days from the bid closing date."),
        }, null, "RFP 6000000028.docx");

        Assert.Equal(60, terms.Validity!.Days);
        Assert.Equal(new[] { "USD", "SAR" }, terms.AllowedCurrencies);
        Assert.StartsWith("AMC/SAC", terms.DeliveryTerms);
        Assert.StartsWith("2-year", terms.Agreement);
    }

    // ----------------------------------------------------------------- send warnings

    private static QuoteItem Priced(int? leadTime = 14, string? status = null, decimal price = 10m) => new()
    {
        Quantity = 1m, UnitPrice = price, DeliveryLeadTime = leadTime, PricingStatus = status, ItemDescription = "x",
        CreatedBy = "t",
    };

    [Fact]
    public void Validity_shorter_than_the_buyer_asks_is_a_warning_with_the_date_to_set()
    {
        var terms = BuyerQuoteTerms.None with
        {
            Validity = BuyerQuoteTermRules.ParseValidity("Quotation must be valid for 90 days from bid due date"),
        };
        var warnings = QuoteSendWarnings.Evaluate(Today, new DateTime(2026, 10, 17), new DateTime(2026, 9, 30), "SAR",
            new[] { Priced() }, terms);

        var warning = Assert.Single(warnings);
        Assert.Equal(QuoteSendWarnings.ValidityBelowBuyerMinimum, warning.Code);
        Assert.Equal(new DateTime(2026, 12, 29), warning.SuggestedValidUntil);
        Assert.Contains("90 days from closing", warning.Message);
        Assert.Contains("17 Oct 2026", warning.Message);
    }

    [Fact]
    public void A_quote_that_meets_every_buyer_term_has_no_warning()
    {
        var terms = BuyerQuoteTerms.None with
        {
            Validity = BuyerQuoteTermRules.ParseValidity("valid for 90 days from bid due date"),
            AllowedCurrencies = new[] { "USD", "SAR" },
        };
        Assert.Empty(QuoteSendWarnings.Evaluate(Today, new DateTime(2026, 12, 29), new DateTime(2026, 9, 30), "SAR",
            new[] { Priced(), Priced(leadTime: 0) }, terms));
    }

    [Fact]
    public void A_closed_bid_estimates_price_to_follow_no_delivery_time_and_a_wrong_currency_are_each_named()
    {
        var terms = BuyerQuoteTerms.None with { AllowedCurrencies = new[] { "SAR" } };
        var warnings = QuoteSendWarnings.Evaluate(Today, Today.AddDays(30), new DateTime(2026, 9, 20), "USD",
            new[]
            {
                Priced(status: QuoteLinePricing.Estimate),
                Priced(status: QuoteLinePricing.ToFollow, price: 0m, leadTime: null),
                Priced(status: QuoteLinePricing.ToFollow, price: 0m, leadTime: null),
                Priced(leadTime: null),
                Priced(status: QuoteLinePricing.NotQuoted, price: 0m, leadTime: null),
            }, terms);

        Assert.Equal(new[]
        {
            QuoteSendWarnings.BidClosed, QuoteSendWarnings.LinesNotFirm, QuoteSendWarnings.LeadTimeMissing,
            QuoteSendWarnings.CurrencyNotAllowed,
        }, warnings.Select(w => w.Code));
        Assert.Equal("The bid closed on 20 Sep 2026.", warnings[0].Message);
        Assert.Equal("2 lines are \"Price to follow\" and 1 line is an estimate. Tender buyers treat these as no bid.", warnings[1].Message);
        Assert.Equal("1 priced line has no delivery time.", warnings[2].Message);
        Assert.Equal("Buyer asks for prices in SAR. This quote is in USD.", warnings[3].Message);
    }

    [Fact]
    public void A_newer_buyer_revision_names_the_revision_and_the_changed_quantities()
    {
        var warning = QuoteSendWarnings.NewerBuyerRevision(3, 4, new[]
        {
            new DTOs.QuoteDTOs.QuoteRevisionLineChangeDTO { Line = "2", Field = "quantity", From = "20", To = "35" },
            new DTOs.QuoteDTOs.QuoteRevisionLineChangeDTO { Line = "4", Field = "quantity", From = "1500", To = "2000" },
        }, canApply: true);

        Assert.NotNull(warning);
        Assert.Equal("The buyer sent a newer version (rev 4): 2 lines changed. Quantities: line 2 20 → 35, line 4 1500 → 2000.",
            warning!.Message);
        Assert.True(warning.CanApply);
        Assert.Null(QuoteSendWarnings.NewerBuyerRevision(4, 4, Array.Empty<DTOs.QuoteDTOs.QuoteRevisionLineChangeDTO>(), true));
    }
}
