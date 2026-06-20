using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;
using Sadie.Networking.Events.Economy;

namespace SadieEmulator.Tasks.Game.Marketplace;

// Closes ended auctions once a minute: pays the seller (minus the 5% fee), delivers the item to the
// highest bidder, or returns it to the seller if there were no bids. Auto-registered via the
// IServerTask scan.
public class AuctionSettlementTask(
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IPlayerRepository playerRepository) : IServerTask
{
    public TimeSpan PeriodicInterval => TimeSpan.FromMinutes(1);
    public DateTime LastExecuted { get; set; }

    public Task ExecuteAsync() =>
        MarketplaceService.SettleAuctionsAsync(playerRepository, dbContextFactory);
}
