using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Enums.Game.Players;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Moderation;

[PacketId(EventHandlerId.ModToolsRoomChatLog)]
public class ModToolGetRoomChatLogEventHandler(
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null ||
            client.RoomUser == null ||
            !client.Player.HasPermission(PlayerPermissionName.Moderator))
        {
            return;
        }

        var room = client.RoomUser.Room;

        var messages = room
            .ChatMessages
            .TakeLast(150)
            .ToList();

        // Resolve usernames by player id: online users first, then any already-loaded
        // navigation, then a single DB lookup for chatters who have since left the room.
        var usernamesById = new Dictionary<long, string>();

        foreach (var roomUser in room.UserRepository.GetAll())
        {
            usernamesById[roomUser.Player.Id] = roomUser.Player.Username;
        }

        foreach (var message in messages)
        {
            if (message.Player != null && !usernamesById.ContainsKey(message.PlayerId))
            {
                usernamesById[message.PlayerId] = message.Player.Username;
            }
        }

        var missingIds = messages
            .Select(x => x.PlayerId)
            .Distinct()
            .Where(id => !usernamesById.ContainsKey(id))
            .ToList();

        if (missingIds.Count > 0)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();

            var rows = await dbContext.Players
                .Where(x => missingIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Username })
                .ToListAsync();

            foreach (var row in rows)
            {
                usernamesById[row.Id] = row.Username;
            }
        }

        await client.WriteToStreamAsync(new FixedRoomChatLogWriter
        {
            RoomId = room.Id,
            RoomName = room.Name,
            Messages = messages,
            UsernamesById = usernamesById
        });
    }
}