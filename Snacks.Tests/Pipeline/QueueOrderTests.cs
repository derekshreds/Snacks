using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Pins the canonical pending-queue ordering used by every
///     <c>_workQueue.Sort</c> site and the queue API listing: user priority
///     ("move to front") wins over the default bitrate-descending order.
/// </summary>
public sealed class QueueOrderTests
{
    private static WorkItem Item(long bitrate, int priority = 0) =>
        new() { Bitrate = bitrate, Priority = priority };

    [Fact]
    public void Default_order_is_bitrate_descending()
    {
        var items = new List<WorkItem> { Item(1000), Item(9000), Item(4000) };
        items.Sort(TranscodingService.CompareQueueOrder);
        items.Select(i => i.Bitrate).Should().Equal(9000, 4000, 1000);
    }

    [Fact]
    public void Prioritized_item_sorts_ahead_of_higher_bitrate_items()
    {
        var front = Item(500, priority: 1);
        var items = new List<WorkItem> { Item(9000), front, Item(4000) };
        items.Sort(TranscodingService.CompareQueueOrder);
        items[0].Should().BeSameAs(front);
    }

    [Fact]
    public void Later_prioritization_outranks_earlier_one()
    {
        var first  = Item(500, priority: 1);
        var second = Item(300, priority: 2);
        var items  = new List<WorkItem> { first, Item(9000), second };
        items.Sort(TranscodingService.CompareQueueOrder);
        items.Take(2).Should().Equal(second, first);
    }
}
