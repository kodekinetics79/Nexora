using System.Globalization;
using ERP_RFQ_Automation.BankReconciliation.Parsing;

namespace ERP_RFQ_Automation.Tests;

public sealed class BankStatementParserTests
{
    [Fact]
    public void CsvAndCamt053_ProduceEquivalentCanonicalOutput()
    {
        using var _ = new CultureScope("de-DE");

        var csv = new StrictCsvBankStatementParser().Parse(EquivalentCsv);
        var camt = new Camt053BankStatementParser().Parse(EquivalentCamt);

        AssertEquivalent(csv, camt);
        Assert.Collection(csv.Lines,
            credit =>
            {
                Assert.Equal(BankTransactionDirection.Credit, credit.Direction);
                Assert.Equal(250.25m, credit.SignedAmount);
                Assert.Equal(64, credit.Fingerprint.Length);
            },
            debit =>
            {
                Assert.Equal(BankTransactionDirection.Debit, debit.Direction);
                Assert.Equal(-50.25m, debit.SignedAmount);
                Assert.Equal(64, debit.Fingerprint.Length);
            });
    }

    [Fact]
    public void Csv_RejectsDuplicateOrdinals()
    {
        var duplicate = EquivalentCsv.Replace(
            "STMT-2026-07,DE02120300000000202051,EUR,2026-07-01,2026-07-31,1000.00,1200.00,2,",
            "STMT-2026-07,DE02120300000000202051,EUR,2026-07-01,2026-07-31,1000.00,1200.00,1,",
            StringComparison.Ordinal);

        var exception = Assert.Throws<BankStatementParseException>(
            () => new StrictCsvBankStatementParser().Parse(duplicate));

        Assert.Contains("Duplicate statement line ordinal 1", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-07-03,250.25,CREDIT", "2026-07-03,250,25,CREDIT")]
    [InlineData("2026-07-03,250.25,CREDIT", "2026-07-03,2.5e2,CREDIT")]
    [InlineData("2026-07-03,2026-07-03", "03/07/2026,2026-07-03")]
    [InlineData("250.25,CREDIT,TX-001", "250.25,CRDT,TX-001")]
    public void Csv_RejectsNonCanonicalAmountsDatesAndDirections(string valid, string invalid)
    {
        var malformed = EquivalentCsv.Replace(valid, invalid, StringComparison.Ordinal);

        Assert.Throws<BankStatementParseException>(
            () => new StrictCsvBankStatementParser().Parse(malformed));
    }

    [Fact]
    public void Csv_RejectsClosingBalanceThatDoesNotReconcile()
    {
        var malformed = EquivalentCsv.Replace(
            ",1000.00,1200.00,", ",1000.00,9999.00,", StringComparison.Ordinal);

        var exception = Assert.Throws<BankStatementParseException>(
            () => new StrictCsvBankStatementParser().Parse(malformed));

        Assert.Contains("Closing balance does not reconcile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_IsStableAcrossStatementAndOrdinalChanges()
    {
        const string header =
            "StatementReference,AccountIdentifier,Currency,PeriodStart,PeriodEnd,OpeningBalance," +
            "ClosingBalance,Ordinal,BookingDate,ValueDate,Amount,Direction,ExternalTransactionId," +
            "BankReference,TransactionCode,Counterparty,RemittanceText\n";
        var first = new StrictCsvBankStatementParser().Parse(header +
            "FIRST,ACCOUNT-1,USD,2026-01-01,2026-01-31,0,10,1,2026-01-02,2026-01-02," +
            "10,CREDIT,TX-10,REF-10,PMNT,Customer,Invoice 10\n");
        var second = new StrictCsvBankStatementParser().Parse(header +
            "SECOND,ACCOUNT-1,USD,2026-01-01,2026-01-31,0.00,10.00,77,2026-01-02,2026-01-02," +
            "10.00,CREDIT,TX-10,REF-10,PMNT,Customer,Invoice 10\n");

        Assert.Equal(first.Lines[0].Fingerprint, second.Lines[0].Fingerprint);
    }

    [Fact]
    public void Camt053_RejectsDtdAndExternalEntityPayload()
    {
        const string unsafeXml = """
            <?xml version="1.0"?>
            <!DOCTYPE Document [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08">
              <BkToCstmrStmt><Stmt><Id>&xxe;</Id></Stmt></BkToCstmrStmt>
            </Document>
            """;

        var exception = Assert.Throws<BankStatementParseException>(
            () => new Camt053BankStatementParser().Parse(unsafeXml));

        Assert.Contains("malformed or violates safe XML limits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Camt053_RejectsWrongNamespaceAndMultipleStatements()
    {
        var wrongNamespace = EquivalentCamt.Replace("camt.053.001.08", "camt.054.001.08", StringComparison.Ordinal);
        var duplicateStatement = EquivalentCamt.Replace(
            "</Stmt>\n  </BkToCstmrStmt>", "</Stmt><Stmt><Id>SECOND</Id></Stmt>\n  </BkToCstmrStmt>",
            StringComparison.Ordinal);

        Assert.Throws<BankStatementParseException>(
            () => new Camt053BankStatementParser().Parse(wrongNamespace));
        var exception = Assert.Throws<BankStatementParseException>(
            () => new Camt053BankStatementParser().Parse(duplicateStatement));
        Assert.Contains("exactly one statement", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Camt053_AcceptsZeroOpeningBalanceAndInvariantTimestampDates()
    {
        var xml = EquivalentCamt
            .Replace("<Amt Ccy=\"EUR\">1000.00</Amt>", "<Amt Ccy=\"EUR\">0</Amt>", StringComparison.Ordinal)
            .Replace("<Amt Ccy=\"EUR\">1200.00</Amt>", "<Amt Ccy=\"EUR\">200.00</Amt>", StringComparison.Ordinal)
            .Replace("<FrDt>2026-07-01</FrDt><ToDt>2026-07-31</ToDt>",
                "<FrDtTm>2026-07-01T00:00:00Z</FrDtTm><ToDtTm>2026-07-31T23:59:59Z</ToDtTm>",
                StringComparison.Ordinal);

        var parsed = new Camt053BankStatementParser().Parse(xml);

        Assert.Equal(0m, parsed.OpeningBalance);
        Assert.Equal(new DateOnly(2026, 7, 1), parsed.PeriodStart);
        Assert.Equal(new DateOnly(2026, 7, 31), parsed.PeriodEnd);
    }

    [Fact]
    public void Camt053_RejectsBalanceCurrencyDifferentFromAccountCurrency()
    {
        var malformed = EquivalentCamt.Replace(
            "<Amt Ccy=\"EUR\">1200.00</Amt>", "<Amt Ccy=\"USD\">1200.00</Amt>",
            StringComparison.Ordinal);

        var exception = Assert.Throws<BankStatementParseException>(
            () => new Camt053BankStatementParser().Parse(malformed));

        Assert.Contains("balance currency differs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Camt053_RejectsAggregatedEntryWithControlledParseError()
    {
        var aggregated = EquivalentCamt.Replace(
            "</TxDtls></NtryDtls>",
            "</TxDtls><TxDtls><Refs><TxId>TX-EXTRA</TxId></Refs></TxDtls></NtryDtls>",
            StringComparison.Ordinal);

        Assert.Throws<BankStatementParseException>(
            () => new Camt053BankStatementParser().Parse(aggregated));
    }

    private static void AssertEquivalent(ParsedBankStatement expected, ParsedBankStatement actual)
    {
        Assert.Equal(expected.StatementReference, actual.StatementReference);
        Assert.Equal(expected.AccountIdentifier, actual.AccountIdentifier);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(expected.PeriodStart, actual.PeriodStart);
        Assert.Equal(expected.PeriodEnd, actual.PeriodEnd);
        Assert.Equal(expected.OpeningBalance, actual.OpeningBalance);
        Assert.Equal(expected.ClosingBalance, actual.ClosingBalance);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        for (var index = 0; index < expected.Lines.Count; index++)
        {
            Assert.Equal(expected.Lines[index], actual.Lines[index]);
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        internal CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }

    private const string EquivalentCsv = """
        StatementReference,AccountIdentifier,Currency,PeriodStart,PeriodEnd,OpeningBalance,ClosingBalance,Ordinal,BookingDate,ValueDate,Amount,Direction,ExternalTransactionId,BankReference,TransactionCode,Counterparty,RemittanceText
        STMT-2026-07,DE02120300000000202051,EUR,2026-07-01,2026-07-31,1000.00,1200.00,1,2026-07-03,2026-07-03,250.25,CREDIT,TX-001,ASR-001,PMNT-RCDT-ESCT,Acme GmbH,"Invoice 42, final"
        STMT-2026-07,DE02120300000000202051,EUR,2026-07-01,2026-07-31,1000.00,1200.00,2,2026-07-05,2026-07-06,50.25,DEBIT,TX-002,ASR-002,PMNT-ICDT-ESCT,Vendor AG,Invoice 84
        """;

    private const string EquivalentCamt = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.08">
          <BkToCstmrStmt>
            <Stmt>
              <Id>STMT-2026-07</Id>
              <FrToDt><FrDt>2026-07-01</FrDt><ToDt>2026-07-31</ToDt></FrToDt>
              <Acct><Id><IBAN>DE02120300000000202051</IBAN></Id><Ccy>EUR</Ccy></Acct>
              <Bal><Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp><Amt Ccy="EUR">1000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd></Bal>
              <Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy="EUR">1200.00</Amt><CdtDbtInd>CRDT</CdtDbtInd></Bal>
              <Ntry>
                <NtryRef>ENTRY-001</NtryRef><Amt Ccy="EUR">250.25</Amt><CdtDbtInd>CRDT</CdtDbtInd>
                <BookgDt><Dt>2026-07-03</Dt></BookgDt><ValDt><Dt>2026-07-03</Dt></ValDt>
                <BkTxCd><Domn><Cd>PMNT</Cd><Fmly><Cd>RCDT</Cd><SubFmlyCd>ESCT</SubFmlyCd></Fmly></Domn></BkTxCd>
                <NtryDtls><TxDtls><Refs><AcctSvcrRef>ASR-001</AcctSvcrRef><TxId>TX-001</TxId></Refs><RltdPties><Dbtr><Nm>Acme GmbH</Nm></Dbtr></RltdPties><RmtInf><Ustrd>Invoice 42, final</Ustrd></RmtInf></TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <NtryRef>ENTRY-002</NtryRef><Amt Ccy="EUR">50.25</Amt><CdtDbtInd>DBIT</CdtDbtInd>
                <BookgDt><Dt>2026-07-05</Dt></BookgDt><ValDt><Dt>2026-07-06</Dt></ValDt>
                <BkTxCd><Domn><Cd>PMNT</Cd><Fmly><Cd>ICDT</Cd><SubFmlyCd>ESCT</SubFmlyCd></Fmly></Domn></BkTxCd>
                <NtryDtls><TxDtls><Refs><AcctSvcrRef>ASR-002</AcctSvcrRef><TxId>TX-002</TxId></Refs><RltdPties><Cdtr><Nm>Vendor AG</Nm></Cdtr></RltdPties><RmtInf><Ustrd>Invoice 84</Ustrd></RmtInf></TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;
}
