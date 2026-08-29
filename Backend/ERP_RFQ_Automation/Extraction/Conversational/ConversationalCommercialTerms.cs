using System.Globalization;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.Extraction.Conversational;

/// <summary>
/// Recovers explicitly labelled commercial terms from the exact prose submitted to the model.
/// This is a narrow deterministic backstop: it never infers from addresses or unlabelled prose.
/// </summary>
internal static partial class ConversationalCommercialTerms
{
    internal static LeadExtractionResult Apply(string source, LeadExtractionResult result)
    {
        var freshSource = BeforeQuotedHistory(source);
        var required = Capture(RequiredDeliveryDate(), freshSource);
        var parsedRequired = RfqDateParser.Parse(required);
        var delivery = Capture(DeliveryLocation(), freshSource);
        var agreement = Capture(AgreementReference(), freshSource);
        var modelRequired = Clean(result.RequiredDeliveryDate);
        var modelDelivery = Clean(result.DeliveryLocation);
        var modelAgreement = Clean(result.AgreementReference);
        var backfilledRequired = modelRequired is null && parsedRequired.HasValue;
        var backfilledDelivery = modelDelivery is null && delivery is not null;
        var backfilledAgreement = modelAgreement is null && agreement is not null;

        return result with
        {
            RequiredDeliveryDate = modelRequired
                ?? parsedRequired?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RequiredDeliveryDateConfidence = result.RequiredDeliveryDateConfidence
                ?? (backfilledRequired ? 1d : null),
            DeliveryLocation = modelDelivery ?? delivery,
            DeliveryLocationConfidence = result.DeliveryLocationConfidence
                ?? (backfilledDelivery ? 1d : null),
            AgreementReference = modelAgreement ?? agreement,
            AgreementReferenceConfidence = result.AgreementReferenceConfidence
                ?? (backfilledAgreement ? 1d : null)
        };
    }

    /// <summary>
    /// A reply or forward can carry labelled values from an older request. Those values are
    /// evidence for the old message, not the fresh request, so the deterministic backstop must
    /// obey the same fresh-text boundary as the conversational model instructions.
    /// </summary>
    private static string BeforeQuotedHistory(string source)
    {
        using var reader = new StringReader(source);
        var fresh = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(">", StringComparison.Ordinal)
                || trimmed.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase)
                || (trimmed.StartsWith("On ", StringComparison.OrdinalIgnoreCase)
                    && trimmed.EndsWith(" wrote:", StringComparison.OrdinalIgnoreCase)))
                break;
            fresh.Add(line);
        }

        return string.Join('\n', fresh);
    }

    private static string? Capture(Regex regex, string source)
    {
        var match = regex.Match(source);
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('.', ';');

    [GeneratedRegex(@"(?im)^\s*(?:required|requested)\s+delivery\s+date\s*:\s*([^\r\n]{1,120})\s*$")]
    private static partial Regex RequiredDeliveryDate();

    [GeneratedRegex(@"(?im)^\s*delivery\s+(?:location|address|to)\s*:\s*([^\r\n]{1,500})\s*$")]
    private static partial Regex DeliveryLocation();

    [GeneratedRegex(@"(?im)^\s*(?:agreement|contract|framework)\s+(?:reference|ref(?:erence)?\.?|number|no\.?)\s*:\s*([^\r\n]{1,100})\s*$")]
    private static partial Regex AgreementReference();
}
