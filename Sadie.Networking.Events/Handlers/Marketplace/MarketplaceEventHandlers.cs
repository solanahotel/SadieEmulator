using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Marketplace;

// Custom marketplace packet handlers (headers 9000-9005). Standalone feature — nothing to do with the
// catalog. All delegate to MarketplaceService.

// Browse a section's active listings (9000).
[PacketId(9000)]
public class MarketplaceGetListingsEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public string Section { get; set; } = "item_market";

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var listings = await MarketplaceService.GetActiveListingsAsync(dbContextFactory, Section);
        await client.WriteToStreamAsync(new MarketplaceListingsWriter { Listings = listings });
    }
}

// The viewer's eligible inventory for a section's picker (9001). item_market -> marketable rares;
// credit_exchange -> credit items only.
[PacketId(9001)]
public class MarketplaceGetSellableEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public string Section { get; set; } = "item_market";

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var items = await MarketplaceService.GetSellableAsync(dbContextFactory, client.Player, Section == "credit_exchange");
        await client.WriteToStreamAsync(new MarketplaceSellableWriter { Items = items });
    }
}

// The viewer's own active listings (9002).
[PacketId(9002)]
public class MarketplaceGetMyListingsEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var listings = await MarketplaceService.GetMyListingsAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new MarketplaceMyListingsWriter { Listings = listings });
    }
}

// Create a listing (9003). Wire: section, priceCredits, priceHotel, itemIds[]. item_market uses
// priceCredits; credit_exchange uses priceHotel and only accepts credit items.
[PacketId(9003)]
public class MarketplaceCreateEventHandler(
    Sadie.API.Game.Players.IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public string Section { get; set; } = "item_market";
    public int PriceCredits { get; set; }
    public string PriceHotel { get; set; } = "0";
    public int DurationHours { get; set; }
    public List<int> ItemIds { get; set; } = [];

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        bool ok;
        string error;
        if (Section == "credit_exchange")
        {
            decimal.TryParse(PriceHotel, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var hotel);
            (ok, error, _) = await MarketplaceService.ListCreditExchangeAsync(playerRepository, dbContextFactory, client.Player, ItemIds, hotel);
        }
        else if (Section == "auction")
        {
            var itemId = ItemIds.Count > 0 ? ItemIds[0] : 0;
            (ok, error, _) = await MarketplaceService.CreateAuctionAsync(playerRepository, dbContextFactory, client.Player, itemId, PriceCredits, DurationHours);
        }
        else
        {
            (ok, error, _) = await MarketplaceService.ListItemMarketAsync(playerRepository, dbContextFactory, client.Player, ItemIds, PriceCredits);
        }

        await client.WriteToStreamAsync(new MarketplaceResultWriter { Ok = ok, Message = ok ? "Listed." : error });
    }
}

// Place a bid on an auction (9020). Wire: listingId, bidAmount.
[PacketId(9020)]
public class MarketplaceBidEventHandler(
    Sadie.API.Game.Players.IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int ListingId { get; set; }
    public int BidAmount { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var (ok, error) = await MarketplaceService.PlaceBidAsync(playerRepository, dbContextFactory, client.Player, ListingId, BidAmount);
        await client.WriteToStreamAsync(new MarketplaceResultWriter { Ok = ok, Message = ok ? "Bid placed!" : error });
    }
}

// Buy an Item Market listing (9004).
[PacketId(9004)]
public class MarketplaceBuyEventHandler(
    Sadie.API.Game.Players.IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int ListingId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var (ok, error) = await MarketplaceService.BuyItemMarketAsync(playerRepository, dbContextFactory, client.Player, ListingId);
        await client.WriteToStreamAsync(new MarketplaceResultWriter { Ok = ok, Message = ok ? "Purchase complete." : error });
    }
}

// Cancel an active listing (9005).
[PacketId(9005)]
public class MarketplaceCancelEventHandler(
    Sadie.API.Game.Players.IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int ListingId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var (ok, error) = await MarketplaceService.CancelListingAsync(playerRepository, dbContextFactory, client.Player, ListingId);
        await client.WriteToStreamAsync(new MarketplaceResultWriter { Ok = ok, Message = ok ? "Listing cancelled." : error });
    }
}
