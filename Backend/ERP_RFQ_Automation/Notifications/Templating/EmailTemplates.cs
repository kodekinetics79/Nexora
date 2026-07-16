using System.Collections.Generic;

namespace ERP_RFQ_Automation.Notifications.Templating
{
    /// <summary>
    /// A single template's static definition: subject line + HTML body + plain-text
    /// body, all containing <c>{{placeholder}}</c> tokens the renderer fills in.
    /// </summary>
    public sealed class EmailTemplateDefinition
    {
        public string Subject { get; init; } = string.Empty;
        public string Html { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
    }

    /// <summary>
    /// Embedded, Nexora-branded transactional email templates. Responsive,
    /// inline-CSS HTML that renders across common email clients, each paired with a
    /// plain-text fallback. No external files or templating engine.
    /// </summary>
    public static class EmailTemplates
    {
        // Known template names (also referenced by INotificationService).
        public const string LeadNeedsReview = "lead-needs-review";
        public const string RfqToSupplier = "rfq-to-supplier";
        public const string QuoteToBuyer = "quote-to-buyer";
        public const string OrderConfirmation = "order-confirmation";

        /// <summary>
        /// Shared responsive shell. Table-based layout with inline CSS for maximum
        /// email-client compatibility (Outlook, Gmail, Apple Mail). Body content is
        /// injected via the {{__CONTENT__}} token before per-model tokens are applied.
        /// </summary>
        private const string Shell = """
<!DOCTYPE html>
<html lang="en" xmlns="http://www.w3.org/1999/xhtml">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta http-equiv="X-UA-Compatible" content="IE=edge" />
  <title>{{__TITLE__}}</title>
</head>
<body style="margin:0; padding:0; background-color:#eef2f7; -webkit-text-size-adjust:100%; -ms-text-size-adjust:100%;">
  <span style="display:none; font-size:1px; color:#eef2f7; line-height:1px; max-height:0; max-width:0; opacity:0; overflow:hidden;">{{__PREHEADER__}}</span>
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#eef2f7;">
    <tr>
      <td align="center" style="padding:24px 12px;">
        <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:600px; max-width:600px; background-color:#ffffff; border-radius:12px; overflow:hidden; box-shadow:0 1px 3px rgba(16,24,40,0.08); font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;">
          <tr>
            <td style="background-color:#0f172a; padding:24px 32px;">
              <span style="font-size:22px; font-weight:700; letter-spacing:0.5px; color:#ffffff;">Nexora</span>
              <span style="font-size:12px; color:#94a3b8; margin-left:8px;">Supply &amp; Procurement</span>
            </td>
          </tr>
          <tr>
            <td style="padding:32px 32px 8px 32px; color:#0f172a; font-size:16px; line-height:1.55;">
              {{__CONTENT__}}
            </td>
          </tr>
          <tr>
            <td style="padding:24px 32px 32px 32px;">
              <hr style="border:none; border-top:1px solid #e2e8f0; margin:0 0 16px 0;" />
              <p style="margin:0; color:#64748b; font-size:12px; line-height:1.6;">
                This is an automated message from the Nexora platform. If you were not expecting it, you can safely ignore it.
              </p>
              <p style="margin:8px 0 0 0; color:#94a3b8; font-size:12px;">&copy; Nexora &middot; Automated notification</p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>
""";

        /// <summary>Reusable CTA button block (table-based for Outlook).</summary>
        private const string CtaButton = """
<table role="presentation" cellpadding="0" cellspacing="0" style="margin:24px 0;">
  <tr>
    <td align="center" bgcolor="#2563eb" style="border-radius:8px;">
      <a href="{{ctaUrl}}" target="_blank" style="display:inline-block; padding:12px 28px; font-size:15px; font-weight:600; color:#ffffff; text-decoration:none; border-radius:8px;">{{ctaLabel}}</a>
    </td>
  </tr>
</table>
""";

        private static string Wrap(string title, string preheader, string content) =>
            Shell
                .Replace("{{__TITLE__}}", title)
                .Replace("{{__PREHEADER__}}", preheader)
                .Replace("{{__CONTENT__}}", content);

