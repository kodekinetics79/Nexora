using System;
using MimeKit;
using MimeKit.Tnef;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <param name="ReasonCode">Stable snake_case code recorded on the component.</param>
/// <param name="OperatorDetail">Operator-safe sentence. Never names a provider or a bucket.</param>
public readonly record struct ContainerRefusal(string ReasonCode, string OperatorDetail);

/// <summary>
/// Recognises email container formats that hide commercial content behind a wrapper this system
/// does not open, and refuses them TRUTHFULLY rather than letting them look like ordinary
/// unsupported files.
///
/// <para><b>Why refusal is the right answer here, for now.</b> The repository has no reader for
/// any of these formats. MimeKit exposes <c>TnefPart.ExtractAttachments()</c>, but exposing an
/// API is not the same as proving bounded extraction: that method materialises the expanded
/// container before any of this pipeline's budgets can see it, so wiring it would reintroduce
/// exactly the unbounded-expansion hazard the bounded decoder was built to remove. Until
/// extraction can be bounded, <c>winmail.dat</c> is unsupported commercial evidence and goes to a
/// human — a slower answer, never a wrong one.</para>
///
/// <para><b>The distinction that matters.</b> These parts are refused with reasons that mark them
/// as commercially significant, so they send the message to <c>NeedsReview</c>. They are NOT
/// classified as decoration and never silently dropped. An Exchange sender in RTF mode ships
/// every real attachment inside <c>winmail.dat</c>; treating that as an unremarkable unsupported
/// file would lose the entire enquiry silently.</para>
/// </summary>
public static class ContainerFormatClassifier
{
    public const string TnefContainer = "tnef_container_unsupported";
    public const string EncryptedMessage = "encrypted_message_not_readable";
    public const string SignedContainer = "signed_container_not_unwrapped";
    public const string AppleDoubleResourceFork = "appledouble_resource_fork";
    public const string CalendarInvite = "calendar_invite";
    public const string DeliveryStatus = "delivery_status_notification";

    /// <summary>Null when the part is not a recognised container and should proceed normally.</summary>
    public static ContainerRefusal? Classify(MimePart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        var mediaType = part.ContentType?.MediaType ?? string.Empty;
        var subType = part.ContentType?.MediaSubtype ?? string.Empty;

        // TNEF — an Exchange RTF sender puts EVERY real attachment in here.
        if (part is TnefPart
            || subType.Equals("ms-tnef", StringComparison.OrdinalIgnoreCase)
            || subType.Equals("vnd.ms-tnef", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part.FileName, "winmail.dat", StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerRefusal(TnefContainer,
                "This message was sent in a format that packs its attachments into a single "
                + "container this system does not open yet. The attachments are still in the "
                + "original message and need a person to look at them.");
        }

        // S/MIME. Opaque signed-data and enveloped-data hide the whole real message; unwrapping
        // needs a governed key store this deployment does not have, and pretending otherwise
        // would report an empty inquiry for a signed enterprise RFQ.
        if (mediaType.Equals("application", StringComparison.OrdinalIgnoreCase))
        {
            if (subType.Equals("pkcs7-mime", StringComparison.OrdinalIgnoreCase)
                || subType.Equals("x-pkcs7-mime", StringComparison.OrdinalIgnoreCase))
            {
                var smimeType = part.ContentType?.Parameters["smime-type"];
                var encrypted = string.Equals(smimeType, "enveloped-data", StringComparison.OrdinalIgnoreCase);
                return new ContainerRefusal(
                    encrypted ? EncryptedMessage : SignedContainer,
                    encrypted
                        ? "This message is encrypted, so its contents cannot be read here. It "
                          + "needs a person with access to the decryption key."
                        : "This message is digitally signed in a form that wraps its contents. "
                          + "It has not been opened here and needs a person to review it.");
            }

            // Detached signatures accompany readable content; refusing the signature part itself
            // as commercially significant would send every signed customer to review forever.
            if (subType.Equals("pkcs7-signature", StringComparison.OrdinalIgnoreCase)
                || subType.Equals("x-pkcs7-signature", StringComparison.OrdinalIgnoreCase)
                || subType.Equals("pgp-signature", StringComparison.OrdinalIgnoreCase))
            {
                return new ContainerRefusal(SignedContainer,
                    "A digital signature accompanying this message. It carries no commercial "
                    + "content of its own.");
            }

            // AppleDouble resource fork. It usually shares the data fork's filename, so
            // extracting it produces binary noise under the real document's name. Refusing the
            // FORK does not hide the primary file — that arrives as its own part and is
            // processed normally.
            if (subType.Equals("applefile", StringComparison.OrdinalIgnoreCase))
            {
                return new ContainerRefusal(AppleDoubleResourceFork,
                    "Mac resource-fork metadata that accompanies a file sent from Apple Mail. "
                    + "The file itself is handled separately.");
            }
        }

        // Calendar invites. Classified conservatively: an invite is refused as a container we do
        // not read, NOT assumed non-commercial, because a supplier meeting request can carry a
        // real deadline and only a person can judge that.
        if (mediaType.Equals("text", StringComparison.OrdinalIgnoreCase)
            && subType.Equals("calendar", StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerRefusal(CalendarInvite,
                "A calendar invitation. It has not been read as commercial content.");
        }

        return null;
    }

    /// <summary>
    /// Whether a message is a delivery-status notification, on deterministic MIME evidence only.
    ///
    /// <para><c>multipart/report; report-type=delivery-status</c> is a machine-generated
    /// structure a sender cannot plausibly produce by accident, which is what makes it safe to
    /// treat as NoInquiry. A subject line reading "Undeliverable" is not evidence — customers
    /// forward bounces asking what went wrong, and that forward is a real enquiry.</para>
    /// </summary>
    public static bool IsDeliveryStatusNotification(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Body is not Multipart { ContentType: { } contentType } multipart) return false;
        if (!contentType.MediaSubtype.Equals("report", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(contentType.Parameters["report-type"], "delivery-status",
                StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var part in multipart)
            if (part is MessageDeliveryStatus) return true;

        return false;
    }
}
