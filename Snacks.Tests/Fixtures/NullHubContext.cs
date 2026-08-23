using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;

namespace Snacks.Tests.Fixtures;

/// <summary>
///     No-op <see cref="IHubContext{TranscodingHub}"/> for tests that construct a
///     <see cref="Snacks.Services.TranscodingService"/> but don't care about SignalR
///     broadcasts. Shared here so each test class doesn't carry its own copy.
/// </summary>
internal sealed class NullHubContext : IHubContext<TranscodingHub>
{
    public IHubClients Clients { get; } = new NullClients();
    public IGroupManager Groups { get; } = new NullGroups();

    private sealed class NullClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NullProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
