namespace Snacks.Services.Cluster;

using Snacks.Models;

/// <summary>
///     Validates a path the master sent for shared-storage dispatch against the
///     node's allowlists. Canonicalizes the path, resolves symlinks, then
///     prefix-checks against an allowlist with a trailing-slash test that prevents
///     <c>/foo</c> from matching <c>/foobar</c>. Optional rewrite is applied
///     before the allowlist check so master and node can mount the same share at
///     different paths.
/// </summary>
public static class SharedStoragePathValidator
{
    /// <summary>
    ///     Outcome of a validation. <see cref="Mode"/> is <c>"shared"</c> when the
    ///     master's path was accepted; <c>"upload"</c> when the node will fall back
    ///     to the regular transfer flow. <see cref="Reason"/> populated only on
    ///     fallback to surface the cause in logs.
    /// </summary>
    public sealed class Result
    {
        public string Mode { get; init; } = "upload";
        public string? Reason { get; init; }
        public string? ResolvedInputPath { get; init; }
        public string? ResolvedOutputPath { get; init; }
        public string? ResolvedOutputDirectory { get; init; }
    }

    /// <summary>
    ///     Validates the master-provided shared paths. Returns a <see cref="Result"/>
    ///     describing whether the node will accept shared mode for this job.
    ///     Never throws — failures fall back to upload mode with a logged reason.
    /// </summary>
    /// <param name="metadata">The dispatch metadata from the master.</param>
    /// <param name="config">The node's cluster configuration.</param>
    public static Result Validate(JobMetadata metadata, ClusterConfig config)
    {
        if (!config.SharedStorageEnabled)
            return new Result { Mode = "upload", Reason = "Node has shared storage disabled" };

        if (string.IsNullOrEmpty(metadata.SharedStorageInputPath))
            return new Result { Mode = "upload", Reason = "Master did not offer a shared input path" };

        try
        {
            // Input: rewrite → canonicalize → resolve symlink → re-canonicalize → allowlist.
            var resolvedInput = ResolvePath(metadata.SharedStorageInputPath, config);
            if (!IsUnderAllowlist(resolvedInput, config.SharedStorageInputPaths))
                return new Result { Mode = "upload", Reason = $"Input path '{resolvedInput}' is not under any allowed input directory" };

            if (!File.Exists(resolvedInput))
                return new Result { Mode = "upload", Reason = $"Input file does not exist on node at '{resolvedInput}'" };

            // Probe-read so a stale mount or permission error is caught up front
            // rather than after the encode has already started.
            try
            {
                using var probe = new FileStream(resolvedInput, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[4];
                _ = probe.Read(buf, 0, buf.Length);
            }
            catch (Exception ex)
            {
                return new Result { Mode = "upload", Reason = $"Input file is not readable: {ex.Message}" };
            }

            // Output: optional. If the master didn't supply one, accept shared
            // input only — output will follow the regular download path.
            string? resolvedOutput = null;
            string? resolvedOutputDir = null;
            if (!string.IsNullOrEmpty(metadata.SharedStorageOutputPath) ||
                !string.IsNullOrEmpty(metadata.SharedStorageOutputDirectory))
            {
                if (string.IsNullOrEmpty(metadata.SharedStorageOutputDirectory))
                    return new Result { Mode = "upload", Reason = "Master supplied output path without an output directory" };

                resolvedOutputDir = ResolvePath(metadata.SharedStorageOutputDirectory, config);
                if (!IsUnderAllowlist(resolvedOutputDir, config.SharedStorageOutputPaths))
                    return new Result { Mode = "upload", Reason = $"Output directory '{resolvedOutputDir}' is not under any allowed output directory" };

                if (!Directory.Exists(resolvedOutputDir))
                    return new Result { Mode = "upload", Reason = $"Output directory does not exist on node at '{resolvedOutputDir}'" };

                // Write probe — fail fast on read-only mounts or permission issues.
                var probeName = Path.Combine(resolvedOutputDir, $".snacks-shared-probe-{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllText(probeName, "");
                    File.Delete(probeName);
                }
                catch (Exception ex)
                {
                    return new Result { Mode = "upload", Reason = $"Output directory is not writable: {ex.Message}" };
                }

                if (!string.IsNullOrEmpty(metadata.SharedStorageOutputPath))
                {
                    // Output filename rebuilt under the resolved directory so a
                    // cross-rewrite path stays consistent — the master only knows
                    // its own view of the share.
                    var outputFileName = Path.GetFileName(metadata.SharedStorageOutputPath);
                    if (string.IsNullOrEmpty(outputFileName))
                        return new Result { Mode = "upload", Reason = "Output path has no filename component" };
                    resolvedOutput = Path.Combine(resolvedOutputDir, outputFileName);
                }
            }

            return new Result
            {
                Mode = "shared",
                ResolvedInputPath = resolvedInput,
                ResolvedOutputPath = resolvedOutput,
                ResolvedOutputDirectory = resolvedOutputDir,
            };
        }
        catch (Exception ex)
        {
            return new Result { Mode = "upload", Reason = $"Validation threw: {ex.Message}" };
        }
    }

    /// <summary>
    ///     Applies the configured rewrite, canonicalizes, then resolves a single
    ///     symlink hop and re-canonicalizes the result. The two-step canonicalize
    ///     defends against an allowlisted directory that contains a symlink which
    ///     points outside the allowlist.
    /// </summary>
    private static string ResolvePath(string raw, ClusterConfig config)
    {
        var rewritten = ApplyRewrite(raw, config);
        var canonical = Path.GetFullPath(rewritten);

        // ResolveLinkTarget(true) follows the chain to the final target. If the
        // path itself isn't a link, the call returns null and we keep the
        // canonical path. Wrapped in a try because some platforms throw rather
        // than return null on permission errors.
        try
        {
            var info = new FileInfo(canonical);
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (!string.IsNullOrEmpty(resolved))
                canonical = Path.GetFullPath(resolved);
        }
        catch { }

        return canonical;
    }

    private static string ApplyRewrite(string raw, ClusterConfig config)
    {
        // EffectiveRewrites is longest-prefix-first (legacy single pair folded in),
        // so nested mounts resolve to the deepest match and the first hit wins.
        foreach (var rewrite in config.EffectiveRewrites())
        {
            // Match on the exact prefix only — a substring match would silently
            // rewrite unrelated paths that happen to contain the same text.
            if (raw.StartsWith(rewrite.From, PathComparison))
                return rewrite.To + raw.Substring(rewrite.From.Length);
        }
        return raw;
    }

    private static bool IsUnderAllowlist(string canonicalPath, List<string> allowlist)
    {
        if (allowlist.Count == 0) return false;

        foreach (var raw in allowlist)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var allowed = Path.GetFullPath(raw);
            if (string.Equals(canonicalPath, allowed, PathComparison)) return true;

            // Trailing-separator check stops "/foo" matching "/foobar".
            var withSep = allowed.EndsWith(Path.DirectorySeparatorChar) || allowed.EndsWith(Path.AltDirectorySeparatorChar)
                ? allowed
                : allowed + Path.DirectorySeparatorChar;
            if (canonicalPath.StartsWith(withSep, PathComparison)) return true;
        }
        return false;
    }

    // Windows paths are case-insensitive; Linux/macOS are case-sensitive. The same
    // image is shipped to both, so honor the OS rules at runtime.
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
