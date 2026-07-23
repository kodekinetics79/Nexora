using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ERP_RFQ_Automation.BankReconciliation.Parsing;

public sealed class Camt053BankStatementParser : IBankStatementParser
{
    public ParsedBankStatement Parse(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The CAMT.053 input stream must be readable.", nameof(input));
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = BankStatementCanonicalizer.MaximumDocumentCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            };
            using var reader = XmlReader.Create(input, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new BankStatementParseException("CAMT.053 XML is malformed or violates safe XML limits.", exception);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Document" ||
            !root.Name.NamespaceName.Contains("camt.053", StringComparison.Ordinal))
        {
            throw new BankStatementParseException("XML root must be an ISO 20022 CAMT.053 Document.");
        }

        var ns = root.Name.Namespace;
        var container = RequiredElement(root, ns + "BkToCstmrStmt", "BkToCstmrStmt");
        var statements = container.Elements(ns + "Stmt").ToArray();
        if (statements.Length != 1)
        {
            throw new BankStatementParseException("CAMT.053 payload must contain exactly one statement.");
        }

        var statement = statements[0];
        var statementReference = RequiredValue(statement, ns + "Id", "statement ID");
        var account = RequiredElement(statement, ns + "Acct", "account");
        var accountId = RequiredElement(account, ns + "Id", "account ID");
        var accountIdentifier = Value(accountId.Element(ns + "IBAN")) ??
            Value(accountId.Element(ns + "Othr")?.Element(ns + "Id")) ??
            throw new BankStatementParseException("CAMT.053 account must contain IBAN or Othr/Id.");
        var currency = BankStatementCanonicalizer.Currency(RequiredValue(account, ns + "Ccy", "account currency"));
        var period = RequiredElement(statement, ns + "FrToDt", "statement period");
        var periodStart = ReadIsoDate(period, ns, "FrDt", "FrDtTm", "statement period start");
        var periodEnd = ReadIsoDate(period, ns, "ToDt", "ToDtTm", "statement period end");
        var openingBalance = ReadBalance(statement, ns, currency, "OPBD", "PRCD");
        var closingBalance = ReadBalance(statement, ns, currency, "CLBD");

        var entries = statement.Elements(ns + "Ntry").ToArray();
        if (entries.Length > BankStatementCanonicalizer.MaximumLines)
        {
            throw new BankStatementParseException(
                $"CAMT.053 cannot exceed {BankStatementCanonicalizer.MaximumLines} entries.");
        }

        var lines = new List<ParsedBankStatementLine>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var ordinal = index + 1;
            var amountElement = RequiredElement(entry, ns + "Amt", $"entry {ordinal} amount");
            var entryCurrency = BankStatementCanonicalizer.Currency(
                BankStatementCanonicalizer.Required(amountElement.Attribute("Ccy")?.Value,
                    $"entry {ordinal} amount currency"));
            var amountText = BankStatementCanonicalizer.Required(amountElement.Value, $"entry {ordinal} amount");
            var amount = BankStatementCanonicalizer.Amount(amountText, $"entry {ordinal} amount", true);
            var direction = RequiredValue(entry, ns + "CdtDbtInd", $"entry {ordinal} credit/debit indicator") switch
            {
                "CRDT" => BankTransactionDirection.Credit,
                "DBIT" => BankTransactionDirection.Debit,
                _ => throw new BankStatementParseException(
                    $"CAMT.053 entry {ordinal} CdtDbtInd must be CRDT or DBIT.")
            };
            var transactionDetails = entry.Element(ns + "NtryDtls")?.Elements(ns + "TxDtls").ToArray()
                ?? [];
            if (transactionDetails.Length > 1)
            {
                throw new BankStatementParseException(
                    $"CAMT.053 entry {ordinal} contains multiple TxDtls records; aggregated entries are not supported.");
            }
            var details = transactionDetails.SingleOrDefault();
            var references = details?.Element(ns + "Refs");
            var externalTransactionId = FirstValue(
                references?.Element(ns + "TxId"),
                references?.Element(ns + "EndToEndId"),
                references?.Element(ns + "InstrId"),
                entry.Element(ns + "NtryRef"));
            var bankReference = FirstValue(
                references?.Element(ns + "AcctSvcrRef"),
                entry.Element(ns + "AcctSvcrRef"),
                entry.Element(ns + "NtryRef"));
            var transactionCode = ReadTransactionCode(entry.Element(ns + "BkTxCd"), ns);
            var counterparty = ReadCounterparty(details, direction, ns);
            var remittanceText = details?.Element(ns + "RmtInf")?.Elements(ns + "Ustrd")
                .Select(Value)
                .Where(value => value is not null)
                .AggregateOrDefault();

