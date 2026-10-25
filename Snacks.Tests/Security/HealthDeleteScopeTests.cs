using FluentAssertions;
using Snacks.Models;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>
///     The single-file health delete may only remove files the Library Health page lists —
///     not any media file under the library root.
/// </summary>
public sealed class HealthDeleteScopeTests
{
    private static MediaFile Row(string path, MediaFileStatus status) => new()
    {
        FilePath        = path,
        Directory       = "/lib",
        FileName        = Path.GetFileName(path),
        BaseName        = Path.GetFileNameWithoutExtension(path),
        Codec           = "h264",
        Width           = 1920,
        Height          = 1080,
        Duration        = 120,
        AudioStreams    = "[{\"l\":\"eng\",\"c\":\"aac\",\"ch\":2}]",
        SubtitleStreams = "[]",
        Status          = status,
        LastScannedAt   = DateTime.UtcNow,
    };

    [Fact]
    public async Task Only_files_listed_on_the_health_page_count_as_flagged()
    {
        using var db = new InMemoryDb();
        var repo = db.CreateRepository();
        await repo.UpsertAsync(Row("/lib/broken.mkv", MediaFileStatus.Failed));
        await repo.UpsertAsync(Row("/lib/healthy.mkv", MediaFileStatus.Completed));

        (await repo.IsHealthFlaggedAsync("/lib/broken.mkv")).Should().BeTrue();
        (await repo.IsHealthFlaggedAsync("/lib/healthy.mkv")).Should().BeFalse();
        (await repo.IsHealthFlaggedAsync("/lib/not-in-library.mkv")).Should().BeFalse();
    }
}
