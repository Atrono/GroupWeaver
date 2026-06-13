using System.Text;

using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins the shipped example rulesets in examples/rulesets/ (ADR-008, AP 3.1
/// slice 9): every example must run through <see cref="RulesetLoader.Load"/>
/// cleanly, and the published copy of the default must never drift from the
/// embedded <c>GroupWeaver.Core.Rules.DefaultRuleset.jsonc</c> resource.
/// Line endings are normalized (\r\n -> \n) before the drift comparison so
/// CRLF checkout settings (.gitattributes "* text=auto") cannot flake the
/// pin; everything else — including a BOM — must be byte-identical.
/// </summary>
public class ExampleRulesetTests
{
    private const string EmbeddedDefaultResourceName = "GroupWeaver.Core.Rules.DefaultRuleset.jsonc";

    [Fact]
    public void PureAgdlpExample_LoadsWithZeroErrors()
    {
        var path = ExamplePath("pure-agdlp.jsonc");
        Assert.True(File.Exists(path), $"Example ruleset missing: {path}");

        var result = RulesetLoader.Load(File.ReadAllText(path));

        Assert.Empty(result.Errors);
        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.NotNull(result.Ruleset);
    }

    [Fact]
    public void DefaultStrictAgdlpExample_IsByteIdenticalToEmbeddedDefault_AfterNewlineNormalization()
    {
        var path = ExamplePath("default-strict-agdlp.jsonc");
        Assert.True(File.Exists(path), $"Example ruleset missing: {path}");

        var exampleBytes = NormalizeNewlines(File.ReadAllBytes(path));
        var embeddedBytes = NormalizeNewlines(ReadEmbeddedDefaultBytes());

        // String comparison first: content drift fails with a readable diff ...
        Assert.Equal(
            Encoding.UTF8.GetString(embeddedBytes),
            Encoding.UTF8.GetString(exampleBytes));

        // ... but the pin is BYTES: BOM or encoding drift must fail too.
        Assert.Equal(embeddedBytes, exampleBytes);
    }

    // --- helpers ---------------------------------------------------------------

    /// <summary>Drops the \r of every \r\n pair; lone \r and everything else
    /// pass through untouched, so only the CRLF-vs-LF checkout difference is
    /// forgiven.</summary>
    private static byte[] NormalizeNewlines(byte[] bytes)
    {
        var result = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue;
            }

            result.Add(bytes[i]);
        }

        return [.. result];
    }

    private static byte[] ReadEmbeddedDefaultBytes()
    {
        using var stream = typeof(RulesetLoader).Assembly.GetManifestResourceStream(EmbeddedDefaultResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {EmbeddedDefaultResourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ExamplePath(string fileName)
        => Path.Combine(FindRepoRoot(), "examples", "rulesets", fileName);

    // Same repo-root resolution as SmokeTests: ascend from the test output
    // directory until the solution file appears.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "GroupWeaver.sln not found in any parent of the test output directory.");
    }
}
