using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ERP_RFQ_Automation.CommercialRouting;

internal static class RoutingRequestFingerprint
{
    public static string ForRoute(long businessUnitId, RouteLeadCommand command) => Hash(new
    {
        operation = "route-lead",
        businessUnitId,
        command.LeadId,
        correlationId = command.CorrelationId.Trim(),
        scopeKeys = (command.ScopeKeys ?? new Dictionary<OwnershipScope, string?>())
            .OrderBy(pair => pair.Key)
            .Select(pair => new { scope = pair.Key.ToString(), value = Normalize(pair.Value) })
            .ToArray()
    });

    public static string ForManualAssignment(
        long businessUnitId, ManualAssignLeadCommand command, long? workItemId = null) => Hash(new
        {
            operation = workItemId.HasValue ? "assign-queue-item" : "assign-lead",
            businessUnitId,
            workItemId,
            command.LeadId,
            command.AssignedToUserId,
            command.AssignedByUserId,
            correlationId = command.CorrelationId.Trim(),
            assignmentScope = command.AssignmentScope.ToString(),
            comment = Normalize(command.Comment),
            command.EnforceExpectedAssignee,
            command.ExpectedAssigneeId
        });

    public static string ForQueueAssignment(
        long businessUnitId, long workItemId, AssignQueueItemCommand command) => Hash(new
        {
            operation = "assign-queue-item",
            businessUnitId,
            workItemId,
            command.ExpectedVersion,
            command.AssignedToUserId,
            command.AssignedByUserId,
            correlationId = command.CorrelationId.Trim(),
            assignmentScope = command.AssignmentScope.ToString(),
            comment = Normalize(command.Comment)
        });

    public static string? ReadFromExplanation(string explanation)
    {
        try
        {
            using var document = JsonDocument.Parse(explanation);
            return document.RootElement.TryGetProperty("requestHash", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Hash(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
