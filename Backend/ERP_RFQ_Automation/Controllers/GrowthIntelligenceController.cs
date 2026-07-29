using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Growth;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial-intelligence")]
public sealed class GrowthIntelligenceController(
    GrowthIntelligenceService growth,
    ErpRfqAutomationContext db,
    IRoleGate roleGate) : ControllerBase
{
    [HttpGet("coaching-recovery")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    [RequireModulePermission("Customers", PermissionAction.View)]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    [RequireModulePermission("Orders", PermissionAction.View)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public async Task<ActionResult> CoachingRecovery([FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] long? salesRepUserId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = TenantId();
            var actorUserId = UserId();
            var manager = await IsManagerAsync(tenantId);
            var (fromUtc, toUtc, asOfUtc) = Period(from, to);
            var workspace = await growth.GetCoachingRecoveryWorkspaceAsync(tenantId, fromUtc, toUtc, asOfUtc,
                new GrowthAccessScope(actorUserId, manager, salesRepUserId), cancellationToken);
            return Ok(await ToWorkspaceResponse(tenantId, manager, workspace, cancellationToken));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem("Invalid growth-intelligence period", exception.Message,
                StatusCodes.Status400BadRequest));
        }
    }

    [HttpPost("coaching/{findingKey}/acknowledgements")]
    [RequireManagerRole]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    [RequireModulePermission("Customers", PermissionAction.View)]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    [RequireModulePermission("Orders", PermissionAction.View)]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public async Task<ActionResult> Acknowledge(string findingKey,
        [FromBody] CoachingAcknowledgementRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = TenantId();
            if (!await IsManagerAsync(tenantId)) return Forbid();
            if (findingKey.Length != 64 || findingKey.Any(character => !Uri.IsHexDigit(character)))
                return BadRequest(Problem("Invalid coaching finding", "A stable 64-character finding key is required.",
                    StatusCodes.Status400BadRequest));
            var (fromUtc, toUtc, asOfUtc) = Period(request.From, request.To);
            var result = await growth.AcknowledgeFindingAsync(tenantId,
                new AcknowledgeCoachingFindingCommand(findingKey, request.Disposition, request.Reason,
                    UserId(), IdempotencyKey(), CorrelationId(), fromUtc, toUtc, asOfUtc,
                    request.SalesRepUserId), cancellationToken);
            return Ok(new
            {
                disposition = result.DecisionCode,
                result.Reason,
                acknowledgedByName = await UserName(tenantId, result.ManagerUserId, cancellationToken),
                acknowledgedAt = result.CreatedAtUtc
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem("Invalid coaching acknowledgement", exception.Message,
                StatusCodes.Status400BadRequest));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem("Coaching acknowledgement conflict", exception.Message,
                StatusCodes.Status409Conflict));
        }
    }

    [HttpGet("/api/intelligence/customers/{customerId:long}/health")]
    [RequireModulePermission("Customers", PermissionAction.View)]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    [RequireModulePermission("Quotations", PermissionAction.View)]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public async Task<ActionResult> CustomerHealth(long customerId, [FromQuery] DateTime from,
        [FromQuery] DateTime to, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = TenantId();
            var (fromUtc, toUtc, asOfUtc) = Period(from, to);
            var health = await growth.GetCustomerHealthAsync(tenantId, customerId, fromUtc, toUtc, asOfUtc,
                cancellationToken);
            return Ok(ToCustomerHealthResponse(health));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem("Customer not found", exception.Message, StatusCodes.Status404NotFound));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem("Invalid customer-health period", exception.Message,
                StatusCodes.Status400BadRequest));
        }
    }

    private async Task<object> ToWorkspaceResponse(long tenantId, bool manager,
        CoachingRecoveryWorkspace workspace, CancellationToken cancellationToken)
    {
        var repIds = workspace.CoachingFindings.Select(item => item.SalesRepUserId)
            .Concat(workspace.RecoveryRows.Where(item => item.SalesRepUserId.HasValue)
                .Select(item => item.SalesRepUserId!.Value)).Distinct().ToArray();
        var customerIds = workspace.CoachingFindings.Where(item => item.CustomerId.HasValue)
            .Select(item => item.CustomerId!.Value)
            .Concat(workspace.RecoveryRows.Where(item => item.CustomerId.HasValue)
                .Select(item => item.CustomerId!.Value)).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(user => user.Buid == tenantId && repIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => ($"{user.FirstName} {user.LastName}").Trim(),
                cancellationToken);
        var customers = await db.Customers.AsNoTracking()
            .Where(customer => customer.Buid == tenantId && customerIds.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, customer => customer.Name, cancellationToken);

        return new
        {
            generatedAt = workspace.GeneratedAtUtc,
            workspace.PolicyVersion,
            scope = manager && !workspace.SalesRepScope.HasValue ? "tenant" : "assigned_to_me",
            dataCompleteness = new
            {
                status = workspace.DataCompleteness.IsComplete ? "complete" : "partial",
                workspace.DataCompleteness.IncompleteSources
            },
            coachingFindings = workspace.CoachingFindings.Select(item => new
            {
                item.FindingKey,
                item.Code,
                item.SalesRepUserId,
                salesRepName = users.GetValueOrDefault(item.SalesRepUserId) ?? $"Sales rep {item.SalesRepUserId}",
                item.CustomerId,
                customerName = item.CustomerId.HasValue ? customers.GetValueOrDefault(item.CustomerId.Value) : null,
                aggregateType = item.SourceAggregateType,
                aggregateId = item.SourceAggregateId,
                sourceVersion = item.SourceAggregateVersion,
                reference = $"{item.SourceAggregateType} {item.SourceAggregateId}",
                item.NexoraSerial,
                severity = Severity(item.Code),
                item.ObservedValue,
                observedUnit = Unit(item.Code),
                item.ThresholdValue,
                item.SampleSize,
                item.Confidence,
                asOf = item.GeneratedAtUtc,
                item.Recommendation,
                actionRoute = item.Route,
                workspace.PolicyVersion,
                evidence = item.Evidence.Select(ToEvidence),
                latestAcknowledgement = item.LatestAcknowledgement is null ? null : new
                {
                    disposition = item.LatestAcknowledgement.DecisionCode,
                    item.LatestAcknowledgement.Reason,
                    acknowledgedByName = users.GetValueOrDefault(item.LatestAcknowledgement.ManagerUserId),
                    acknowledgedAt = item.LatestAcknowledgement.CreatedAtUtc
                }
            }),
            recoveryOpportunities = workspace.RecoveryRows.Select(item => new
            {
                item.RecoveryKey,
                item.Code,
                sourceType = item.SourceAggregateType,
                sourceId = item.SourceAggregateId,
                sourceVersion = item.SourceAggregateVersion,
                item.CustomerId,
                customerName = item.CustomerId.HasValue ? customers.GetValueOrDefault(item.CustomerId.Value) : null,
                ownerUserId = item.SalesRepUserId,
                ownerName = item.SalesRepUserId.HasValue ? users.GetValueOrDefault(item.SalesRepUserId.Value) : null,
                item.NexoraSerial,
                severity = Severity(item.Code),
                title = Label(item.Code),
                explanation = item.Summary,
                item.RecommendedAction,
                actionRoute = item.Route,
                dueAt = item.Evidence.Where(fact => fact.Code.Contains("DUE", StringComparison.OrdinalIgnoreCase))
                    .Select(fact => fact.ObservedAtUtc).FirstOrDefault(),
                sampleSize = item.Evidence.Count,
                confidence = 1m,
                evidence = item.Evidence.Select(ToEvidence)
            })
        };
    }

    private static object ToCustomerHealthResponse(CustomerHealth health) => new
    {
        health.CustomerId,
        generatedAt = health.GeneratedAtUtc,
        dataCompleteness = new
        {
            status = health.DataCompleteness.IsComplete ? "complete" : "partial",
            health.DataCompleteness.IncompleteSources
        },
        period = new { from = health.Period.FromUtc, to = health.Period.ToUtc,
            previousFrom = health.Period.PreviousFromUtc, previousTo = health.Period.PreviousToUtc },
        rfqTrend = new { status = MetricStatus(health.RfqTrend.TrendPercent), reason = MetricReason(health.RfqTrend.TrendPercent),
            health.RfqTrend.CurrentCount, health.RfqTrend.PreviousCount, changePercent = health.RfqTrend.TrendPercent },
        quoteCoverage = new { status = MetricStatus(health.QuoteFunnel.CoveragePercent), reason = MetricReason(health.QuoteFunnel.CoveragePercent),
            rfqCount = health.QuoteFunnel.EligibleRfqCount, quotedRfqCount = health.QuoteFunnel.QuotedRfqCount,
            health.QuoteFunnel.CoveragePercent },
        quoteDecisions = new { status = MetricStatus(health.QuoteFunnel.ConversionPercent), reason = MetricReason(health.QuoteFunnel.ConversionPercent),
            decidedCount = health.QuoteFunnel.DecidedQuoteCount, wonCount = health.QuoteFunnel.WonQuoteCount,
            lostCount = health.QuoteFunnel.DecidedQuoteCount - health.QuoteFunnel.WonQuoteCount, pendingCount = (int?)null },
        conversion = new { status = MetricStatus(health.QuoteFunnel.ConversionPercent), reason = MetricReason(health.QuoteFunnel.ConversionPercent),
            ratePercent = health.QuoteFunnel.ConversionPercent, sampleSize = health.QuoteFunnel.DecidedQuoteCount },
        acceptedPrices = health.AcceptedPrices.Select(item => new
        {
            productId = (long?)null,
            partNumber = (string?)null,
            description = $"Quote {item.QuoteNumber} line {item.QuoteItemId}",
            item.CurrencyCode,
            item.Quantity,
            item.UnitPrice,
            acceptedOn = item.AcceptedAtUtc,
            evidence = ToEvidence(new EvidenceFact(item.EvidenceType, item.QuoteNumber, item.EvidenceType,
                item.EvidenceId, item.AcceptedAtUtc))
        }),
        margin = new { status = health.Margin.Status, reason = health.Margin.Reason,
            grossMarginPercent = health.Margin.Percent },
        revisionBurden = new { status = health.RevisionBurden.LeadRevisionCount + health.RevisionBurden.QuoteRevisionCount > 0 ? "available" : "insufficient-evidence",
            reason = health.RevisionBurden.LeadRevisionCount + health.RevisionBurden.QuoteRevisionCount > 0 ? null : "No persisted revisions in this cohort.",
            revisionCount = health.RevisionBurden.LeadRevisionCount + health.RevisionBurden.QuoteRevisionCount,
            inquiryCount = health.RfqTrend.CurrentCount + health.RfqTrend.PreviousCount,
            changedFieldCount = health.RevisionBurden.LeadChangedFieldCount,
            comparedFieldCount = health.RevisionBurden.LeadComparedFieldCount,
            fieldChangePercent = health.RevisionBurden.ChangedFieldPercent,
            changedLineCount = health.RevisionBurden.LeadChangedLineCount,
            comparedLineCount = health.RevisionBurden.LeadComparedLineCount,
            lineChangePercent = health.RevisionBurden.ChangedLinePercent },
        followUp = new { status = MetricStatus(health.FollowUp.ResponsePercent), reason = MetricReason(health.FollowUp.ResponsePercent),
            openCount = health.FollowUp.DueCount, health.FollowUp.OverdueCount,
            effectivenessPercent = health.FollowUp.ResponsePercent },
        lastCommercialActivity = health.LastCommercialActivityAtUtc.HasValue
            ? ToEvidence(new EvidenceFact("LAST_COMMERCIAL_ACTIVITY", health.LastCommercialActivityAtUtc.Value.ToString("O"),
                "Customer", health.CustomerId, health.LastCommercialActivityAtUtc)) : null,
        healthBand = health.Health.Band,
        healthReasons = health.Health.Reasons,
        opportunities = health.Opportunities.Select(item => new
        {
            code = item.HasCustomerQuote ? "OPEN_QUOTED_RFQ" : "UNQUOTED_RFQ",
            title = item.HasCustomerQuote ? $"Progress RFQ {item.RfqNumber}" : $"Quote RFQ {item.RfqNumber}",
            explanation = item.HasCustomerQuote ? "A customer quote exists and remains commercially active."
                : "No customer quote is linked to this active RFQ.",
            actionRoute = item.Route,
            evidence = new[] { ToEvidence(new EvidenceFact("CUSTOMER_QUOTE_PRESENT", item.HasCustomerQuote.ToString(),
                "Rfq", item.RfqId, item.ReceivedAtUtc)) }
        }),
        nextBestAction = health.NextAction is null ? null : new
        {
            title = health.NextAction.Label,
            explanation = Label(health.NextAction.Code),
            actionRoute = health.NextAction.Route,
            evidence = health.NextAction.Evidence.Select(ToEvidence)
        }
    };

    private static object ToEvidence(EvidenceFact fact) => new
    {
        recordType = fact.SourceType,
        recordId = fact.SourceId,
        reference = $"{Label(fact.Code)}: {fact.Value}",
        occurredOn = fact.ObservedAtUtc,
        role = Label(fact.Code)
    };

    private static string Severity(string code) => code.Contains("OVERDUE", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase) ? "high" :
        code.Contains("MISSING", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("WEAK", StringComparison.OrdinalIgnoreCase) ||
        code.Contains("SLOW", StringComparison.OrdinalIgnoreCase) ? "medium" : "low";

    private static string? Unit(string code) => code.Contains("RESPONSE", StringComparison.OrdinalIgnoreCase)
        ? "hours" : code.Contains("COVERAGE", StringComparison.OrdinalIgnoreCase) ? "percent" : null;
    private static string Label(string value) => string.Join(' ', value.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    private static string MetricStatus(decimal? value) => value.HasValue ? "available" : "insufficient-evidence";
    private static string? MetricReason(decimal? value) => value.HasValue ? null : "The denominator is zero for this cohort.";

    private static (DateTime FromUtc, DateTime ToUtc, DateTime AsOfUtc) Period(DateTime from, DateTime to)
    {
        var now = DateTime.UtcNow;
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var requestedToUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        if (requestedToUtc.TimeOfDay == TimeSpan.Zero) requestedToUtc = requestedToUtc.AddDays(1);
        var toUtc = requestedToUtc > now ? now : requestedToUtc;
        return (fromUtc, toUtc, now);
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenantId) && tenantId > 0
        ? tenantId : throw new UnauthorizedAccessException("A valid authenticated tenant claim is required.");
    private long UserId() => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        User.FindFirst("sub")?.Value, out var userId) && userId > 0
        ? userId : throw new UnauthorizedAccessException("A valid authenticated user claim is required.");
    private long RoleId() => long.TryParse(User.FindFirst("roleId")?.Value, out var roleId) && roleId > 0
        ? roleId : throw new UnauthorizedAccessException("A valid authenticated role claim is required.");
    private Task<bool> IsManagerAsync(long tenantId) => roleGate.IsManagerOrAdminAsync(RoleId(), tenantId);
    private string IdempotencyKey()
    {
        var value = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))
            throw new ArgumentException("Idempotency-Key is required and must not exceed 160 printable characters.");
        return value;
    }
    private string CorrelationId() => First(Request.Headers["X-Correlation-ID"].ToString(),
        HttpContext.TraceIdentifier, Guid.NewGuid().ToString("N"));
    private static string First(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!;
    private Task<string?> UserName(long tenantId, long userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().Where(user => user.Buid == tenantId && user.Id == userId)
            .Select(user => (user.FirstName + " " + user.LastName).Trim()).SingleOrDefaultAsync(cancellationToken);
    private static ProblemDetails Problem(string title, string detail, int status) => new()
        { Title = title, Detail = detail, Status = status };
}

public sealed record CoachingAcknowledgementRequest(string Disposition, string Reason, DateTime From,
    DateTime To, long? SalesRepUserId = null);
