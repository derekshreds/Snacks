using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Loads, persists, and broadcasts changes to <see cref="NetworkingSettings"/>.
///     Disk file lives at <c>{workdir}/config/networking.json</c>. Settings are
///     read fresh from memory on every consumer call so a save takes effect on
///     the next chunk acquire — no service restart needed.
/// </summary>
public sealed class NetworkingSettingsService
{
    private readonly Func<string> _workDirResolver;
    private readonly object _writeLock = new();
    private NetworkingSettings _current;

    /// <summary>
    ///     Fired after a successful <see cref="Save"/> so consumers (the
    ///     transfer throttle) can rebuild rate-limiters with the new caps.
    /// </summary>
    public event Action<NetworkingSettings>? Changed;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented              = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    ///     DI constructor. Resolves the working directory through
    ///     <see cref="FileService"/> so the config file lives next to all
    ///     the others.
    /// </summary>
    public NetworkingSettingsService(FileService fileService)
        : this(() => fileService.GetWorkingDirectory()) { }

    /// <summary>
    ///     Test/embedded constructor accepting any working-directory resolver.
    ///     Lets unit tests point at a temp directory without subclassing the
    ///     sealed <see cref="FileService"/>.
    /// </summary>
    public NetworkingSettingsService(Func<string> workDirResolver)
    {
        _workDirResolver = workDirResolver;
        _current         = LoadFromDisk();
    }

    /// <summary> Snapshot of the current settings. Safe to read without locking. </summary>
    public NetworkingSettings Get() => _current;

    /// <summary>
    ///     Validates, atomically writes to disk, replaces the in-memory value,
    ///     and fires <see cref="Changed"/>. Throws on invalid input rather
    ///     than silently clamping so the UI surfaces validation errors.
    /// </summary>
    public void Save(NetworkingSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);

        lock (_writeLock)
        {
            var path   = GetPath();
            var tmp    = path + ".tmp";
            var json   = JsonSerializer.Serialize(value, _jsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, json);
            // Atomic replace: write tmp, then move-overwrite. POSIX rename is
            // atomic; on Windows .NET handles the equivalent via File.Move.
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else                   File.Move(tmp, path);
            _current = value;
        }

        try { Changed?.Invoke(value); }
        catch (Exception ex) { Console.WriteLine($"NetworkingSettings: Changed handler threw: {ex.Message}"); }
    }

    private static void Validate(NetworkingSettings v)
    {
        if (v.MaxConcurrentUploads             < 0) throw new ArgumentException("MaxConcurrentUploads must be >= 0");
        if (v.MaxConcurrentUploadsPerNode      < 0) throw new ArgumentException("MaxConcurrentUploadsPerNode must be >= 0");
        if (v.MaxConcurrentDownloads           < 0) throw new ArgumentException("MaxConcurrentDownloads must be >= 0");
        if (v.MaxConcurrentDownloadsPerNode    < 0) throw new ArgumentException("MaxConcurrentDownloadsPerNode must be >= 0");
        if (v.MaxUploadMBps                    < 0) throw new ArgumentException("MaxUploadMBps must be >= 0");
        if (v.MaxUploadMBpsPerNode             < 0) throw new ArgumentException("MaxUploadMBpsPerNode must be >= 0");
        if (v.MaxDownloadMBps                  < 0) throw new ArgumentException("MaxDownloadMBps must be >= 0");
        if (v.MaxDownloadMBpsPerNode           < 0) throw new ArgumentException("MaxDownloadMBpsPerNode must be >= 0");
        if (v.ChunkSizeMB < Cluster.TransferLimits.MinChunkSizeMB || v.ChunkSizeMB > Cluster.TransferLimits.MaxChunkSizeMB)
            throw new ArgumentException($"ChunkSizeMB must be between {Cluster.TransferLimits.MinChunkSizeMB} and {Cluster.TransferLimits.MaxChunkSizeMB}");
    }

    private NetworkingSettings LoadFromDisk()
    {
        var path = GetPath();
        if (!File.Exists(path)) return new NetworkingSettings();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NetworkingSettings>(json, _jsonOptions) ?? new NetworkingSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NetworkingSettings: Failed to load {path}: {ex.Message} — falling back to defaults");
            return new NetworkingSettings();
        }
    }

    private string GetPath()
    {
        var configDir = Path.Combine(_workDirResolver(), "config");
        return Path.Combine(configDir, "networking.json");
    }
}
