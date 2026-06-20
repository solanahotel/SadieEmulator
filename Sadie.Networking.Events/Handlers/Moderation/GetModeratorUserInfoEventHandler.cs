using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Enums.Game.Players;
using Sadie.Networking.Events.Moderation;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Moderation;

// Feeds the mod tool's user-info panel (request MOD_TOOL_USER_INFO = 3295, response
// MODERATION_USER_INFO = 2866). The server previously had no handler for this, so the panel was
// always empty. We surface the basics plus the live trade-lock state (count + expiry); sanction
// history we don't persist (cfh / caution counts) is reported as 0.
[PacketId(3295)]
public class GetModeratorUserInfoEventHandler(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int UserId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || !client.Player.HasPermission("admin"))
        {
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var player = await dbContext.Players
            .Include(x => x.Data)
            .Include(x => x.AvatarData)
            .FirstOrDefaultAsync(x => x.Id == UserId);

        if (player == null)
        {
            return;
        }

        var banCount = (await dbContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM player_bans WHERE player_id = {0}", (long) UserId)
            .ToListAsync()).FirstOrDefault();

        var now = DateTimeOffset.Now;
        var registrationAgeInMinutes = (int) Math.Max(0, (now - player.CreatedAt).TotalMinutes);

        var lastOnline = player.Data?.LastOnline;
        var online = playerRepository.GetPlayerLogicById(UserId) != null || (player.Data?.IsOnline ?? false);
        var minutesSinceLastLogin = online || lastOnline == null
            ? 0
            : (int) Math.Max(0, (now - lastOnline.Value).TotalMinutes);

        // Historical counts come from the durable player_sanctions log; the trade-lock expiry is the
        // active (still-future) lock, which also survives restarts.
        var cautionCount = await SanctionPersistence.CountAsync(dbContext, UserId, SanctionPersistence.TypeCaution);
        var tradingLockCount = await SanctionPersistence.CountAsync(dbContext, UserId, SanctionPersistence.TypeTradeLock);
        var tradeExpiry = await SanctionPersistence.ActiveExpiryAsync(dbContext, UserId, SanctionPersistence.TypeTradeLock);
        var cfhCount = await CfhTicketLog.CfhCountAsync(dbContext, UserId);
        var abusiveCfhCount = await CfhTicketLog.AbusiveCfhCountAsync(dbContext, UserId);

        await client.WriteToStreamAsync(new ModeratorUserInfoWriter
        {
            UserId = (int) player.Id,
            Username = player.Username,
            Figure = player.AvatarData?.FigureCode ?? "",
            RegistrationAgeInMinutes = registrationAgeInMinutes,
            MinutesSinceLastLogin = minutesSinceLastLogin,
            Online = online,
            CfhCount = cfhCount,
            AbusiveCfhCount = abusiveCfhCount,
            CautionCount = cautionCount,
            BanCount = banCount,
            TradingLockCount = tradingLockCount,
            TradingExpiryDate = tradeExpiry?.ToString() ?? "",
            LastPurchaseDate = "",
            IdentityId = (int) player.Id,
            IdentityRelatedBanCount = 0,
            PrimaryEmailAddress = player.Email,
            UserClassification = ""
        });
    }
}
