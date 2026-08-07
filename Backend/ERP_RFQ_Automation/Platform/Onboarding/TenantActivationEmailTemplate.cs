using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Notifications.Templating;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// The founding-administrator invitation email: Nexora's transactional shell, a single call to
/// action, and — by construction — no credential anywhere in it.
///
/// <para><b>Why it lives here and not in <c>Notifications/Templating/EmailTemplates.cs</c>.</b>
/// That registry is a sealed static dictionary with no registration hook, and the notifications
/// module is owned elsewhere. Rather than reach into a file this module does not own, the
/// template follows the established shape — subject + inline-CSS table HTML + a plain-text
/// fallback, all driven by <c>{{token}}</c> substitution — and renders into the module's own
/// <see cref="RenderedEmail"/> so the composed message is indistinguishable downstream.</para>
///
/// <para><b>The rule this template exists to keep.</b> There is no password token, no
/// credential parameter, and nothing that could carry one: the recipient chooses their own
/// password on the activation page. An operator reading this file can verify in one pass that
/// no secret other than the single-use link can ever reach a mailbox.</para>
/// </summary>
public static class TenantActivationEmailTemplate
{
    /// <summary>Matches the same <c>{{ token }}</c> shape the notifications renderer accepts.</summary>
    private static readonly Regex TokenRegex =
        new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private const string Subject =
        "Activate your {{companyName}} administrator account on Nexora";

    private const string Html = """
<!DOCTYPE html>
<html lang="en" xmlns="http://www.w3.org/1999/xhtml">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta http-equiv="X-UA-Compatible" content="IE=edge" />
  <title>Activate your Nexora administrator account</title>
</head>
<body style="margin:0; padding:0; background-color:#eef2f7; -webkit-text-size-adjust:100%; -ms-text-size-adjust:100%;">
  <span style="display:none; font-size:1px; color:#eef2f7; line-height:1px; max-height:0; max-width:0; opacity:0; overflow:hidden;">Set your password and sign in to {{companyName}} on Nexora. The link expires {{expiresOn}}.</span>
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
              <h1 style="margin:0 0 16px 0; font-size:20px; color:#0f172a;">Activate your administrator account</h1>
              <p style="margin:0 0 16px 0;">Hi {{recipientName}},</p>
              <p style="margin:0 0 16px 0;">A Nexora workspace has been created for <strong>{{companyName}}</strong>, and you have been set up as its administrator. Use the button below to choose your password and sign in for the first time.</p>
              <table role="presentation" cellpadding="0" cellspacing="0" style="margin:24px 0;">
                <tr>
                  <td align="center" bgcolor="#2563eb" style="border-radius:8px;">
                    <a href="{{activationUrl}}" target="_blank" style="display:inline-block; padding:12px 28px; font-size:15px; font-weight:600; color:#ffffff; text-decoration:none; border-radius:8px;">Set your password</a>
                  </td>
                </tr>
              </table>
              <p style="margin:0 0 16px 0; color:#475569; font-size:14px;">This link works once and expires on <strong>{{expiresOn}}</strong> ({{validityWindow}} from when it was sent). If the button does not work, copy this address into your browser:</p>
              <p style="margin:0 0 16px 0; font-size:13px; word-break:break-all;"><a href="{{activationUrl}}" style="color:#2563eb;">{{activationUrl}}</a></p>
              <p style="margin:0 0 8px 0; color:#475569; font-size:14px;">Nobody at Nexora — including whoever created this workspace — knows or can see the password you choose. {{supportLine}}</p>
            </td>
          </tr>
          <tr>
            <td style="padding:24px 32px 32px 32px;">
              <hr style="border:none; border-top:1px solid #e2e8f0; margin:0 0 16px 0;" />
              <p style="margin:0; color:#64748b; font-size:12px; line-height:1.6;">
                If you were not expecting this invitation, ignore this message and the link will expire on its own. No account is active until the password is set.
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

    private const string Text = """
Activate your administrator account

Hi {{recipientName}},

A Nexora workspace has been created for {{companyName}}, and you have been set up
as its administrator. Open the link below to choose your password and sign in for
the first time.

{{activationUrl}}

This link works once and expires on {{expiresOn}} ({{validityWindow}} from when it
was sent).

Nobody at Nexora — including whoever created this workspace — knows or can see the
password you choose. {{supportLine}}

If you were not expecting this invitation, ignore this message and the link will
expire on its own. No account is active until the password is set.

— Nexora (automated notification)
""";

    public static RenderedEmail Render(IDictionary<string, string?> model) => new()
    {
        Subject = Substitute(Subject, model),
        HtmlBody = Substitute(Html, model),
        TextBody = Substitute(Text, model)
    };

    private static string Substitute(string template, IDictionary<string, string?> model) =>
        TokenRegex.Replace(template, match =>
            // An unknown token is stripped rather than passed through: leaking a literal
            // "{{supportLine}}" into a customer's first impression of the product is worse
            // than an empty sentence.
            model.TryGetValue(match.Groups[1].Value, out var value) ? value ?? string.Empty : string.Empty);
}
