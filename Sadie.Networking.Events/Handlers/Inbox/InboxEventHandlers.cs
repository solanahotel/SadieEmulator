using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Inbox;

// Persistent inbox (player_inbox). 9006 = fetch messages, 9007 = mark all read.

[PacketId(9006)]
public class InboxGetEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var messages = await InboxService.GetMessagesAsync(dbContextFactory, client.Player.Id);
        var unread = await InboxService.GetUnreadCountAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new InboxMessagesWriter { Messages = messages });
        await client.WriteToStreamAsync(new InboxUnreadCountWriter { Count = unread });
    }
}

[PacketId(9007)]
public class InboxMarkReadEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        await InboxService.MarkAllReadAsync(dbContextFactory, client.Player.Id);
        var messages = await InboxService.GetMessagesAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new InboxMessagesWriter { Messages = messages });
        await client.WriteToStreamAsync(new InboxUnreadCountWriter { Count = 0 });
    }
}
