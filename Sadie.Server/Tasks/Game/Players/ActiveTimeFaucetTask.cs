using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db;
using Sadie.Networking.Events.Economy;

namespace SadieEmulator.Tasks.Game.Players;

// Active-time faucet: 10 credits per 30 minutes of genuine activity (must be in a room and not idle),
// capped at 50/day (5 grants = 2.5 hours). Persisted via FaucetService -> CurrencyService, so the
// credits survive relog. Auto-registered via the IServerTask scan.
public class ActiveTimeFaucetTask(
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IPlayerRepository playerRepository,
    IRoomUserRepository roomUserRepository) : IServerTask
{
    public TimeSpan PeriodicInterval => TimeSpan.FromMinutes(30);

    // Seed to "now" so the first grant lands after 30 minutes of uptime, not immediately on boot.
    public DateTime LastExecuted { get; set; } = DateTime.Now;

    public async Task ExecuteAsync()
    {
        foreach (var player in playerRepository.GetAll())
        {
            if (player.State.CurrentRoomId == 0) continue;                                   // not in a room
            if (roomUserRepository.TryGetById(player.Id, out var roomUser) && roomUser!.IsIdle) continue; // idle

            await FaucetService.GrantAsync(playerRepository, dbContextFactory, player.Id,
                "active_time", FaucetService.ActiveAmount, FaucetService.ActiveCap, isMember: false);
        }
    }
}
