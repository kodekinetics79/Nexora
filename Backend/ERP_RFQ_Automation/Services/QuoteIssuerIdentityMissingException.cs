namespace ERP_RFQ_Automation.Services;

/// <summary>
/// Thrown when a customer-facing quotation is about to be produced for a business unit whose
/// own records cannot say who is sending it.
///
/// <para><b>Why this refuses instead of substituting something.</b> The code this replaces read
/// <c>config?.CompanyEmail ?? quote.Rfq?.Lead?.Clientemail</c>. The second term is the address the
/// ENQUIRY ARRIVED FROM — the customer's own — so a tenant that had never filled in its seller
/// email sent that customer a quotation naming the customer as the sender, beside a placeholder
/// street address and a +1 800 telephone number. Nothing about the resulting document looks
/// wrong; it is simply, quietly, from nobody. A refusal is visible in one second and fixable in
/// one screen. A plausible wrong document goes to a buyer and cannot be recalled.</para>
///
/// <para>A distinct type rather than <see cref="InvalidOperationException"/> for the same two
/// reasons <see cref="Intelligence.Pricing.PriceAttestationRequiredException"/> is one: the PDF
/// endpoint maps it to 409 carrying this reason verbatim, and the quote-delivery dispatcher must
/// NOT retry it — nobody's setup screen changes between attempt one and attempt eight, so
/// retrying only keeps a doomed send alive in the outbox and dresses a configuration gap up as
/// flaky infrastructure.</para>
///
/// <para><see cref="Exception.Message"/> names the screen that fixes it, because the person who
/// meets this failure is a sales rep who did nothing wrong.</para>
/// </summary>
/// <para>It derives from <see cref="InvalidOperationException"/> deliberately: the PDF and send
/// endpoints already map that to 409 carrying the message, so the rep sees this reason without a
/// new controller branch. The distinct TYPE is what the dispatcher needs, and it is caught there
/// ahead of the generic handler.</para>
public sealed class QuoteIssuerIdentityMissingException : InvalidOperationException
{
    public QuoteIssuerIdentityMissingException(string message) : base(message) { }
}
