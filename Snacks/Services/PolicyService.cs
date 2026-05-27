using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Business logic for the named encoder-policy bundles (built-in seeds,
///     CRUD over <c>config/policies.json</c>, active-policy tracking,
///     import-rename-on-collision, and import-side schema/sanitization checks).
///
///     Persistence is delegated to <see cref="ConfigFileService"/> so the
///     atomic write-then-rename + <c>.bak</c> fallback used everywhere else
///     applies here too. The controller stays a thin transport layer; this
///     service is what the tests exercise.
/// </summary>
public sealed class PolicyService
{
    private const string FileName = "policies.json";

    private readonly ConfigFileService _configFiles;
    private readonly object            _lock = new();

    /// <summary>
    ///     Serializer used for the canonical-equality comparison driving the active-policy
    ///     query. Same options as <see cref="SettingsPersistenceService"/> so the JSON
    ///     produced by both layers compares byte-for-byte.
    /// </summary>
    private static readonly JsonSerializerOptions _canonical = new()
    {
        WriteIndented               = false,
        PropertyNameCaseInsensitive = true,
    };

    public PolicyService(ConfigFileService configFiles)
    {
        ArgumentNullException.ThrowIfNull(configFiles);
        _configFiles = configFiles;
    }

    /******************************************************************
     *  Public API
     ******************************************************************/

    /// <summary>
    ///     Returns every policy known to the system (built-ins + custom).
    ///     Built-ins appear first, then custom in insertion order.
    /// </summary>
    public IReadOnlyList<Policy> List()
    {
        lock (_lock) return LoadAndSeed().Policies.Select(p => p.Clone()).ToList();
    }

    /// <summary> Returns a single policy by Id, or <see langword="null"/> when not found. </summary>
    public Policy? Get(string id)
    {
        lock (_lock) return LoadAndSeed().Policies.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    /// <summary>
    ///     Returns the id of the last-applied policy (or <see langword="null"/> when none
    ///     has ever been applied). Read by the apply endpoint after persistence to update
    ///     active-policy state on subsequent reads.
    /// </summary>
    public string? GetLastAppliedPolicyId()
    {
        lock (_lock) return LoadAndSeed().LastAppliedPolicyId;
    }

    /// <summary>
    ///     Records <paramref name="policyId"/> as the most recently applied policy.
    ///     Called by the apply endpoint after the settings have been persisted; the
    ///     value is then used by <see cref="GetActive"/> to distinguish "still on the
    ///     applied policy" from "user has tweaked settings since applying".
    /// </summary>
    public void SetLastAppliedPolicyId(string? policyId)
    {
        lock (_lock)
        {
            var doc = LoadAndSeed();
            doc.LastAppliedPolicyId = policyId;
            Save(doc);
        }
    }

    /// <summary>
    ///     Resolves the active policy by comparing <paramref name="currentSettings"/>
    ///     against the last-applied policy (if any) and falling back to a scan of every
    ///     policy looking for an exact match. Distinguishes three cases:
    ///     <list type="bullet">
    ///         <item><b>Exact match</b> &mdash; settings equal a known policy's options. Returns that policy with <c>Modified=false</c>.</item>
    ///         <item><b>Drift</b> &mdash; settings differ from <c>LastAppliedPolicyId</c>'s options. Returns that policy with <c>Modified=true</c>.</item>
    ///         <item><b>Custom</b> &mdash; no last-applied id and no policy matches. Returns <c>{Id=null, Name="Custom"}</c>.</item>
    ///     </list>
    /// </summary>
    public ActivePolicyResult GetActive(EncoderOptions currentSettings)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);

        lock (_lock)
        {
            var doc         = LoadAndSeed();
            // Strip machine-local fields from BOTH sides before comparing so the user's
            // OutputDirectory / EncodeDirectory don't make the active state read as
            // "modified" just because the policy stores them as null. Active-state
            // semantics are about encoding choices, not local paths.
            var currentJson = CanonicalForCompare(currentSettings);

            // Prefer the last-applied policy: that's the user's explicit intent.
            if (!string.IsNullOrEmpty(doc.LastAppliedPolicyId))
            {
                var pinned = doc.Policies.FirstOrDefault(p => p.Id == doc.LastAppliedPolicyId);
                if (pinned != null)
                {
                    return new ActivePolicyResult
                    {
                        Id       = pinned.Id,
                        Name     = pinned.Name,
                        Modified = CanonicalForCompare(pinned.Options) != currentJson,
                    };
                }
            }

            // No last-applied (fresh install) or it was deleted: look for an exact match.
            foreach (var p in doc.Policies)
            {
                if (CanonicalForCompare(p.Options) == currentJson)
                {
                    return new ActivePolicyResult { Id = p.Id, Name = p.Name, Modified = false };
                }
            }

            return new ActivePolicyResult { Id = null, Name = "Custom", Modified = false };
        }
    }

    /// <summary>
    ///     Serializes <paramref name="options"/> for the active-state equality check,
    ///     normalizing out machine-local fields so two installs comparing the same
    ///     policy don't disagree on identity just because their <see cref="EncoderOptions.OutputDirectory"/>
    ///     values happen to differ.
    /// </summary>
    private static string CanonicalForCompare(EncoderOptions options)
    {
        var clone = options.Clone();
        clone.ClearMachineLocalFields();
        return JsonSerializer.Serialize(clone, _canonical);
    }

    /// <summary>
    ///     Persists a new custom policy. A fresh GUID Id is assigned regardless of
    ///     what the caller supplies, and <c>BuiltIn</c> is always set to false.
    ///     Outcome bullets are sanitized (trimmed + empties dropped) on the way in
    ///     so the picker never renders a blank line, regardless of what the client sent.
    /// </summary>
    public Policy Create(string name, string? description, IEnumerable<string>? outcomeBullets, EncoderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            var doc    = LoadAndSeed();
            var unique = MakeUniqueName(doc.Policies, name.Trim());
            var policy = new Policy
            {
                Id             = "policy-" + Guid.NewGuid().ToString("N"),
                Name           = unique,
                Description    = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                OutcomeBullets = SanitizeBullets(outcomeBullets),
                BuiltIn        = false,
                CreatedUtc     = DateTime.UtcNow,
                UpdatedUtc     = DateTime.UtcNow,
                Options        = options.Clone(),
            };
            policy.Options.ApplyLegacyAudioMigration();
            // Filesystem paths are machine-local and never belong in a portable policy.
            policy.Options.ClearMachineLocalFields();

            doc.Policies.Add(policy);
            Save(doc);
            return policy.Clone();
        }
    }

    /// <summary>
    ///     Updates a custom policy. Returns the updated policy, or <see langword="null"/>
    ///     when no policy with the given Id exists. Throws
    ///     <see cref="InvalidOperationException"/> when the target is a built-in.
    ///     Same sanitization rules as <see cref="Create"/> for description + bullets.
    /// </summary>
    public Policy? Update(string id, string name, string? description, IEnumerable<string>? outcomeBullets, EncoderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        lock (_lock)
        {
            var doc   = LoadAndSeed();
            var index = doc.Policies.FindIndex(p => p.Id == id);
            if (index < 0) return null;
            if (doc.Policies[index].BuiltIn)
                throw new InvalidOperationException("Built-in policies are read-only. Duplicate this policy to customize it.");

            var existing = doc.Policies[index];
            existing.Name           = name.Trim();
            existing.Description    = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            existing.OutcomeBullets = SanitizeBullets(outcomeBullets);
            existing.Options        = options.Clone();
            existing.Options.ApplyLegacyAudioMigration();
            // Same machine-local rule as Create - paths never persist into a policy.
            existing.Options.ClearMachineLocalFields();
            existing.UpdatedUtc     = DateTime.UtcNow;

            Save(doc);
            return existing.Clone();
        }
    }

    /// <summary>
    ///     Trims every entry and drops blanks. <see langword="null"/> input yields an
    ///     empty list. Caps at 12 lines so a user pasting their entire CHANGELOG doesn't
    ///     turn the picker card into a wall of text.
    /// </summary>
    private static List<string> SanitizeBullets(IEnumerable<string>? bullets)
    {
        if (bullets == null) return new();
        return bullets
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .Take(12)
            .ToList();
    }

    /// <summary>
    ///     Deletes a custom policy. Returns <see langword="false"/> when no policy
    ///     with the given Id exists. Throws <see cref="InvalidOperationException"/>
    ///     when the target is a built-in. Clears <see cref="PolicyDocument.LastAppliedPolicyId"/>
    ///     when the deleted policy was the active one so the UI falls back to "Custom".
    /// </summary>
    public bool Delete(string id)
    {
        lock (_lock)
        {
            var doc   = LoadAndSeed();
            var index = doc.Policies.FindIndex(p => p.Id == id);
            if (index < 0) return false;
            if (doc.Policies[index].BuiltIn)
                throw new InvalidOperationException("Built-in policies cannot be deleted. Duplicate this policy if you need a customizable copy.");

            doc.Policies.RemoveAt(index);
            if (doc.LastAppliedPolicyId == id) doc.LastAppliedPolicyId = null;
            Save(doc);
            return true;
        }
    }

    /// <summary>
    ///     Server-side clone of an existing policy into a new custom one. Useful as the
    ///     "duplicate this built-in so I can tweak it" entry point. Returns the new policy
    ///     or <see langword="null"/> when the source Id is missing.
    /// </summary>
    public Policy? Duplicate(string id)
    {
        lock (_lock)
        {
            var doc = LoadAndSeed();
            var src = doc.Policies.FirstOrDefault(p => p.Id == id);
            if (src == null) return null;

            return Create(src.Name + " (custom)", src.Description, src.OutcomeBullets, src.Options);
        }
    }

    /// <summary>
    ///     Wraps a single policy in a <see cref="PolicyDocument"/> for download/export.
    ///     The built-in flag is preserved in the export so the user can see the lineage,
    ///     but <see cref="Import"/> always strips it on the way back in.
    /// </summary>
    public PolicyDocument? ExportOne(string id)
    {
        lock (_lock)
        {
            var policy = LoadAndSeed().Policies.FirstOrDefault(p => p.Id == id);
            if (policy == null) return null;
            return new PolicyDocument
            {
                SchemaVersion = PolicyDocument.CurrentSchemaVersion,
                Policies      = new() { policy.Clone() },
            };
        }
    }

    /// <summary>
    ///     Exports every custom (non-built-in) policy. Built-ins are excluded because they're
    ///     already present on every Snacks install and would be renamed to "Balanced (imported)"
    ///     on the way back in, which is not what users want.
    /// </summary>
    public PolicyDocument ExportAllCustom()
    {
        lock (_lock)
        {
            return new PolicyDocument
            {
                SchemaVersion = PolicyDocument.CurrentSchemaVersion,
                Policies      = LoadAndSeed().Policies.Where(p => !p.BuiltIn).Select(p => p.Clone()).ToList(),
            };
        }
    }

    /// <summary>
    ///     Imports the policies in <paramref name="document"/>. Every entry is treated as
    ///     a new custom policy: a fresh Id is assigned, <c>BuiltIn</c> is forced to false,
    ///     and on name collision the imported entry is renamed to <c>"{name} (imported)"</c>
    ///     (with a numeric suffix when that also collides).
    ///
    ///     Throws <see cref="InvalidOperationException"/> when the document's
    ///     <see cref="PolicyDocument.SchemaVersion"/> is higher than
    ///     <see cref="PolicyDocument.CurrentSchemaVersion"/>.
    /// </summary>
    public IReadOnlyList<Policy> Import(PolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion <= 0)
            throw new InvalidOperationException("Import file is missing a schema version.");
        if (document.SchemaVersion > PolicyDocument.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Import file targets policy schema {document.SchemaVersion}, but this version of Snacks only supports up to {PolicyDocument.CurrentSchemaVersion}. Upgrade Snacks to import this file.");

        lock (_lock)
        {
            var doc   = LoadAndSeed();
            var added = new List<Policy>();

            foreach (var incoming in document.Policies ?? new())
            {
                if (incoming == null || incoming.Options == null) continue;
                if (string.IsNullOrWhiteSpace(incoming.Name))     continue;

                var renamed = incoming.Clone();
                renamed.Id         = "policy-" + Guid.NewGuid().ToString("N");
                renamed.BuiltIn    = false;
                renamed.Name       = MakeUniqueName(doc.Policies, renamed.Name.Trim(), markImported: true);
                renamed.CreatedUtc = DateTime.UtcNow;
                renamed.UpdatedUtc = DateTime.UtcNow;
                renamed.Options.ApplyLegacyAudioMigration();
                // Strip paths from the imported policy - the exporter's local paths
                // mean nothing here and would silently overwrite the importer's
                // configured OutputDirectory on first apply.
                renamed.Options.ClearMachineLocalFields();

                doc.Policies.Add(renamed);
                added.Add(renamed.Clone());
            }

            Save(doc);
            return added;
        }
    }

    /******************************************************************
     *  Internal helpers
     ******************************************************************/

    /// <summary>
    ///     Loads the policy document from disk, seeding any missing built-ins on the way
    ///     out. Always returns a document with every built-in present in canonical order,
    ///     followed by whatever custom policies the user has saved. Persists when seeding
    ///     adds anything so subsequent reads are stable.
    /// </summary>
    private PolicyDocument LoadAndSeed()
    {
        var loaded   = _configFiles.Load<PolicyDocument>(FileName);
        var builtIns = Policy.BuiltIns();

        var seeded = new PolicyDocument
        {
            SchemaVersion       = PolicyDocument.CurrentSchemaVersion,
            LastAppliedPolicyId = loaded.LastAppliedPolicyId,
            Policies            = new(),
        };
        seeded.Policies.AddRange(builtIns.Select(p => p.Clone()));

        foreach (var p in loaded.Policies ?? new())
        {
            if (p == null) continue;
            // Drop any "built-in" rows from disk - only the in-memory canonical list is authoritative.
            if (p.BuiltIn) continue;
            // Drop rows colliding with a built-in id, defensively.
            if (builtIns.Any(b => b.Id == p.Id)) continue;
            // Normalize audio shape on every load so legacy custom policies don't leak into the new pipeline.
            p.Options ??= new EncoderOptions();
            p.Options.ApplyLegacyAudioMigration();
            // Defensive scrub: any policy persisted before the machine-local rule
            // existed could still carry paths. Strip them on load too so the contract
            // ("policies never carry paths") holds regardless of file history.
            p.Options.ClearMachineLocalFields();
            seeded.Policies.Add(p);
        }

        // Persist if either (a) the file didn't exist, or (b) the on-disk shape differs
        // from what we just produced.
        var loadedJson = JsonSerializer.Serialize(loaded);
        var seededJson = JsonSerializer.Serialize(seeded);
        if (loadedJson != seededJson)
            _configFiles.Save(FileName, seeded);

        return seeded;
    }

    private void Save(PolicyDocument doc) => _configFiles.Save(FileName, doc);

    /// <summary>
    ///     Returns <paramref name="desired"/> when no policy in <paramref name="existing"/>
    ///     has that name. Otherwise appends <c>" (imported)"</c> (when importing) or
    ///     <c>" (copy)"</c> (otherwise), with a numeric suffix to break ties.
    /// </summary>
    private static string MakeUniqueName(List<Policy> existing, string desired, bool markImported = false)
    {
        if (!existing.Any(p => string.Equals(p.Name, desired, StringComparison.OrdinalIgnoreCase)))
            return desired;

        var suffix    = markImported ? " (imported)" : " (copy)";
        var candidate = desired + suffix;
        var counter   = 2;
        while (existing.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{desired}{suffix} {counter}";
            counter++;
        }
        return candidate;
    }
}
