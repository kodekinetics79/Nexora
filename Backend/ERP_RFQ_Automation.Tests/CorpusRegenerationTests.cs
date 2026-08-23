using ERP_RFQ_Automation.Tests.Support;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The corpus regeneration ritual — see <c>Corpus/README.md</c>. This is NOT part of the
/// normal run: it does nothing unless <c>NEXORA_REGENERATE_CORPUS=1</c>, in which case it
/// rewrites every file under <c>NEXORA_CORPUS_OUT</c> from <see cref="CorpusGenerator"/>.
/// The committed bytes in <c>Corpus/</c> are the corpus; tests read those, never these
/// builders.
///
/// It also proves, at generation time, the two properties the corpus depends on:
/// the encrypted PDF genuinely refuses to open without its password (the exact check
/// <c>ProductionDocumentReader</c> relies on), and it opens fine WITH it.
/// </summary>
public sealed class CorpusRegenerationTests
{
    [Fact]
    public void Regenerate_writes_the_corpus_when_explicitly_asked()
    {
        if (Environment.GetEnvironmentVariable("NEXORA_REGENERATE_CORPUS") != "1")
            return; // the committed corpus is authoritative; nothing to do on a normal run

        var output = Environment.GetEnvironmentVariable("NEXORA_CORPUS_OUT");
        Assert.False(string.IsNullOrWhiteSpace(output),
            "Set NEXORA_CORPUS_OUT to the source Corpus/ directory to regenerate.");
        Directory.CreateDirectory(output!);

        long total = 0;
        foreach (var (name, bytes) in CorpusGenerator.BuildAll())
        {
            File.WriteAllBytes(Path.Combine(output!, name), bytes);
            total += bytes.LongLength;
        }

        // The corpus must stay modest — it is committed to the repository.
        Assert.True(total < 10L * 1024 * 1024, $"Corpus grew to {total} bytes; keep it under 10 MB.");
    }

    [Fact]
    public void The_encrypted_corpus_pdf_is_genuinely_password_protected()
    {
        // Asserted against the GENERATOR output (not the committed file) so the property is
        // re-proven on every regeneration; CorpusAcceptanceTests proves the committed file
        // takes the reader's password-disposition path.
        var bytes = CorpusGenerator.EncryptedPdf();

        Assert.ThrowsAny<PdfDocumentEncryptedException>(() =>
        {
            using var _ = PdfDocument.Open(bytes);
        });

        using var opened = PdfDocument.Open(bytes,
            new ParsingOptions { Password = "nexora" });
        Assert.Equal(1, opened.NumberOfPages);
    }
}
