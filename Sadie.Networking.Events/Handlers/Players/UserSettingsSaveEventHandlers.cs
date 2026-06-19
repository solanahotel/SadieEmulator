using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Players;

// Save the toggles/sliders from the settings cog. The client sends these on every change,
// but the server had no handlers, so the changes were dropped (reset on relog). Each persists
// to player_game_settings (PlayerGameSettings is init-only in memory, so we write straight to
// the DB; the client already reflects the change locally, and it loads correctly next login).

[PacketId(1262)] // USER_SETTINGS_OLD_CHAT
public class UserSettingsOldChatEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public bool Value { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_game_settings SET prefer_old_chat = {0} WHERE player_id = {1}", Value, client.Player.Id);
    }
}

[PacketId(1086)] // USER_SETTINGS_INVITES
public class UserSettingsRoomInvitesEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public bool Value { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_game_settings SET block_room_invites = {0} WHERE player_id = {1}", Value, client.Player.Id);
    }
}

[PacketId(1461)] // USER_SETTINGS_CAMERA
public class UserSettingsCameraFollowEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public bool Value { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_game_settings SET block_camera_follow = {0} WHERE player_id = {1}", Value, client.Player.Id);
    }
}

[PacketId(1367)] // USER_SETTINGS_VOLUME
public class UserSettingsVolumeEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int SystemVolume { get; set; }
    public int FurnitureVolume { get; set; }
    public int TraxVolume { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_game_settings SET system_volume = {0}, furniture_volume = {1}, trax_volume = {2} WHERE player_id = {3}",
            SystemVolume, FurnitureVolume, TraxVolume, client.Player.Id);
    }
}
