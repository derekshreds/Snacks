using System.IO;
using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Regression coverage for the "cluster mux-pass jobs vanish into Skipped" bug.
///
///     <para>The pipeline interaction the user hit:</para>
///     <list type="number">
///         <item>Worker successfully encoded a remux (or any keep-eligible output) into <c>{base} [snacks].{ext}</c> in its per-job temp dir.</item>
///         <item>With master config <c>DeleteOriginalFile = true</c> + in-place output, <see cref="TranscodingService.GetCleanOutputName"/> stripped the <c>[snacks]</c> tag during the move.</item>
///         <item><c>ClusterNodeJobService.GetOutputFileForJob</c> globs <c>*[snacks]*</c> in the temp dir — the rename made the file invisible to the glob, returning <see langword="null"/>.</item>
///         <item>That <see langword="null"/> got reported to the master as <c>noSavings = true</c>; the master wrote <c>MediaFile.Status = Skipped</c> and the encoded output was reaped with the worker's temp dir. Original on disk untouched, encoded version gone.</item>
///     </list>
///
///     <para>The fix: <see cref="TranscodingService.ConvertVideoForRemoteAsync"/> passes <c>skipPlacement: true</c>
///     to <c>ConvertVideoAsync</c>, which guards the <c>HandleOutputPlacement</c> call on the keep branch.
///     The <c>[snacks]</c>-tagged file survives in the worker's temp dir for the master to download, and the
///     master runs <c>HandleOutputPlacement</c> against the user's actual library where it makes sense.</para>
/// </summary>
public sealed class RemoteConversionPlacementTests
{
    // =====================================================================
    //  Documents the rename behavior that bit on the worker
    // =====================================================================

    [Fact]
    public void GetCleanOutputName_StripsSnacksTag_FromInPlaceOutput()
    {
        // Given an in-place output path with the [snacks] staging tag…
        var snacksPath = Path.Combine(Path.GetTempPath(), "Toy Story 2 (1999) [snacks].mkv");

        // …GetCleanOutputName produces the user-facing destination name without the tag.
        var clean = TranscodingService.GetCleanOutputName(snacksPath);

        clean.Should().NotContain("[snacks]");
        clean.Should().EndWith("Toy Story 2 (1999).mkv");
    }

    [Fact]
    public void GetCleanOutputName_TagStrip_HidesFileFromSnacksGlob()
    {
        // The "smoking gun" interaction: after the rename, the *[snacks]* glob that
        // ClusterNodeJobService.GetOutputFileForJob uses to locate the worker's encoded
        // output finds nothing. On the master that's fine — placement put the file in the
        // user's library. On a worker, that's the bug: the encoded file is still in temp,
        // just renamed, and the worker now reports noSavings=true to the master.

        var tempRoot = Path.Combine(Path.GetTempPath(), $"snacks-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var snacksFile = Path.Combine(tempRoot, "Toy Story 2 (1999) [snacks].mkv");
            File.WriteAllText(snacksFile, "encoded-bytes");

            // Sanity: glob finds the [snacks] file when it has the tag.
            Directory.GetFiles(tempRoot, "*[snacks]*").Should().ContainSingle();

            // Master's in-place DeleteOriginalFile branch performs this rename. We don't
            // care about FileMoveAsync's IO mechanics here — File.Move is the same shape.
            var cleanPath = TranscodingService.GetCleanOutputName(snacksFile);
            File.Move(snacksFile, cleanPath);

            // The encoded file is still on disk, but now has no [snacks] tag.
            File.Exists(cleanPath).Should().BeTrue();

            // …so the worker's glob returns nothing. This is the false-noSavings trigger
            // that the Phase 1 fix prevents by NOT running the rename on the worker.
            Directory.GetFiles(tempRoot, "*[snacks]*").Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    // =====================================================================
    //  Locks in the fix: ConvertVideoForRemoteAsync passes skipPlacement: true
    // =====================================================================

    [Fact]
    public void ConvertVideoForRemoteAsync_PassesSkipPlacementTrue_ToInnerConvert()
    {
        // The full encode pipeline runs ffmpeg + many DI dependencies, so an integration
        // test isn't feasible here. The contract we want to lock in is a one-liner —
        // ConvertVideoForRemoteAsync MUST call ConvertVideoAsync with skipPlacement: true.
        // Read the source verbatim and assert. A future change that drops or flips the flag
        // breaks this test loudly.

        var sourcePath = LocateSourceFile("Snacks/Services/TranscodingService.cs");
        var src = File.ReadAllText(sourcePath);

        // Find the body of ConvertVideoForRemoteAsync.
        var marker = "public async Task ConvertVideoForRemoteAsync(";
        var startIdx = src.IndexOf(marker, StringComparison.Ordinal);
        startIdx.Should().BeGreaterThan(-1, "ConvertVideoForRemoteAsync must exist");

        // Take a generous window after the signature to cover the body.
        var window = src.Substring(startIdx, Math.Min(2000, src.Length - startIdx));

        window.Should().Contain("ConvertVideoAsync(",
            "ConvertVideoForRemoteAsync should delegate to ConvertVideoAsync");
        window.Should().Contain("skipPlacement: true",
            "the fix is for ConvertVideoForRemoteAsync to call ConvertVideoAsync with skipPlacement: true; " +
            "without it, the worker runs HandleOutputPlacement and renames its [snacks] file out of existence, " +
            "causing GetOutputFileForJob to return null and the master to falsely mark the row Skipped");
    }

    /// <summary>
    ///     Walks up from the test binary to find the repo-relative source file. Test runs
    ///     happen from <c>Snacks.Tests/bin/.../</c>, so we step out until we find the .sln.
    /// </summary>
    private static string LocateSourceFile(string repoRelativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Snacks.sln")))
                return Path.Combine(dir, repoRelativePath);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException("Could not locate Snacks.sln from test base dir");
    }
}
