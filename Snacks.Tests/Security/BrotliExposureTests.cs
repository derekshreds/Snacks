using FluentAssertions;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>
///     The Brotli decoder bundled with .NET 10 is affected by CVE-2026-111497. Snacks keeps it
///     unreachable by never decoding Brotli: no HttpClient advertises or decodes "br", no code
///     decompresses Brotli streams, and request bodies are never decompressed. This guards
///     against any of those creeping back in.
/// </summary>
public sealed class BrotliExposureTests
{
    private static readonly string[] ForbiddenPatterns =
    [
        "DecompressionMethods.All",
        "DecompressionMethods.Brotli",
        "BrotliStream",
        "BrotliDecoder",
        "UseRequestDecompression",
        "AddRequestDecompression",
    ];

    [Fact]
    public void Application_code_never_enables_a_brotli_decode_path()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Snacks.sln"))) root = root.Parent;
        root.Should().NotBeNull("the test runs from inside the repository");

        var sources = Directory.EnumerateFiles(Path.Combine(root!.FullName, "Snacks"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        sources.Should().NotBeEmpty();

        var hits = sources
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => ForbiddenPatterns.Any(p => l.Text.Contains(p, StringComparison.Ordinal)))
            .Select(l => $"{Path.GetRelativePath(root.FullName, l.File)}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        hits.Should().BeEmpty();
    }
}
