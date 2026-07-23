using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.BankReconciliation.Parsing;

public enum BankTransactionDirection
{
    Credit,
    Debit
}

public sealed record ParsedBankStatementLine(
    int Ordinal,
    DateOnly BookingDate,
    DateOnly ValueDate,
    decimal SignedAmount,
    string Currency,
    BankTransactionDirection Direction,
    string OriginalAmountText,
    string? ExternalTransactionId,
    string? BankReference,
    string? TransactionCode,
    string? Counterparty,
    string? RemittanceText,
    string Fingerprint);

public sealed record ParsedBankStatement(
    string StatementReference,
    string AccountIdentifier,
    string Currency,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal OpeningBalance,
    decimal ClosingBalance,
    IReadOnlyList<ParsedBankStatementLine> Lines);

public interface IBankStatementParser
{
    ParsedBankStatement Parse(Stream input);
}

public sealed class BankStatementParseException : FormatException
{
    public BankStatementParseException(string message)
        : base(message)
    {
    }

    public BankStatementParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static partial class BankStatementCanonicalizer
{
    internal const int MaximumDocumentCharacters = 10 * 1024 * 1024;
    internal const int MaximumLines = 100_000;
    internal const int MaximumFieldCharacters = 10_000;

    internal static ParsedBankStatement CreateStatement(
        string statementReference,
        string accountIdentifier,
        string currency,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal openingBalance,
        decimal closingBalance,
        IReadOnlyList<ParsedBankStatementLine> lines)
    {
        statementReference = Required(statementReference, "statement reference");
        accountIdentifier = Required(accountIdentifier, "account identifier");
        currency = Currency(currency);

        if (periodEnd < periodStart)
        {
            throw new BankStatementParseException("Statement period end cannot precede its start.");
        }

        if (lines.Count == 0)
        {
            throw new BankStatementParseException("A statement must contain at least one line.");
        }

        if (lines.Count > MaximumLines)
        {
            throw new BankStatementParseException($"A statement cannot exceed {MaximumLines} lines.");
        }

        var ordinals = new HashSet<int>();
        foreach (var line in lines)
        {
            if (line.Ordinal <= 0)
            {
                throw new BankStatementParseException("Statement line ordinals must be positive integers.");
            }

            if (!ordinals.Add(line.Ordinal))
            {
                throw new BankStatementParseException($"Duplicate statement line ordinal {line.Ordinal}.");
            }

            if (!string.Equals(line.Currency, currency, StringComparison.Ordinal))
            {
                throw new BankStatementParseException(
                    $"Line {line.Ordinal} currency '{line.Currency}' differs from statement currency '{currency}'.");
            }

            if (line.BookingDate < periodStart || line.BookingDate > periodEnd)
            {
                throw new BankStatementParseException(
                    $"Line {line.Ordinal} booking date is outside the statement period.");
            }
        }

        var calculatedClosingBalance = openingBalance + lines.Sum(line => line.SignedAmount);
        if (calculatedClosingBalance != closingBalance)
        {
            throw new BankStatementParseException(
                $"Closing balance does not reconcile: expected {FormatAmount(calculatedClosingBalance)}, " +
                $"received {FormatAmount(closingBalance)}.");
        }

        return new ParsedBankStatement(
            statementReference,
            accountIdentifier,
            currency,
            periodStart,
            periodEnd,
            openingBalance,
            closingBalance,
            Array.AsReadOnly(lines.ToArray()));
    }

    internal static ParsedBankStatementLine CreateLine(
        int ordinal,
        DateOnly bookingDate,
        DateOnly valueDate,
        decimal unsignedAmount,
        string originalAmountText,
        string currency,
        BankTransactionDirection direction,
        string? externalTransactionId,
        string? bankReference,
        string? transactionCode,
        string? counterparty,
        string? remittanceText,
        string accountIdentifier)
    {
        if (unsignedAmount <= 0m)
        {
            throw new BankStatementParseException($"Line {ordinal} amount must be greater than zero.");
        }

        currency = Currency(currency);
        accountIdentifier = Required(accountIdentifier, "account identifier");
        originalAmountText = Required(originalAmountText, $"line {ordinal} original amount");
        externalTransactionId = Optional(externalTransactionId, $"line {ordinal} external transaction ID");
        bankReference = Optional(bankReference, $"line {ordinal} bank reference");
        transactionCode = Optional(transactionCode, $"line {ordinal} transaction code");
        counterparty = Optional(counterparty, $"line {ordinal} counterparty");
        remittanceText = Optional(remittanceText, $"line {ordinal} remittance text");

        var signedAmount = direction == BankTransactionDirection.Credit
            ? unsignedAmount
            : -unsignedAmount;
        var fingerprint = Fingerprint(
            accountIdentifier,
            bookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FormatAmount(signedAmount),
            currency,
            externalTransactionId,
            bankReference,
            transactionCode,
            counterparty,
            remittanceText);

        return new ParsedBankStatementLine(
            ordinal,
            bookingDate,
            valueDate,
            signedAmount,
            currency,
            direction,
            originalAmountText,
            externalTransactionId,
            bankReference,
            transactionCode,
            counterparty,
            remittanceText,
            fingerprint);
    }

    internal static decimal Amount(string value, string fieldName, bool mustBeUnsigned = false)
    {
        value = Required(value, fieldName);
        if (!CanonicalAmountPattern().IsMatch(value) ||
            !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
        {
            throw new BankStatementParseException(
                $"{fieldName} must be an invariant decimal without grouping separators or exponents.");
        }

        if (mustBeUnsigned && amount <= 0m)
        {
            throw new BankStatementParseException($"{fieldName} must be greater than zero and unsigned.");
        }

        return amount;
    }

    internal static DateOnly Date(string value, string fieldName)
    {
        value = Required(value, fieldName);
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            throw new BankStatementParseException($"{fieldName} must use yyyy-MM-dd.");
        }

        return date;
    }

    internal static string Currency(string value)
    {
        value = Required(value, "currency").ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(value))
        {
            throw new BankStatementParseException("Currency must be a three-letter ISO-style code.");
        }

        return value;
    }

    internal static string Required(string? value, string fieldName)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            throw new BankStatementParseException($"Missing required {fieldName}.");
        }

        return normalized;
    }

    internal static string? Optional(string? value, string fieldName)
    {
        var normalized = Normalize(value);
        if (normalized is not null && normalized.Length > MaximumFieldCharacters)
        {
            throw new BankStatementParseException(
                $"{fieldName} exceeds the {MaximumFieldCharacters}-character limit.");
        }

        return normalized;
    }

    internal static string FormatAmount(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        if (normalized.Length > MaximumFieldCharacters)
        {
            throw new BankStatementParseException(
                $"A field exceeds the {MaximumFieldCharacters}-character limit.");
        }

        return normalized;
    }

    private static string Fingerprint(params string?[] values)
    {
        var canonical = new StringBuilder();
        foreach (var value in values)
        {
            var normalized = value ?? string.Empty;
            canonical.Append(normalized.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':');
            canonical.Append(normalized);
            canonical.Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[+-]?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalAmountPattern();
}
