using System.Globalization;
using System.Text;

namespace ERP_RFQ_Automation.BankReconciliation.Parsing;

public sealed class StrictCsvBankStatementParser : IBankStatementParser
{
    private static readonly string[] ExpectedHeader =
    [
        "StatementReference", "AccountIdentifier", "Currency", "PeriodStart", "PeriodEnd",
        "OpeningBalance", "ClosingBalance", "Ordinal", "BookingDate", "ValueDate", "Amount",
        "Direction", "ExternalTransactionId", "BankReference", "TransactionCode", "Counterparty",
        "RemittanceText"
    ];

    public ParsedBankStatement Parse(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The CSV input stream must be readable.", nameof(input));
        }

        var records = ReadRecords(input);
        if (records.Count < 2)
        {
            throw new BankStatementParseException("CSV must contain the exact header and at least one data row.");
        }

        if (!records[0].SequenceEqual(ExpectedHeader, StringComparer.Ordinal))
        {
            throw new BankStatementParseException(
                $"CSV header must exactly equal: {string.Join(',', ExpectedHeader)}.");
        }

        var first = records[1];
        ValidateColumnCount(first, 2);
        var statementReference = BankStatementCanonicalizer.Required(first[0], "statement reference");
        var accountIdentifier = BankStatementCanonicalizer.Required(first[1], "account identifier");
        var currency = BankStatementCanonicalizer.Currency(first[2]);
        var periodStart = BankStatementCanonicalizer.Date(first[3], "period start");
        var periodEnd = BankStatementCanonicalizer.Date(first[4], "period end");
        var openingBalance = BankStatementCanonicalizer.Amount(first[5], "opening balance");
        var closingBalance = BankStatementCanonicalizer.Amount(first[6], "closing balance");
        var lines = new List<ParsedBankStatementLine>(records.Count - 1);

        for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            var row = records[rowIndex];
            ValidateColumnCount(row, rowNumber);
            RequireSame(row[0], statementReference, "statement reference", rowNumber);
            RequireSame(row[1], accountIdentifier, "account identifier", rowNumber);
            RequireSame(BankStatementCanonicalizer.Currency(row[2]), currency, "currency", rowNumber);
            RequireSame(row[3], first[3], "period start", rowNumber);
            RequireSame(row[4], first[4], "period end", rowNumber);
            RequireSame(row[5], first[5], "opening balance", rowNumber);
            RequireSame(row[6], first[6], "closing balance", rowNumber);

            if (!int.TryParse(row[7], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                throw new BankStatementParseException($"CSV row {rowNumber} ordinal must be a positive integer.");
            }

            var direction = row[11] switch
            {
                "CREDIT" => BankTransactionDirection.Credit,
                "DEBIT" => BankTransactionDirection.Debit,
                _ => throw new BankStatementParseException(
                    $"CSV row {rowNumber} direction must be CREDIT or DEBIT.")
            };
            var amount = BankStatementCanonicalizer.Amount(row[10], $"CSV row {rowNumber} amount", true);
            lines.Add(BankStatementCanonicalizer.CreateLine(
                ordinal,
                BankStatementCanonicalizer.Date(row[8], $"CSV row {rowNumber} booking date"),
                BankStatementCanonicalizer.Date(row[9], $"CSV row {rowNumber} value date"),
                amount,
                row[10],
                currency,
                direction,
                row[12],
                row[13],
                row[14],
                row[15],
                row[16],
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

    public ParsedBankStatement Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv), writable: false);
        return Parse(stream);
    }

    private static List<string[]> ReadRecords(Stream input)
    {
        using var reader = new StreamReader(
            input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quoteClosed = false;
        var characterCount = 0;

        while (true)
        {
            var raw = reader.Read();
            if (raw < 0)
            {
                break;
            }

            characterCount++;
            if (characterCount > BankStatementCanonicalizer.MaximumDocumentCharacters)
            {
                throw new BankStatementParseException(
                    $"CSV exceeds the {BankStatementCanonicalizer.MaximumDocumentCharacters}-character limit.");
            }

            var current = (char)raw;
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        characterCount++;
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                        quoteClosed = true;
                    }
                }
                else
                {
                    field.Append(current);
                }

                EnsureFieldLimit(field);
                continue;
            }

            if (quoteClosed && current is not (',' or '\r' or '\n'))
            {
                throw new BankStatementParseException("A closing CSV quote must be followed by a delimiter or line ending.");
            }

            switch (current)
            {
                case '"' when field.Length == 0 && !quoteClosed:
                    inQuotes = true;
                    break;
                case '"':
                    throw new BankStatementParseException("CSV quotes are only permitted around complete fields.");
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    quoteClosed = false;
                    break;
                case '\r':
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                        characterCount++;
                    }

                    CompleteRecord(records, record, field);
                    quoteClosed = false;
                    break;
                case '\n':
                    CompleteRecord(records, record, field);
                    quoteClosed = false;
                    break;
                default:
                    field.Append(current);
                    EnsureFieldLimit(field);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new BankStatementParseException("CSV contains an unterminated quoted field.");
        }

        if (field.Length > 0 || record.Count > 0 || quoteClosed)
        {
            CompleteRecord(records, record, field);
        }

        return records;
    }

    private static void CompleteRecord(List<string[]> records, List<string> record, StringBuilder field)
    {
        record.Add(field.ToString());
        field.Clear();
        if (record.Count != 1 || record[0].Length != 0)
        {
            records.Add(record.ToArray());
            if (records.Count > BankStatementCanonicalizer.MaximumLines + 1)
            {
                throw new BankStatementParseException(
                    $"CSV cannot exceed {BankStatementCanonicalizer.MaximumLines} data rows.");
            }
        }

        record.Clear();
    }

    private static void EnsureFieldLimit(StringBuilder field)
    {
        if (field.Length > BankStatementCanonicalizer.MaximumFieldCharacters)
        {
            throw new BankStatementParseException(
                $"A CSV field exceeds the {BankStatementCanonicalizer.MaximumFieldCharacters}-character limit.");
        }
    }

    private static void ValidateColumnCount(string[] row, int rowNumber)
    {
        if (row.Length != ExpectedHeader.Length)
        {
            throw new BankStatementParseException(
                $"CSV row {rowNumber} has {row.Length} columns; expected {ExpectedHeader.Length}.");
        }
    }

    private static void RequireSame(string actual, string expected, string fieldName, int rowNumber)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new BankStatementParseException(
                $"CSV row {rowNumber} {fieldName} differs from the first data row.");
        }
    }
}
