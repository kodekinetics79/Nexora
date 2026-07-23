using ERP_RFQ_Automation.BankReconciliation.Parsing;

namespace ERP_RFQ_Automation.BankReconciliation.Services;

public interface IBankReconciliationService
{
    Task<BankMatchingRuleDto> CreateMatchingRuleAsync(long businessUnitId, string idempotencyKey,
        CreateBankMatchingRuleRequest request, string actor, CancellationToken ct = default);
    Task<BankMatchingRuleDto> TransitionMatchingRuleAsync(long businessUnitId, long ruleId, string action,
        BankMatchingRuleActionRequest request, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<BankMatchingRuleDto>> GetMatchingRulesAsync(long businessUnitId, CancellationToken ct = default);
    Task<BankAccountDto> CreateBankAccountAsync(long businessUnitId, string idempotencyKey,
        CreateBankAccountRequest request, string actor, CancellationToken cancellationToken = default);
    Task<BankAccountDto> GetBankAccountAsync(long businessUnitId, long bankAccountId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BankAccountDto>> GetBankAccountsAsync(long businessUnitId, bool includeClosed,
        CancellationToken cancellationToken = default);
    Task<BankAccountDto> TransitionBankAccountAsync(long businessUnitId, long bankAccountId, string action,
        BankAccountActionRequest request, string actor, CancellationToken cancellationToken = default);

    Task<BankStatementDto> ImportStatementAsync(long businessUnitId, string idempotencyKey,
        ImportBankStatementRequest request, string actor, CancellationToken cancellationToken = default);
    Task<BankStatementDto> ImportStatementAsync(long businessUnitId, string idempotencyKey, long bankAccountId,
        string sourceType, string originalFileName, string rawObjectReference, string sourceHash,
        string parserVersion, byte[] rawPayload, ParsedBankStatement statement, string actor,
        CancellationToken cancellationToken = default);
    Task<BankStatementDto> GetStatementAsync(long businessUnitId, long statementId,
        CancellationToken cancellationToken = default);
    Task<BankStatementSourceDto> GetStatementSourceAsync(long businessUnitId, long statementId,
        CancellationToken cancellationToken = default);

    Task<ReconciliationRunDto> CreateRunAsync(long businessUnitId, string idempotencyKey,
        CreateReconciliationRunRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationRunDto> GetRunAsync(long businessUnitId, long runId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReconciliationMatchDto>> GenerateExactCandidatesAsync(long businessUnitId, long runId,
        string idempotencyKey, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationMatchDto> CreateMatchAsync(long businessUnitId, string idempotencyKey,
        CreateReconciliationMatchRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationMatchDto> ConfirmMatchAsync(long businessUnitId, long matchId,
        MatchActionRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationMatchDto> VoidMatchAsync(long businessUnitId, long matchId,
        MatchActionRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationRunDto> SubmitRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationRunDto> ApproveRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, CancellationToken cancellationToken = default);
    Task<ReconciliationRunDto> ReopenRunAsync(long businessUnitId, long runId,
        ReconciliationActionRequest request, string actor, CancellationToken cancellationToken = default);
}