        private static readonly IReadOnlyDictionary<string, EmailTemplateDefinition> _templates =
            new Dictionary<string, EmailTemplateDefinition>(System.StringComparer.OrdinalIgnoreCase)
            {
                [LeadNeedsReview] = new EmailTemplateDefinition
                {
                    Subject = "Action needed: document requires review — {{documentName}}",
                    Html = Wrap(
                        "Document needs review",
                        "A new document needs a human review in Nexora.",
                        """
<h1 style="margin:0 0 16px 0; font-size:20px; color:#0f172a;">A document needs your review</h1>
<p style="margin:0 0 16px 0;">Hi {{reviewerName}},</p>
<p style="margin:0 0 16px 0;">An incoming document was processed automatically but needs a human to confirm the extracted details before it moves forward.</p>
<table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="margin:8px 0 8px 0; border-collapse:collapse;">
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px; width:40%;">Document</td><td style="padding:8px 0; color:#0f172a; font-size:14px; font-weight:600;">{{documentName}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Source</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{source}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Received</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{receivedAt}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Confidence</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{confidence}}</td></tr>
</table>
<p style="margin:0 0 8px 0;">{{summary}}</p>
""" + CtaButton),
                    Text = """
A document needs your review

Hi {{reviewerName}},

An incoming document was processed automatically but needs a human to confirm the extracted details before it moves forward.

Document:   {{documentName}}
Source:     {{source}}
Received:   {{receivedAt}}
Confidence: {{confidence}}

{{summary}}

Review it here: {{ctaUrl}}

— Nexora (automated notification)
"""
                },

                [RfqToSupplier] = new EmailTemplateDefinition
                {
                    Subject = "Request for Quotation {{rfqNumber}} from {{buyerCompany}}",
                    Html = Wrap(
                        "Request for Quotation",
                        "You've received a new RFQ from {{buyerCompany}} on Nexora.",
                        """
<h1 style="margin:0 0 16px 0; font-size:20px; color:#0f172a;">Request for Quotation</h1>
<p style="margin:0 0 16px 0;">Dear {{supplierName}},</p>
<p style="margin:0 0 16px 0;">{{buyerCompany}} invites you to submit a quotation for the following request. We'd appreciate your best pricing and lead times.</p>
<table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="margin:8px 0; border-collapse:collapse;">
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px; width:40%;">RFQ number</td><td style="padding:8px 0; color:#0f172a; font-size:14px; font-weight:600;">{{rfqNumber}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Title</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{rfqTitle}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Items</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{itemSummary}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Respond by</td><td style="padding:8px 0; color:#0f172a; font-size:14px; font-weight:600;">{{dueDate}}</td></tr>
</table>
<p style="margin:0 0 8px 0;">{{message}}</p>
""" + CtaButton),
                    Text = """
Request for Quotation {{rfqNumber}}

Dear {{supplierName}},

{{buyerCompany}} invites you to submit a quotation for the following request.

RFQ number: {{rfqNumber}}
Title:      {{rfqTitle}}
Items:      {{itemSummary}}
Respond by: {{dueDate}}

{{message}}

Submit your quotation here: {{ctaUrl}}

— Nexora (automated notification)
"""
                },

                [QuoteToBuyer] = new EmailTemplateDefinition
                {
                    Subject = "Your quotation {{quoteNumber}} from {{supplierCompany}}",
                    Html = Wrap(
                        "Your quotation is ready",
                        "Quotation {{quoteNumber}} is ready to view.",
                        """
<h1 style="margin:0 0 16px 0; font-size:20px; color:#0f172a;">Your quotation is ready</h1>
<p style="margin:0 0 16px 0;">Hi {{buyerName}},</p>
<p style="margin:0 0 16px 0;">Thank you for your interest. {{supplierCompany}} has prepared the following quotation for your request.</p>
<table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="margin:8px 0; border-collapse:collapse;">
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px; width:40%;">Quotation</td><td style="padding:8px 0; color:#0f172a; font-size:14px; font-weight:600;">{{quoteNumber}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Reference RFQ</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{rfqNumber}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Total</td><td style="padding:8px 0; color:#0f172a; font-size:16px; font-weight:700;">{{totalAmount}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Valid until</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{validUntil}}</td></tr>
</table>
<p style="margin:0 0 8px 0;">{{message}}</p>
""" + CtaButton),
                    Text = """
Your quotation {{quoteNumber}} is ready

Hi {{buyerName}},

{{supplierCompany}} has prepared the following quotation for your request.

Quotation:     {{quoteNumber}}
Reference RFQ: {{rfqNumber}}
Total:         {{totalAmount}}
Valid until:   {{validUntil}}

{{message}}

View the full quotation here: {{ctaUrl}}

— Nexora (automated notification)
"""
                },

                [OrderConfirmation] = new EmailTemplateDefinition
                {
                    Subject = "Order confirmed: {{orderNumber}}",
                    Html = Wrap(
                        "Order confirmed",
                        "Order {{orderNumber}} has been confirmed.",
                        """
<h1 style="margin:0 0 16px 0; font-size:20px; color:#0f172a;">Your order is confirmed</h1>
<p style="margin:0 0 16px 0;">Hi {{customerName}},</p>
<p style="margin:0 0 16px 0;">Good news — your order has been confirmed and is now being processed. Here are the details for your records.</p>
<table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="margin:8px 0; border-collapse:collapse;">
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px; width:40%;">Order number</td><td style="padding:8px 0; color:#0f172a; font-size:14px; font-weight:600;">{{orderNumber}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Order date</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{orderDate}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Items</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{itemSummary}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Total</td><td style="padding:8px 0; color:#0f172a; font-size:16px; font-weight:700;">{{totalAmount}}</td></tr>
  <tr><td style="padding:8px 0; color:#64748b; font-size:14px;">Est. delivery</td><td style="padding:8px 0; color:#0f172a; font-size:14px;">{{estimatedDelivery}}</td></tr>
</table>
<p style="margin:0 0 8px 0;">{{message}}</p>
""" + CtaButton),
                    Text = """
Your order is confirmed: {{orderNumber}}

Hi {{customerName}},

Your order has been confirmed and is now being processed.

Order number:  {{orderNumber}}
Order date:    {{orderDate}}
Items:         {{itemSummary}}
Total:         {{totalAmount}}
Est. delivery: {{estimatedDelivery}}

{{message}}

Track your order here: {{ctaUrl}}

— Nexora (automated notification)
"""
                }
            };

        /// <summary>Returns the template definition, or null if the name is unknown.</summary>
        public static EmailTemplateDefinition? Get(string templateName) =>
            _templates.TryGetValue(templateName, out var def) ? def : null;
    }
}
