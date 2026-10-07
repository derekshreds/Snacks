using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Pins music output placement. Music has its own delete-original and output-directory
///     settings, and placement must honor those — not the video "Replace Original Files"
///     toggle — so a replaced track lands under its original name with no <c>[snacks]</c> tag.
///     Driven through <see cref="TranscodingService.HandleRemoteCompletion"/> (the master's
///     post-download step), which shares the placement code with the local music path.
///     In the EnvConfigOverrides collection because it mutates SNACKS_WORK_DIR.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class MusicOutputPlacementTests : IDisposable
{
    private readonly InMemoryDb _db = new();
    private readonly string     _root;
    private readonly string     _albumDir;
    private readonly string     _stagingDir;
    private readonly string?    _priorWorkDir;

    public MusicOutputPlacementTests()
    {
        _root       = Path.Combine(Path.GetTempPath(), $"snacks-music-place-{Guid.NewGuid():N}");
        _albumDir   = Path.Combine(_root, "library", "Music", "Some Artist", "Some Album");
        _stagingDir = Path.Combine(_root, "encode");
        Directory.CreateDirectory(_albumDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", Path.Combine(_root, "work"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TranscodingService NewService() =>
        new(new FileService(), new FfprobeService(), new NullHubContext(), _db.CreateRepository());

    /// <summary> Writes a source track and its smaller staged encode; returns the work item and output path. </summary>
    private (WorkItem item, string outputPath) Stage(string sourceName, string outputExt, string? outputDir = null)
    {
        var sourcePath = Path.Combine(_albumDir, sourceName);
        File.WriteAllBytes(sourcePath, new byte[4096]);

        var baseName   = Path.GetFileNameWithoutExtension(sourceName);
        var outputPath = Path.Combine(outputDir ?? _stagingDir, $"{baseName} [snacks]{outputExt}");
        File.WriteAllText(outputPath, "encoded");

        var item = new WorkItem
        {
            FileName = sourceName,
            Path     = sourcePath,
            Size     = 4096,
            Kind     = MediaKind.Music,
            Status   = WorkItemStatus.Processing,
        };
        return (item, outputPath);
    }


    [Fact]
    public async Task Music_delete_original_replaces_source_under_its_clean_name()
    {
        var (item, output) = Stage("01 - Song.flac", ".m4a");
        var options = new EncoderOptions { DeleteOriginalFile = false };
        options.Music.DeleteOriginalFile = true;

        await NewService().HandleRemoteCompletion(item, output, options);

        var final = Path.Combine(_albumDir, "01 - Song.m4a");
        File.ReadAllText(final).Should().Be("encoded");
        File.Exists(item.Path).Should().BeFalse("the original is replaced");
        File.Exists(output).Should().BeFalse();
        Directory.GetFiles(_albumDir).Should().Equal(final);
        item.Status.Should().Be(WorkItemStatus.Completed);
    }


    [Fact]
    public async Task Same_extension_replace_overwrites_the_source_path()
    {
        var (item, output) = Stage("02 - Song.mp3", ".mp3");
        var options = new EncoderOptions();
        options.Music.DeleteOriginalFile = true;

        await NewService().HandleRemoteCompletion(item, output, options);

        File.ReadAllText(item.Path).Should().Be("encoded");
        Directory.GetFiles(_albumDir).Should().Equal(item.Path);
    }


    [Fact]
    public async Task Video_replace_setting_does_not_delete_music_originals()
    {
        // Before the fix the cluster path ran video placement for music, so the video
        // toggle replaced the track while the music toggle was off.
        var (item, output) = Stage("03 - Song.flac", ".m4a");
        var options = new EncoderOptions { DeleteOriginalFile = true };
        options.Music.DeleteOriginalFile = false;

        await NewService().HandleRemoteCompletion(item, output, options);

        File.Exists(item.Path).Should().BeTrue();
        var kept = Path.Combine(_albumDir, "03 - Song [snacks].m4a");
        File.ReadAllText(kept).Should().Be("encoded", "a staged keep-both output still moves beside its original");
        File.Exists(output).Should().BeFalse();
    }


    [Fact]
    public async Task Music_replace_lands_in_place_even_with_an_output_directory()
    {
        // Same as video: when replacing, the output directory only stages the encode. The
        // track stays in its album folder, where Lidarr/Plex/Jellyfin expect it.
        var musicOut = Path.Combine(_root, "music-out");
        Directory.CreateDirectory(musicOut);
        var (item, output) = Stage("04 - Song.flac", ".m4a", outputDir: musicOut);
        var options = new EncoderOptions { OutputDirectory = Path.Combine(_root, "video-out") };
        options.Music.DeleteOriginalFile = true;
        options.Music.OutputDirectory    = musicOut;

        await NewService().HandleRemoteCompletion(item, output, options);

        File.ReadAllText(Path.Combine(_albumDir, "04 - Song.m4a")).Should().Be("encoded");
        File.Exists(item.Path).Should().BeFalse();
        Directory.GetFiles(musicOut).Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "video-out")).Should().BeFalse();
    }


    [Fact]
    public async Task Music_keep_both_uses_the_output_directory()
    {
        var musicOut = Path.Combine(_root, "music-out");
        var (item, output) = Stage("05 - Song.flac", ".m4a");
        var options = new EncoderOptions();
        options.Music.DeleteOriginalFile = false;
        options.Music.OutputDirectory    = musicOut;

        await NewService().HandleRemoteCompletion(item, output, options);

        File.ReadAllText(Path.Combine(musicOut, "05 - Song [snacks].m4a")).Should().Be("encoded");
        File.Exists(item.Path).Should().BeTrue();
    }
}
