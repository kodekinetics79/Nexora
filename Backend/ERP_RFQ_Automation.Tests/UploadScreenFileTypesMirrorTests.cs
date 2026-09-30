using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Security.DocumentInspection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// CP-05: the lead upload screen refused .msg, .eml and .html ("Selection stopped: 1 unsupported
/// format") although the server's intake reads all of them — Aramco and Marafiq forward their RFQs
/// as .msg. The screen's list must be exactly the server's allow-list, in both directions: a type
/// the screen offers and the server refuses is a failed upload; a type the server reads and the
/// screen refuses is a lost RFQ.
/// </summary>
public sealed class UploadScreenFileTypesMirrorTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void The_upload_screen_accepts_exactly_what_intake_accepts()
    {
        var path = Path.Combine(RepositoryRoot(), "Frontend", "src", "pages", "Leads", "uploadFileTypes.ts");
        Assert.True(File.Exists(path), $"uploadFileTypes.ts is missing at {path}.");
        var source = File.ReadAllText(path);

        var list = Regex.Match(source, @"SUPPORTED_EXTENSIONS\s*=\s*\[(?<items>[^\]]*)\]", RegexOptions.Singleline);
        Assert.True(list.Success, "SUPPORTED_EXTENSIONS was not found in uploadFileTypes.ts.");
        var screen = Regex.Matches(list.Groups["items"].Value, @"'(?<ext>\.[a-z0-9]+)'")
            .Select(m => m.Groups["ext"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(DocumentIntakeAllowList.Extensions.SetEquals(screen),
            $"Screen only: [{string.Join(", ", screen.Except(DocumentIntakeAllowList.Extensions))}]; "
            + $"server only: [{string.Join(", ", DocumentIntakeAllowList.Extensions.Except(screen))}].");
        Assert.Contains(".msg", screen);
        Assert.DoesNotContain(".zip", screen);   // the server does not open archives
    }
}
