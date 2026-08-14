namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Operator-facing sentences for the states a message can be held in, as named values rather
/// than string literals buried at their call sites.
///
/// <para>They live here so they can be asserted directly. The alternative — reading the worker's
/// source and matching substrings — is not a behavioural test: it passes when the text is right
/// and the behaviour is wrong, and fails when someone renames a variable. Naming the value makes
/// the promise itself testable.</para>
/// </summary>
public static class EmailInquiryHoldReasons
{
    /// <summary>Extraction succeeded but there is nowhere durable to put the result yet.</summary>
    public const string AssemblyResultStorePending = "assembly_result_store_pending";

    /// <summary>
    /// What an operator is told while a component is held for the missing assembly step.
    ///
    /// <para>Deliberately does NOT promise an automatic retry. Nothing sweeps held components —
    /// there is no recovery service in this build — and telling someone their message will be
    /// reprocessed on its own would be the same class of untruth as advising a retry that cannot
    /// succeed. It says what is true: held, nothing lost.</para>
    /// </summary>
    public const string AssemblyResultStorePendingDetail =
        "This part of the message was read successfully, but the step that combines it with the "
        + "rest of the email is not available yet, so the inquiry is not complete. It is being "
        + "held; the original captured email evidence is preserved.";

    /// <summary>An Email job the worker cannot map to exactly one owning component.</summary>
    /// <summary>A stored result carries a payload contract version this build cannot read.</summary>
    public const string ResultContractUnsupported = "result_contract_unsupported";

    /// <summary>A stored result is present but could not be deserialized.</summary>
    public const string ResultUnreadable = "result_unreadable";

    /// <summary>A component reported Completed but has no durable result row.</summary>
    public const string ResultMissing = "result_missing";

    public const string OwnershipUnresolved = "assembly_ownership_unresolved";
}