            lines.Add(BankStatementCanonicalizer.CreateLine(
                ordinal,
                ReadIsoDate(RequiredElement(entry, ns + "BookgDt", $"entry {ordinal} booking date"),
                    ns, "Dt", "DtTm", $"entry {ordinal} booking date"),
                ReadIsoDate(RequiredElement(entry, ns + "ValDt", $"entry {ordinal} value date"),
                    ns, "Dt", "DtTm", $"entry {ordinal} value date"),
                amount,
                amountText,
                entryCurrency,
                direction,
                externalTransactionId,
                bankReference,
                transactionCode,
                counterparty,
                remittanceText,
                accountIdentifier));
        }

        return BankStatementCanonicalizer.CreateStatement(
            statementReference,
            accountIdentifier,
            currency,
            periodStart,
            periodEnd,
            openingBalance,
            closingBalance,
            lines);
    }

    public ParsedBankStatement Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml), writable: false);
        return Parse(stream);
    }

    private static decimal ReadBalance(
        XElement statement,
        XNamespace ns,
        string statementCurrency,
        params string[] acceptedCodes)
    {
        var balances = statement.Elements(ns + "Bal")
            .Where(balance => acceptedCodes.Contains(
                Value(balance.Element(ns + "Tp")?.Element(ns + "CdOrPrtry")?.Element(ns + "Cd")),
                StringComparer.Ordinal))
            .ToArray();
        if (balances.Length != 1)
        {
            throw new BankStatementParseException(
                $"CAMT.053 must contain exactly one {string.Join(" or ", acceptedCodes)} balance.");
        }

        var amountElement = RequiredElement(balances[0], ns + "Amt", $"{acceptedCodes[0]} balance amount");
        var balanceCurrency = BankStatementCanonicalizer.Currency(
            BankStatementCanonicalizer.Required(amountElement.Attribute("Ccy")?.Value,
                $"{acceptedCodes[0]} balance currency"));
        if (!string.Equals(balanceCurrency, statementCurrency, StringComparison.Ordinal))
        {
            throw new BankStatementParseException(
                $"CAMT.053 {acceptedCodes[0]} balance currency differs from the account currency.");
        }

        var amount = BankStatementCanonicalizer.Amount(amountElement.Value, $"{acceptedCodes[0]} balance amount");
        if (amount < 0m)
        {
            throw new BankStatementParseException(
                $"CAMT.053 {acceptedCodes[0]} balance amount must be unsigned.");
        }
        return RequiredValue(balances[0], ns + "CdtDbtInd", $"{acceptedCodes[0]} balance direction") switch
        {
            "CRDT" => amount,
            "DBIT" => -amount,
            _ => throw new BankStatementParseException(
                $"CAMT.053 {acceptedCodes[0]} balance CdtDbtInd must be CRDT or DBIT.")
        };
    }

    private static DateOnly ReadIsoDate(
        XElement parent,
        XNamespace ns,
        string dateName,
        string dateTimeName,
        string fieldName)
    {
        var date = Value(parent.Element(ns + dateName));
        if (date is not null)
        {
            return BankStatementCanonicalizer.Date(date, fieldName);
        }

        var dateTime = Value(parent.Element(ns + dateTimeName));
        if (dateTime is null)
        {
            throw new BankStatementParseException($"Missing required {fieldName}.");
        }

        try
        {
            var parsed = XmlConvert.ToDateTimeOffset(dateTime);
            return DateOnly.FromDateTime(parsed.Date);
        }
        catch (FormatException exception)
        {
            throw new BankStatementParseException($"{fieldName} must be an ISO 8601 date or timestamp.", exception);
        }
    }

    private static string? ReadTransactionCode(XElement? code, XNamespace ns)
    {
        if (code is null)
        {
            return null;
        }

        var proprietary = Value(code.Element(ns + "Prtry")?.Element(ns + "Cd"));
        if (proprietary is not null)
        {
            return proprietary;
        }

        var domain = code.Element(ns + "Domn");
        var values = new[]
        {
            Value(domain?.Element(ns + "Cd")),
            Value(domain?.Element(ns + "Fmly")?.Element(ns + "Cd")),
            Value(domain?.Element(ns + "Fmly")?.Element(ns + "SubFmlyCd"))
        }.Where(value => value is not null).ToArray();
        return values.Length == 0 ? null : string.Join('-', values!);
    }

    private static string? ReadCounterparty(
        XElement? details,
        BankTransactionDirection direction,
        XNamespace ns)
    {
        var parties = details?.Element(ns + "RltdPties");
        var preferred = direction == BankTransactionDirection.Credit ? "Dbtr" : "Cdtr";
        var fallback = direction == BankTransactionDirection.Credit ? "Cdtr" : "Dbtr";
        return FirstValue(
            parties?.Element(ns + preferred)?.Element(ns + "Nm"),
            parties?.Element(ns + fallback)?.Element(ns + "Nm"));
    }

    private static XElement RequiredElement(XElement parent, XName name, string fieldName) =>
        parent.Element(name) ?? throw new BankStatementParseException($"Missing required CAMT.053 {fieldName}.");

    private static string RequiredValue(XElement parent, XName name, string fieldName) =>
        BankStatementCanonicalizer.Required(parent.Element(name)?.Value, fieldName);

    private static string? FirstValue(params XElement?[] elements) =>
        elements.Select(Value).FirstOrDefault(value => value is not null);

    private static string? Value(XElement? element) =>
        element is null ? null : BankStatementCanonicalizer.Optional(element.Value, element.Name.LocalName);
}

internal static class StringSequenceExtensions
{
    internal static string? AggregateOrDefault(this IEnumerable<string?> values)
    {
        var materialized = values.Where(value => value is not null).Cast<string>().ToArray();
        return materialized.Length == 0 ? null : string.Join(' ', materialized);
    }
}
