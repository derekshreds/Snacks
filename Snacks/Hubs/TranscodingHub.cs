using Microsoft.AspNetCore.SignalR;

namespace Snacks.Hubs;

/// <summary> SignalR hub for broadcasting transcoding progress updates to connected UI clients. </summary>
public class TranscodingHub : Hub
{
    /******************************************************************
     *  Group Management
     ******************************************************************/

    /// <summary>
    ///     Adds the current connection to a SignalR group so it receives group-targeted broadcasts.
    /// </summary>
    /// <param name="groupName"> The name of the group to join. </param>
    /// <returns> A task that completes when the connection has been added to the group. </returns>
    public async Task JoinGroupAsync(string groupName)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    /// <summary>
    ///     Removes the current connection from a SignalR group so it no longer receives group-targeted broadcasts.
    /// </summary>
    /// <param name="groupName"> The name of the group to leave. </param>
    /// <returns> A task that completes when the connection has been removed from the group. </returns>
    public async Task LeaveGroupAsync(string groupName)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}