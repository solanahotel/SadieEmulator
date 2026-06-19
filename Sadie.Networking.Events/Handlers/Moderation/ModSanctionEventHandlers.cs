using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Commands;
using Sadie.Networking.Writers.Players;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Moderation;

// Sanction actions sent from the mod tool's user / ticket view. Each targets a player by id with
// a free-text message and a CFH topic. Reuses the existing kick/ban mechanics (room boot +
// player_bans), the same the chat commands use.

internal static class SanctionHelpers
{
    public static bool IsMod(INetworkClient client) =>
        client.Player != null && client.Player.HasPermission("moderator");

    public static async Task AlertAsync(IPlayerLogic? target, string message)
    {
        if (target?.NetworkObject != null && !string.IsNullOrWhiteSpace(message))
        {
            await target.NetworkObject.WriteToStreamAsync(new PlayerAlertWriter { Message = message });
        }
    }

    public static async Task BootAsync(IRoomRepository roomRepository, IPlayerLogic target)
    {
        var room = roomRepository.TryGetRoomById(target.State.CurrentRoomId);
        if (room != null)
        {
            await room.UserRepository.TryRemoveAsync(target.Id, notifyLeft: true, hotelView: true);
        }
    }
}

// Alert (MODTOOL_SANCTION_ALERT = 229): pops a message on the target's screen.
[PacketId(229)]
public class ModAlertEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int UserId { get; set; }
    public string Message { get; set; } = "";
    public int CfhTopicId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (!SanctionHelpers.IsMod(client)) return;
        await SanctionHelpers.AlertAsync(playerRepository.GetPlayerLogicById(UserId), Message);
    }
}

// Message / caution (MODTOOL_ALERTEVENT = 1840): wire is userId, message, "", "", topicId.
[PacketId(1840)]
public class ModMessageEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int UserId { get; set; }
    public string Message { get; set; } = "";

    public async Task HandleAsync(INetworkClient client)
    {
        if (!SanctionHelpers.IsMod(client)) return;
        await SanctionHelpers.AlertAsync(playerRepository.GetPlayerLogicById(UserId), Message);
    }
}

// Default sanction (DEFAULT_SANCTION = 1681): wire is userId, topicIndex, message.
[PacketId(1681)]
public class DefaultSanctionEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int UserId { get; set; }
    public int TopicIndex { get; set; }
    public string Message { get; set; } = "";

    public async Task HandleAsync(INetworkClient client)
    {
        if (!SanctionHelpers.IsMod(client)) return;
        await SanctionHelpers.AlertAsync(playerRepository.GetPlayerLogicById(UserId), Message);
    }
}

// Mute 1h (MODTOOL_SANCTION_MUTE = 1945): global timed chat mute (duration is fixed by the action).
[PacketId(1945)]
public class ModMuteEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int UserId { get; set; }
    public string Message { get; set; } = "";
    public int CfhTopicId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (!SanctionHelpers.IsMod(client)) return;

        GlobalMuteStore.Mute(UserId, TimeSpan.FromHours(1));
        await SanctionHelpers.AlertAsync(playerRepository.GetPlayerLogicById(UserId),
            string.IsNullOrWhiteSpace(Message) ? "You have been muted for 1 hour." : Message);
    }
}

// Kick (MODTOOL_SANCTION_KICK = 2582): boot the target out of their room.
[PacketId(2582)]
public class ModKickEventHandler(IPlayerRepository playerRepository, IRoomRepository roomRepository) : INetworkPacketEventHandler
{
    public int UserId { get; set; }
    public string Message { get; set; } = "";
    public int CfhTopicId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (!SanctionHelpers.IsMod(client)) return;

        var target = playerRepository.GetPlayerLogicById(UserId);
        if (target == null) return;

        await SanctionHelpers.AlertAsync(target, Message);
        await SanctionHelpers.BootAsync(roomRepository, target);
    }
}

// Ban (MODTOOL_SANCTION_BAN = 2766): wire is userId, message, topicId, sanctionIndex, avatarOnly.
// sanctionIndex is the mod-tool dropdown index, which maps to a ban length.
[PacketId(2766)]
public class ModBanEventHandler(
    IPlayerRepository playerRepository,
    IRoomRepository roomRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int UserId { get; set; }
    public string Message { get; set; } = "";
    public int CfhTopicId { get; set; }
    public int SanctionIndex { get; set; }
    public bool AvatarOnly { get; set; }

    // Dropdown indices: 2=Ban 18h, 3=Ban 7d, 4/5=Ban 30d, 6/7=permanent.
    private static int? MapBanHours(int index) => index switch
    {
        2 => 18,
        3 => 168,
        4 => 720,
        5 => 720,
        _ => null
    };

    public async Task HandleAsync(INetworkClient client)
    {
        if (!SanctionHelpers.IsMod(client) || client.Player == null) return;

        var hours = MapBanHours(SanctionIndex);
        var reason = string.IsNullOrWhiteSpace(Message) ? "Banned via mod tool" : Message;
        var now = DateTime.Now;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        if (hours.HasValue)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_bans (creator_id, player_id, reason, created_at, expires_at) VALUES ({0}, {1}, {2}, {3}, {4})",
                client.Player.Id, UserId, reason, now, now.AddHours(hours.Value));
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_bans (creator_id, player_id, reason, created_at, expires_at) VALUES ({0}, {1}, {2}, {3}, NULL)",
                client.Player.Id, UserId, reason, now);
        }

        // Boot them now; the login check (SecureLoginEventHandler) blocks re-entry.
        var target = playerRepository.GetPlayerLogicById(UserId);
        if (target != null)
        {
            await SanctionHelpers.BootAsync(roomRepository, target);
        }
    }
}
