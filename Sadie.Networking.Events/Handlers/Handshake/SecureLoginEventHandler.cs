using System.Diagnostics;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sadie.API.Game.Players;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Db.Models.Constants;
using Sadie.Db.Models.Server;
using Sadie.Networking.Events.Achievements;
using Sadie.Networking.Events.Commands;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Events.Moderation;
using Sadie.Networking.Writers.Handshake;
using Sadie.Options.Options;
using Sadie.Shared.Attributes;
using Sadie.Shared;

namespace Sadie.Networking.Events.Handlers.Handshake;

[PacketId(EventHandlerId.SecureLogin)]
public class SecureLoginEventHandler(
    ILogger<SecureLoginEventHandler> logger,
    IOptions<EncryptionOptions> encryptionOptions,
    IPlayerRepository playerRepository,
    ServerPlayerConstants constants,
    INetworkClientRepository networkClientRepository,
    ServerSettings serverSettings,
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IMapper mapper,
    IPlayerLoaderService playerLoaderService,
    IPlayerHelperService playerHelperService)
    : INetworkPacketEventHandler
{
    public string? Token { get; set; }
    public int DelayMs { get; set; }
    
    public async Task HandleAsync(INetworkClient client)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(Token) || !ValidateSso(Token))
        {
            logger.LogWarning("Rejected an insecure sso token");
            await client.DisposeAsync();
            return;
        }
        
        if (encryptionOptions.Value.Enabled && !client.EncryptionEnabled)
        {
            logger.LogWarning("Encryption is enabled and TLS Handshake isn't finished.");
            await client.DisposeAsync();
            return;
        }

        var tokenRecord = await playerLoaderService.GetTokenAsync(Token, DelayMs);
        
        if (tokenRecord == null)
        {
            logger.LogWarning("Failed to find token record for provided sso.");
            await client.DisposeAsync();
            return;
        }
        
        var player = await playerRepository.GetPlayerByIdAsync(tokenRecord.PlayerId);

        if (player?.Data == null ||
            player.AvatarData == null ||
            player.NavigatorSettings == null ||
            player.GameSettings == null)
        {
            logger.LogError("Failed to resolve player record.");
            await client.DisposeAsync();
            return;
        }
        
        if (player.Bans.Any(x => x.ExpiresAt == null || x.ExpiresAt >= DateTime.Now))
        {
            logger.LogWarning("Disconnected banned player {@PlayerUsername}", player.Username);
            await client.DisposeAsync();
            return;
        }

        var ipAddress = client
            .Channel
            .RemoteAddress
            .ToString()?
            .Split(":")
            .First() ?? "";
        
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        
        if (dbContext.BannedIpAddresses.Any(x => x.IpAddress == ipAddress && (x.ExpiresAt == null || x.ExpiresAt >= DateTime.Now)))
        {
            logger.LogWarning("Disconnected banned IP {@Ip}", ipAddress);
            await client.DisposeAsync();
            return;
        }

        // Re-seed timed sanctions from their durable record so a mute / trade lock survives an
        // emulator restart (the in-memory stores are otherwise empty on boot).
        var tradeLockExpiry = await SanctionPersistence.ActiveExpiryAsync(dbContext, player.Id, SanctionPersistence.TypeTradeLock);
        if (tradeLockExpiry.HasValue)
        {
            TradeLockStore.Lock(player.Id, tradeLockExpiry.Value - DateTime.Now);
        }

        var muteExpiry = await SanctionPersistence.ActiveExpiryAsync(dbContext, player.Id, SanctionPersistence.TypeMute);
        if (muteExpiry.HasValue)
        {
            GlobalMuteStore.Mute(player.Id, muteExpiry.Value - DateTime.Now);
        }

        var playerLogic = mapper.Map<IPlayerLogic>(player);

        playerLogic.NetworkObject = client;
        playerLogic.Channel = client.Channel;

        var playerId = player.Id;
        var existingPlayer = playerRepository.GetPlayerLogicById(playerId);

        client.Player = playerLogic;

        if (existingPlayer is { Channel: not null })
        {
            await playerRepository.TryRemovePlayerAsync(existingPlayer.Id);
            await networkClientRepository.TryRemoveAsync(existingPlayer.Channel.Id);

            var roomUser = client.RoomUser;
            
            if (roomUser != null)
            {
                await roomUser.Room.UserRepository.TryRemoveAsync(roomUser.Player.Id);
            }
        }

        if (!playerRepository.TryAddPlayer(playerLogic))
        {
            logger.LogError($"Player {playerLogic.Username} could not be registered");
            await client.DisposeAsync();
            return;
        }
        
        await client.WriteToStreamAsync(new SecureLoginWriter());
        
        playerLogic.Data.IsOnline = true;
        playerLogic.Data.LastOnline = DateTime.Now;

        // Persist online status so the CMS (which reads player_data.is_online) shows
        // the correct online count. Disconnect already saves IsOnline=false; login
        // only set it in memory, leaving the DB column stuck at 0.
        await using (var onlineDbContext = await dbContextFactory.CreateDbContextAsync())
        {
            onlineDbContext.Entry(playerLogic.Data).Property(x => x.IsOnline).IsModified = true;
            onlineDbContext.Entry(playerLogic.Data).Property(x => x.LastOnline).IsModified = true;
            await onlineDbContext.SaveChangesAsync();
        }

        playerLogic.Authenticated = true;

        await NetworkPacketEventHelpers.SendLoginPacketsToPlayerAsync(client, playerLogic);
        await NetworkPacketEventHelpers.SendPlayerSubscriptionPacketsAsync(playerLogic);
        
        await playerHelperService.SendPlayerFriendListUpdate(playerLogic, playerRepository);
        
        await playerHelperService.UpdatePlayerStatusForFriendsAsync(
            playerLogic, 
            player.GetMergedFriendships(), 
            true, 
            false, 
            playerRepository);
        
        await SendWelcomeMessageAsync(playerLogic);

        // Achievements: daily login presence + registration longevity. Wrapped so an achievement
        // failure never blocks login.
        try
        {
            await AchievementService.AddProgressAsync(dbContextFactory, playerLogic, "Login", 1, daily: true);
            await AchievementService.AddProgressAsync(dbContextFactory, playerLogic, "AllTimeHotelPresence", 1, daily: true);

            var daysRegistered = (int) Math.Max(0, (DateTimeOffset.Now - player.CreatedAt).TotalDays);
            if (daysRegistered > 0)
            {
                await AchievementService.SetProgressAsync(dbContextFactory, playerLogic, "RegistrationDuration", daysRegistered);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to grant login achievements for {Player}", playerLogic.Username);
        }

        // Faucets: one-time starting grant + daily login + streak bonus. Wrapped so a faucet failure
        // never blocks login.
        try
        {
            var isMember = await FaucetService.IsMemberAsync(dbContextFactory, playerLogic.Id);
            var startGrant = await FaucetService.GrantNewPlayerAsync(playerRepository, dbContextFactory, playerLogic);
            var daily = await FaucetService.GrantDailyAndStreakAsync(playerRepository, dbContextFactory, playerLogic, isMember);

            // First login of the day: always show the Solana Hotel daily/streak message.
            if (daily.FirstToday)
            {
                var intro = startGrant > 0
                    ? $"Welcome to Solana Hotel! Here's {startGrant} starting credits.\n\n"
                    : "Welcome back to Solana Hotel!\n\n";
                await playerLogic.SendAlertAsync(
                    $"{intro}" +
                    $"Daily login bonus: +{daily.LoginCredits} credits\n" +
                    $"Day {daily.Streak} streak bonus: +{daily.StreakCredits} credits\n\n" +
                    $"That's +{daily.Total} credits today — come back tomorrow to grow your streak! (Miss a day and it resets to day 1.)");
            }
            else if (startGrant > 0)
            {
                await playerLogic.SendAlertAsync($"Welcome to Solana Hotel! Here's {startGrant} starting credits.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to grant login faucets for {Player}", playerLogic.Username);
        }

        // Anti-farming: capture the login IP for alt-cluster / Sybil detection (surfaced in the admin
        // feed for manual review — never auto-actioned). Non-fatal.
        try
        {
            await using var ipDbContext = await dbContextFactory.CreateDbContextAsync();
            await ipDbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_login_ips (player_id, ip_address, created_at) VALUES ({0}, {1}, {2})",
                playerLogic.Id, ipAddress, DateTime.Now);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log login IP for {Player}", playerLogic.Username);
        }

        logger.LogInformation($"Player '{playerLogic.Username}' has logged in from {ipAddress} ({Math.Round(sw.Elapsed.TotalMilliseconds)}ms)");
    }

    private async Task SendWelcomeMessageAsync(IPlayerLogic player)
    {
        if (string.IsNullOrEmpty(serverSettings.PlayerWelcomeMessage))
        {
            return;
        }

        var formattedMessage = serverSettings.PlayerWelcomeMessage
            .Replace("[username]", player.Username)
            .Replace("[version]", GlobalState.Version.ToString());

        await player.SendAlertAsync(formattedMessage);
    }

    private bool ValidateSso(string sso) => sso.Length >= constants.MinSsoLength;
}