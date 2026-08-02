using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;

namespace Snacks.Services;

/// <summary>
///     Environment-variable override layer for the JSON config files. Values are applied
///     in-memory after every deserialize and are never written back to disk, so removing
///     an env var cleanly reverts to the file's value. Naming:
///     <list type="bullet">
///         <item><c>SNACKS_SET_&lt;Prop&gt;</c> → settings.json (<see cref="Models.EncoderOptions"/>), nested via <c>__</c> (e.g. <c>SNACKS_SET_Music__BitrateKbps</c>)</item>
///         <item><c>SNACKS_SCAN_&lt;Prop&gt;</c> → autoscan.json (<see cref="Models.AutoScanConfig"/>)</item>
///         <item><c>SNACKS_INTEG_&lt;Section&gt;__&lt;Prop&gt;</c> → integrations.json (<see cref="Models.IntegrationConfig"/>)</item>
///     </list>
///     Segments match CLR property names case-insensitively. Scalars parse per type
///     (bools accept true/false/1/0/yes/no/on/off; enums by name); string lists accept
///     comma-separated or JSON; other complex types take a JSON value. Invalid or unknown
///     variables log a warning once and are skipped — startup never fails on a bad override.
/// </summary>
public static class EnvConfigOverrides
{
    public const string SettingsPrefix     = "SNACKS_SET_";
    public const string AutoScanPrefix     = "SNACKS_SCAN_";
    public const string IntegrationsPrefix = "SNACKS_INTEG_";

    private static readonly string[] AllPrefixes = { SettingsPrefix, AutoScanPrefix, IntegrationsPrefix };

    /// <summary>
    ///     Properties that must never be env-driven, as lowercase dotted CLR paths per prefix.
    ///     HardwareDevicePath is per-dispatch ephemeral state; the auto-scan trio is runtime
    ///     state — QueuePaused in particular must stay controllable via POST /api/queue/paused.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> Denylist = new()
    {
        [SettingsPrefix]     = new() { "hardwaredevicepath" },
        [AutoScanPrefix]     = new() { "lastscantime", "lastscannewfiles", "queuepaused" },
        [IntegrationsPrefix] = new(),
    };

    private static readonly object _sync = new();
    private static IReadOnlyDictionary<string, string>? _envForTesting;
    private static Dictionary<string, string>? _snapshot;
    private static readonly Dictionary<(string Prefix, Type Type), List<Entry>> _planCache = new();
    private static readonly HashSet<string> _warned = new();

    /// <summary> One resolved, parse-validated override: the property chain plus the raw env value. </summary>
    private sealed class Entry
    {
        public required string         EnvName   { get; init; }
        public required PropertyInfo[] Chain     { get; init; }
        public required string         CamelPath { get; init; }
        public required string         RawValue  { get; init; }
    }

    /******************************************************************
     *  Public surface
     ******************************************************************/

