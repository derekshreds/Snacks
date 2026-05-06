using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services.Cluster;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the security-critical path-validation logic that gates shared-storage
///     dispatches. The validator is the boundary that decides whether the master's
///     advertised path lands inside an allowlisted directory after canonicalization
///     and optional rewrite — every test here corresponds to either an attack vector
///     (traversal, prefix collision) or a legitimate setup the user is likely to
///     configure (rewrite, distinct input/output dirs).
/// </summary>
public sealed class SharedStoragePathValidatorTests : IDisposable
{
    private readonly string _root;

    public SharedStoragePathValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "snacks-validator-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    ///     Builds a temp file inside the test root with deterministic content so
    ///     the validator's read-probe and the prefix check both have something
    ///     real to attach to.
    /// </summary>
    private string MakeFile(string relative, byte[]? content = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? new byte[] { 0x52, 0x49, 0x46, 0x46 });
        return path;
    }

    private string MakeDir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static JobMetadata Meta(string? input, string? outputDir = null, string? outputPath = null) => new()
    {
        JobId                       = "job-1",
        FileName                    = "movie.mkv",
        FileSize                    = 4,
        SharedStorageInputPath      = input,
        SharedStorageOutputDirectory = outputDir,
        SharedStorageOutputPath     = outputPath,
    };

    private static ClusterConfig Config(
        bool enabled = true,
        IEnumerable<string>? inputs = null,
        IEnumerable<string>? outputs = null,
        string? rewriteFrom = null,
        string? rewriteTo = null) => new()
    {
        SharedStorageEnabled            = enabled,
        SharedStorageInputPaths         = new List<string>(inputs  ?? Array.Empty<string>()),
        SharedStorageOutputPaths        = new List<string>(outputs ?? Array.Empty<string>()),
        SharedStoragePathRewriteFrom    = rewriteFrom,
        SharedStoragePathRewriteTo      = rewriteTo,
    };

    // =========================================================================
    //  Fail-closed defaults
    // =========================================================================

    [Fact]
    public void NodeDisabled_FallsBackToUpload()
    {
        var file = MakeFile("input.mkv");
        var result = SharedStoragePathValidator.Validate(
            Meta(file),
            Config(enabled: false, inputs: new[] { _root }));

        result.Mode.Should().Be("upload");
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void MasterDidNotOfferPath_FallsBackToUpload()
    {
        var result = SharedStoragePathValidator.Validate(
            Meta(input: null),
            Config(inputs: new[] { _root }));

        result.Mode.Should().Be("upload");
    }

    [Fact]
    public void EmptyAllowlist_RejectsEverything()
    {
        var file = MakeFile("input.mkv");
        var result = SharedStoragePathValidator.Validate(
            Meta(file),
            Config(inputs: Array.Empty<string>()));

        result.Mode.Should().Be("upload");
        result.Reason.Should().Contain("not under any allowed");
    }

    // =========================================================================
    //  Attack vectors
    // =========================================================================

    [Fact]
    public void PathTraversal_CanonicalizedOutOfAllowlist()
    {
        // Allowlist a child dir; the master "offers" a path that escapes via "..".
        var allowedDir = MakeDir("allowed");
        var sneakyPath = Path.Combine(allowedDir, "..", "..", "etc", "passwd");

        var result = SharedStoragePathValidator.Validate(
            Meta(sneakyPath),
            Config(inputs: new[] { allowedDir }));

        result.Mode.Should().Be("upload");
    }

    [Fact]
    public void PrefixCollision_DoesNotMatchSiblingDirectory()
    {
        // Allowlist "/root/foo"; an attacker offers "/root/foobar/x". The naive
        // StartsWith would let it through; the trailing-separator guard MUST
        // keep them apart.
        var allowed = MakeDir("foo");
        var sibling = MakeFile("foobar/leak.mkv");

        var result = SharedStoragePathValidator.Validate(
            Meta(sibling),
            Config(inputs: new[] { allowed }));

        result.Mode.Should().Be("upload");
    }

    [Fact]
    public void MissingInputFile_FallsBackToUpload()
    {
        var allowed = MakeDir("library");
        var ghost   = Path.Combine(allowed, "does-not-exist.mkv");

        var result = SharedStoragePathValidator.Validate(
            Meta(ghost),
            Config(inputs: new[] { allowed }));

        result.Mode.Should().Be("upload");
        result.Reason.Should().Contain("does not exist");
    }

    // =========================================================================
    //  Happy path
    // =========================================================================

    [Fact]
    public void InputUnderAllowlist_AcceptsSharedMode()
    {
        var allowed = MakeDir("library");
        var input   = MakeFile("library/movie.mkv");

        var result = SharedStoragePathValidator.Validate(
            Meta(input),
            Config(inputs: new[] { allowed }));

        result.Mode.Should().Be("shared");
        result.ResolvedInputPath.Should().Be(Path.GetFullPath(input));
        result.ResolvedOutputPath.Should().BeNull();
    }

    [Fact]
    public void InputAndOutput_BothUnderAllowlists_AcceptsSharedMode()
    {
        var inputDir  = MakeDir("library");
        var outputDir = MakeDir("encoded");
        var input     = MakeFile("library/movie.mkv");
        var outPath   = Path.Combine(outputDir, "movie [snacks].mkv");

        var result = SharedStoragePathValidator.Validate(
            Meta(input, outputDir: outputDir, outputPath: outPath),
            Config(inputs: new[] { inputDir }, outputs: new[] { outputDir }));

        result.Mode.Should().Be("shared");
        result.ResolvedOutputDirectory.Should().Be(Path.GetFullPath(outputDir));
        result.ResolvedOutputPath.Should().Be(Path.GetFullPath(outPath));
    }

    [Fact]
    public void OutputDir_NotUnderOutputAllowlist_FallsBackToUpload()
    {
        var inputDir  = MakeDir("library");
        var outputDir = MakeDir("encoded");
        var elsewhere = MakeDir("other");
        var input     = MakeFile("library/movie.mkv");
        var outPath   = Path.Combine(elsewhere, "movie [snacks].mkv");

        var result = SharedStoragePathValidator.Validate(
            Meta(input, outputDir: elsewhere, outputPath: outPath),
            Config(inputs: new[] { inputDir }, outputs: new[] { outputDir }));

        result.Mode.Should().Be("upload");
        result.Reason.Should().Contain("output");
    }

    [Fact]
    public void SeparateInputAndOutputAllowlists_AreEnforcedIndependently()
    {
        // Read-only NAS export modeled here: input is allowlisted but output is
        // not, so output must come from a different allowlist entry.
        var inputDir  = MakeDir("readonly");
        var outputDir = MakeDir("writable");
        var input     = MakeFile("readonly/movie.mkv");
        var outPath   = Path.Combine(outputDir, "movie [snacks].mkv");

        // Output dir not in INPUT allowlist (would leak readable area otherwise),
        // input dir not in OUTPUT allowlist (would imply writable readonly mount).
        var result = SharedStoragePathValidator.Validate(
            Meta(input, outputDir: outputDir, outputPath: outPath),
            Config(inputs: new[] { inputDir }, outputs: new[] { outputDir }));

        result.Mode.Should().Be("shared");
    }

    // =========================================================================
    //  Path rewrite
    // =========================================================================

    [Fact]
    public void Rewrite_TranslatesMasterPathToNodeMount()
    {
        // Master sees the share at "/srv/master-shared", node mounts it at
        // {_root}/library. The rewrite makes the master-supplied path resolve
        // into the node's view before allowlist checking.
        var nodeMount  = MakeDir("library");
        _              = MakeFile("library/movie.mkv");
        var masterPath = "/srv/master-shared/movie.mkv";

        var result = SharedStoragePathValidator.Validate(
            Meta(masterPath),
            Config(
                inputs: new[] { nodeMount },
                rewriteFrom: "/srv/master-shared",
                rewriteTo:   nodeMount));

        result.Mode.Should().Be("shared");
        result.ResolvedInputPath!.Replace('\\', '/')
            .Should().EndWith("library/movie.mkv");
    }

    [Fact]
    public void Rewrite_OnlyAppliesAsExactPrefix()
    {
        // The rewrite from is "/foo"; an unrelated path "/foobar/x" must NOT be
        // rewritten just because "/foo" is a substring.
        var nodeMount = MakeDir("library");
        var unrelated = MakeFile("library/foobar.mkv");

        var result = SharedStoragePathValidator.Validate(
            Meta(unrelated),
            Config(
                inputs: new[] { nodeMount },
                rewriteFrom: "/foo",
                rewriteTo:   "/should-not-apply"));

        // The path was not in the rewrite scope, so it was canonicalized as-is
        // and the allowlist check passed normally.
        result.Mode.Should().Be("shared");
        result.ResolvedInputPath.Should().Be(Path.GetFullPath(unrelated));
    }
}
