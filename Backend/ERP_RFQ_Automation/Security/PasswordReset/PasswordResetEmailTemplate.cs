using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Notifications.Templating;

namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// The password-reset email: Nexora's transactional shell, one call to action, and — by
/// construction — no credential anywhere in it.
///
/// <para><b>Why it lives here and not in <c>Notifications/Templating/EmailTemplates.cs</c>.</b>
/// Same reason <c>TenantActivationEmailTemplate</c> does: that registry is a sealed static
/// dictionary with no registration hook, and the notifications module is owned elsewhere. This
/// follows the established shape — subject + inline-CSS table HTML + a plain-text fallback, all
/// driven by <c>{{token}}</c> substitution — and renders into the same <see cref="RenderedEmail"/>
/// so the composed message is indistinguishable downstream.</para>
///
/// <para><b>The one paragraph that is not decoration.</b> "If you did not ask for this, you can
/// ignore it — your password has not changed" is the entire defence for the person whose address
/// somebody else typed into the form. Without it, an unexpected reset mail reads as evidence of a
/// break-in and produces a panicked support call; with it, the recipient knows the correct action
/// is to do nothing. It is stated in both bodies and it must survive every future edit.</para>
///
/// <para><b>No name of anything.</b> Unlike the invitation, this message names no company. The
/// recipient of a reset mail may be someone who never asked for it, and the mailbox may not be
/// theirs alone — telling a stranger which organisation an address belongs to is exactly the
/// disclosure the request endpoint refuses to make over HTTP, and it would be perverse to make
/// it over SMTP instead.</para>
/// </summary>
public static class PasswordResetEmailTemplate
{
    /// <summary>Matches the same <c>{{ token }}</c> shape the notifications renderer accepts.</summary>
    private static readonly Regex TokenRegex =
        new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private const string Subject = "Reset your Nexora password";

    private const string Html = """
<!DOCTYPE html>
<html lang="en" xmlns="http://www.w3.org/1999/xhtml">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta http-equiv="X-UA-Compatible" content="IE=edge" />
  <title>Reset your Nexora password</title>
</head>
<body style="margin:0; padding:0; background-color:#eef2f7; -webkit-text-size-adjust:100%; -ms-text-size-adjust:100%;">
  <span style="display:none; font-size:1px; color:#eef2f7; line-height:1px; max-height:0; max-width:0; opacity:0; overflow:hidden;">Choose a new password for your Nexora account. The link expires {{expiresOn}}.</span>
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
              <h1 style="margin:0 0 16px 0; font-size:20px; color:#0f172a;">Reset your password</h1>
              <p style="margin:0 0 16px 0;">Hi {{recipientName}},</p>
              <p style="margin:0 0 16px 0;">Somebody asked to reset the password for this Nexora account. Use the button below to choose a new one.</p>
              <table role="presentation" cellpadding="0" cellspacing="0" style="margin:24px 0;">
                <tr>
                  <td align="center" bgcolor="#2563eb" style="border-radius:8px;">
                    <a href="{{resetUrl}}" target="_blank" style="display:inline-block; padding:12px 28px; font-size:15px; font-weight:600; color:#ffffff; text-decoration:none; border-radius:8px;">Choose a new password</a>
                  </td>
                </tr>
              </table>
              <p style="margin:0 0 16px 0; color:#475569; font-size:14px;">This link works once and expires on <strong>{{expiresOn}}</strong> ({{validityWindow}} from when it was sent). If the button does not work, copy this address into your browser:</p>
              <p style="margin:0 0 16px 0; font-size:13px; word-break:break-all;"><a href="{{resetUrl}}" style="color:#2563eb;">{{resetUrl}}</a></p>
              <p style="margin:0 0 8px 0; color:#475569; font-size:14px;">Nobody at Nexora can see your password, before or after you change it. {{supportLine}}</p>
            </td>
          </tr>
          <tr>
            <td style="padding:24px 32px 32px 32px;">
              <hr style="border:none; border-top:1px solid #e2e8f0; margin:0 0 16px 0;" />
              <p style="margin:0; color:#64748b; font-size:12px; line-height:1.6;">
                <strong>If you did not ask for this, you can ignore this message.</strong> Your password has not changed and nothing has happened to your account. The link expires on its own.
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
Reset your password

Hi {{recipientName}},

Somebody asked to reset the password for this Nexora account. Open the link below
to choose a new one.

{{resetUrl}}

This link works once and expires on {{expiresOn}} ({{validityWindow}} from when it
was sent).

Nobody at Nexora can see your password, before or after you change it.
{{supportLine}}

If you did not ask for this, you can ignore this message. Your password has not
changed and nothing has happened to your account. The link expires on its own.

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
            // An unknown token is stripped rather than passed through: a literal "{{supportLine}}"
            // in a security email is worse than an empty sentence.
            model.TryGetValue(match.Groups[1].Value, out var value) ? value ?? string.Empty : string.Empty);
}