    /// <summary> Applies every matching env override to <paramref name="target"/> in place and returns it. </summary>
    public static T Apply<T>(T target, string prefix) where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (var entry in GetPlan(prefix, typeof(T)))
        {
            // Values are re-converted per call so mutable instances (lists, nested
            // objects) are never shared between independently-applied targets.
            if (!TryConvert(entry.Chain[^1], entry.RawValue, out var value, out _)) continue;

            var parent = WalkToParent(target, entry.Chain, createMissing: true);
            if (parent == null) continue;
            entry.Chain[^1].SetValue(parent, value);
        }
        return target;
    }

    /// <summary>
    ///     camelCase dotted paths of the validly-overridden properties for <paramref name="modelType"/>
    ///     (e.g. <c>["codec", "music.bitrateKbps"]</c>) — consumed by the UI as <c>_envLocked</c>.
    /// </summary>
    public static IReadOnlyList<string> LockedPaths(string prefix, Type modelType)
        => GetPlan(prefix, modelType).Select(e => e.CamelPath).ToList();

    /// <summary>
    ///     Removes env-locked keys (case-insensitive, nested) from an incoming save payload
    ///     so config files never absorb env-driven values.
    /// </summary>
    public static void StripLockedPaths(JsonObject incoming, string prefix, Type modelType)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        foreach (var entry in GetPlan(prefix, modelType))
        {
            var segments = entry.CamelPath.Split('.');
            var node     = incoming;
            for (var i = 0; i < segments.Length - 1 && node != null; i++)
                node = FindKey(node, segments[i]) is { } key ? node[key] as JsonObject : null;

            if (node != null && FindKey(node, segments[^1]) is { } leaf)
                node.Remove(leaf);
        }
    }

    /// <summary>
    ///     Copies env-locked property values from <paramref name="fileState"/> (a raw,
    ///     un-overridden load of the config file) onto <paramref name="incoming"/> before
    ///     it is persisted — the typed equivalent of <see cref="StripLockedPaths"/>.
    /// </summary>
    public static void RestoreLockedValues<T>(T incoming, T fileState, string prefix) where T : class
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(fileState);
        foreach (var entry in GetPlan(prefix, typeof(T)))
        {
            var source = WalkToParent(fileState, entry.Chain, createMissing: false);
            var dest   = WalkToParent(incoming,  entry.Chain, createMissing: true);
            if (source == null || dest == null) continue;
            entry.Chain[^1].SetValue(dest, entry.Chain[^1].GetValue(source));
        }
    }

    /// <summary> Test seam — replaces the env snapshot (<see langword="null"/> = re-read the real environment). </summary>
    internal static void SetEnvironmentForTesting(IReadOnlyDictionary<string, string>? env)
    {
        lock (_sync)
        {
            _envForTesting = env;
            _snapshot      = null;
            _planCache.Clear();
            _warned.Clear();
        }
    }

    /******************************************************************
     *  Plan resolution
     ******************************************************************/

    /// <summary> Resolved override plan for a (prefix, model) pair; cached — env is process-constant. </summary>
    private static List<Entry> GetPlan(string prefix, Type modelType)
    {
        lock (_sync)
        {
            if (_planCache.TryGetValue((prefix, modelType), out var cached)) return cached;

            var plan = new List<Entry>();
            foreach (var (name, raw) in Snapshot())
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var segments = name[prefix.Length..].Split("__", StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) continue;

                if (!TryResolveChain(modelType, segments, out var chain))
                {
                    WarnOnce(name, $"no property matches '{string.Join(".", segments)}' on {modelType.Name}");
                    continue;
                }

                var clrPath = string.Join(".", chain.Select(p => p.Name)).ToLowerInvariant();
                if (Denylist.TryGetValue(prefix, out var denied) && denied.Contains(clrPath))
                {
                    WarnOnce(name, "this property is runtime state and cannot be set from the environment");
                    continue;
                }

                if (!TryConvert(chain[^1], raw, out _, out var error))
                {
                    WarnOnce(name, $"cannot parse '{raw}' as {chain[^1].PropertyType.Name}: {error}");
                    continue;
                }

                plan.Add(new Entry
                {
                    EnvName   = name,
                    Chain     = chain,
                    CamelPath = string.Join(".", chain.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))),
                    RawValue  = raw,
                });
            }

            _planCache[(prefix, modelType)] = plan;
            return plan;
        }
    }

    private static Dictionary<string, string> Snapshot()
    {
        if (_snapshot != null) return _snapshot;

        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_envForTesting != null)
        {
            foreach (var (k, v) in _envForTesting) snap[k] = v;
        }
        else
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var key = e.Key as string;
                if (key != null && AllPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                    snap[key] = e.Value as string ?? "";
            }
        }
        _snapshot = snap;
        return snap;
    }

    private static bool TryResolveChain(Type root, string[] segments, out PropertyInfo[] chain)
    {
        var result  = new PropertyInfo[segments.Length];
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var prop = current.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .FirstOrDefault(p => string.Equals(p.Name, segments[i], StringComparison.OrdinalIgnoreCase)
                                                   && p.CanRead
                                                   && (i < segments.Length - 1 || p.CanWrite));
            if (prop == null) { chain = Array.Empty<PropertyInfo>(); return false; }
            result[i] = prop;
            current   = prop.PropertyType;
        }
        chain = result;
        return true;
    }

    /// <summary> Walks the chain to the object holding the leaf property, instantiating missing intermediates when asked. </summary>
    private static object? WalkToParent(object target, PropertyInfo[] chain, bool createMissing)
    {
        var current = target;
        for (var i = 0; i < chain.Length - 1; i++)
        {
            var next = chain[i].GetValue(current);
            if (next == null)
            {
                if (!createMissing || !chain[i].CanWrite) return null;
                next = Activator.CreateInstance(chain[i].PropertyType);
                if (next == null) return null;
                chain[i].SetValue(current, next);
            }
            current = next;
        }
        return current;
    }

    /******************************************************************
     *  Value conversion
     ******************************************************************/

    private static bool TryConvert(PropertyInfo leaf, string raw, out object? value, out string? error)
    {
        value = null;
        error = null;
        var type = Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType;

        try
        {
            if (type == typeof(string)) { value = raw; return true; }

            if (type == typeof(bool))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "true" or "1" or "yes" or "on":   value = true;  return true;
                    case "false" or "0" or "no" or "off":  value = false; return true;
                    default: error = "expected true/false/1/0/yes/no/on/off"; return false;
                }
            }

            if (type.IsEnum)
            {
                if (Enum.TryParse(type, raw.Trim(), ignoreCase: true, out var parsed)) { value = parsed; return true; }
                error = $"expected one of {string.Join(", ", Enum.GetNames(type))}";
                return false;
            }

            if (type == typeof(int) || type == typeof(long) || type == typeof(double)
                || type == typeof(float) || type == typeof(decimal) || type == typeof(DateTime))
            {
                value = Convert.ChangeType(raw.Trim(), type, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            if (type == typeof(List<string>))
            {
                value = raw.TrimStart().StartsWith('[')
                    ? JsonSerializer.Deserialize<List<string>>(raw) ?? new List<string>()
                    : raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                return true;
            }

            // Any other complex type takes a JSON value. Honor a property-level
            // [JsonConverter] (e.g. WatchedFolderListConverter promotes plain strings).
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            if (leaf.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType is { } converterType
                && Activator.CreateInstance(converterType) is JsonConverter converter)
            {
                options.Converters.Add(converter);
            }
            value = JsonSerializer.Deserialize(raw, type, options);
            if (value == null) { error = "JSON value deserialized to null"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    private static string? FindKey(JsonObject node, string name)
        => node.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static void WarnOnce(string envName, string reason)
    {
        if (!_warned.Add(envName)) return;
        Log.Warning("Ignoring environment override {EnvVar}: {Reason}", envName, reason);
    }
}
