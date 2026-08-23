using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using Snacks.Services;
using Snacks.Services.Slots;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Pins the scheduler's dispatch-failure recovery: a throw between "item left the
///     queue" and "job task spawned" must release the slot reservation and requeue the
///     item (the historical leak held a capacity-1 device slot until app restart), and
///     the orphan-reservation reaper must free any local reservation with no backing
///     active job — without ever touching live encodes or remote reservations.
/// </summary>
public sealed class DispatchRecoveryTests : IDisposable
{
    private readonly InMemoryDb _db = new();

    public void Dispose() => _db.Dispose();

    private TranscodingService NewService(out SlotLedger ledger)
    {
        var svc = new TranscodingService(
            new FileService(), new FfprobeService(), new NullHubContext(), _db.CreateRepository());
        ledger = new SlotLedger((_, _) => 1, _ => { });
        svc.SetSlotLedger(ledger);
        return svc;
    }

    private static WorkItem Item(WorkItemStatus status = WorkItemStatus.Pending) => new()
    {
        FileName = "a.mkv",
        Path     = "/m/a.mkv",
        Status   = status,
        Kind     = MediaKind.Video,
    };


    /******************************************************************
     *  RecoverFailedLocalDispatch
     ******************************************************************/

    [Fact]
    public void Recovery_releases_the_reservation_and_requeues_a_pending_item()
    {
        var svc  = NewService(out var ledger);
        var item = Item();
        ledger.TryReserve("master-local", "amd", item.Id, item.FileName).Should().BeTrue();

        svc.RecoverFailedLocalDispatch(item, new InvalidOperationException("boom"));

        ledger.Contains(item.Id).Should().BeFalse();
        ledger.UsedDeviceSlots("master-local", "amd").Should().Be(0);
        svc.GetQueueWindowSnapshot().Should().ContainSingle(w => w.Id == item.Id);
    }


    [Fact]
    public void Recovery_before_any_reservation_is_a_safe_noop_release()
    {
        var svc  = NewService(out var ledger);
        var item = Item();

        svc.RecoverFailedLocalDispatch(item, new InvalidOperationException("boom"));

        ledger.Contains(item.Id).Should().BeFalse();
        svc.GetQueueWindowSnapshot().Should().ContainSingle(w => w.Id == item.Id);
    }


    [Fact]
    public void Recovery_does_not_requeue_an_item_that_went_terminal_mid_dispatch()
    {
        var svc  = NewService(out var ledger);
        var item = Item(WorkItemStatus.Cancelled);
        ledger.TryReserve("master-local", "amd", item.Id, item.FileName).Should().BeTrue();

        svc.RecoverFailedLocalDispatch(item, new InvalidOperationException("boom"));

        ledger.Contains(item.Id).Should().BeFalse();
        svc.GetQueueWindowSnapshot().Should().BeEmpty();
    }


    /******************************************************************
     *  ReapOrphanedLocalReservations
     ******************************************************************/

    [Fact]
    public void Reaper_releases_an_unbacked_local_reservation_past_the_grace_window()
    {
        var svc = NewService(out var ledger);
        ledger.TryReserve("master-local", "amd", "leaked-job", "a.mkv").Should().BeTrue();

        // Negative grace = "everything is past the window" without sleeping.
        svc.ReapOrphanedLocalReservations(TimeSpan.FromSeconds(-1)).Should().Be(1);

        ledger.Contains("leaked-job").Should().BeFalse();
        ledger.UsedDeviceSlots("master-local", "amd").Should().Be(0);
    }


    [Fact]
    public void Reaper_keeps_reservations_backed_by_an_active_job()
    {
        var svc  = NewService(out var ledger);
        var item = Item();
        ledger.TryReserve("master-local", "amd", item.Id, item.FileName).Should().BeTrue();
        svc.RegisterActiveLocalJobForTest(item, "amd");

        svc.ReapOrphanedLocalReservations(TimeSpan.FromSeconds(-1)).Should().Be(0);

        ledger.Contains(item.Id).Should().BeTrue();
    }


    [Fact]
    public void Reaper_keeps_reservations_younger_than_the_grace_window()
    {
        // Covers the reserve→register dispatch gap: a fresh reservation whose job
        // task hasn't spawned yet must never be reaped.
        var svc = NewService(out var ledger);
        ledger.TryReserve("master-local", "amd", "in-dispatch", "a.mkv").Should().BeTrue();

        svc.ReapOrphanedLocalReservations(TimeSpan.FromMinutes(2)).Should().Be(0);

        ledger.Contains("in-dispatch").Should().BeTrue();
    }


    [Fact]
    public void Reaper_never_touches_remote_reservations()
    {
        var svc = NewService(out var ledger);
        ledger.TryReserve("worker-1", "nvidia", "remote-job", "b.mkv").Should().BeTrue();

        svc.ReapOrphanedLocalReservations(TimeSpan.FromSeconds(-1)).Should().Be(0);

        ledger.Contains("remote-job").Should().BeTrue();
    }


    /******************************************************************
     *  FinaliseForDispatchAsync — the scheduler call-site contract.
     ******************************************************************/

    [Fact]
    public async Task Finalise_with_a_timing_out_integration_does_not_throw()
    {
        // Pins the exact scheduler call shape: KeepOriginalLanguage on, a provider
        // whose HTTP layer times out, and CancellationToken.None. The lookup must
        // degrade to "no original language" instead of unwinding the dispatcher.
        var priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        var workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"snacks-finalise-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", workDir);
        try
        {
            var integration = new IntegrationService(
                new ConfigFileService(new FileService()), new ThrowingFactory());
            integration.SaveConfig(new IntegrationConfig
            {
                Sonarr = new ArrIntegration { Enabled = true, BaseUrl = "http://sonarr.test:8989", ApiKey = "k" },
            });

            var svc = new TranscodingService(
                new FileService(), new FfprobeService(), new NullHubContext(), _db.CreateRepository(),
                integrationService: integration);

            var item = new WorkItem
            {
                FileName = "Some Show - S01E01.mkv",
                Path     = "/tv/Some Show (2020)/Season 01/Some Show - S01E01.mkv",
                Status   = WorkItemStatus.Pending,
                Kind     = MediaKind.Video,
            };
            var options = new EncoderOptions { KeepOriginalLanguage = true, OriginalLanguageProvider = "sonarr" };

            var act = () => svc.FinaliseForDispatchAsync(item, options, CancellationToken.None);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", priorWorkDir);
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private sealed class ThrowingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler(), disposeHandler: false);

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new TaskCanceledException("timed out");
        }
    }


}
