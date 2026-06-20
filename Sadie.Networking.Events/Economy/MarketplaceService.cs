using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;
using Sadie.Db.Models.Furniture;
using Sadie.Db.Models.Players;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Players.Inventory;

namespace Sadie.Networking.Events.Economy;

// Core marketplace logic. Phase 1 = the Item Market (rare items -> Credits, fixed price). Escrow uses
// the gift-system pattern: a listed item is removed from inventory (its player_furniture_items row is
// deleted) and snapshotted into marketplace_listing_items, so it cannot reappear on relog or be
// placed/traded while listed. On sale a fresh row is created for the buyer; on cancel for the seller.
public static class MarketplaceService
{
    public const double FeePercent = 0.05;   // 5% credit fee, burned
    public const int MaxActiveListings = 20;

    private sealed record Snapshot(int DefId, string Limited, string Meta);

    // List one or more owned items in the Item Market for a fixed total Credit price.
    public static async Task<(bool Ok, string Error, int ListingId)> ListItemMarketAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic seller, List<int> ownedItemIds, int priceCredits)
    {
        if (priceCredits <= 0) return (false, "Enter a valid price.", 0);
        if (ownedItemIds.Count == 0) return (false, "Select an item to list.", 0);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var active = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM marketplace_listings WHERE seller_id = {0} AND status IN ('active','pending')",
            seller.Id).ToListAsync()).FirstOrDefault();
        if (active >= MaxActiveListings) return (false, "You already have 20 active listings.", 0);

        var items = seller.FurnitureItems
            .Where(x => ownedItemIds.Contains((int) x.Id) && x.PlacementData == null)
            .ToList();
        if (items.Count == 0) return (false, "Those items aren't in your inventory.", 0);

        var defIds = items.Select(x => x.FurnitureItem.Id).Distinct().ToList();
        var marketableDefs = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT id AS Value FROM furniture_items WHERE marketable = 1 AND id IN (" + string.Join(",", defIds) + ")")
            .ToListAsync()).ToHashSet();
        if (items.Any(x => !marketableDefs.Contains(x.FurnitureItem.Id)))
            return (false, "One or more of those items can't be sold on the marketplace.", 0);

        var listingId = await InsertListingAsync(dbContext,
            "INSERT INTO marketplace_listings (seller_id, section, status, furniture_item_id, quantity, price_credits, total_credit_value, created_at) " +
            "VALUES ({0}, 'item_market', 'active', {1}, {2}, {3}, {3}, {4})",
            seller.Id, items[0].FurnitureItem.Id, items.Count, priceCredits, DateTime.Now);

        // Snapshot + escrow each item.
        foreach (var item in items)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO marketplace_listing_items (listing_id, furniture_item_id, limited_data, meta_data) VALUES ({0}, {1}, {2}, {3})",
                listingId, item.FurnitureItem.Id, (object?) item.LimitedData ?? DBNull.Value, (object?) item.MetaData ?? DBNull.Value);

            dbContext.Entry(item).State = EntityState.Deleted;
            seller.FurnitureItems.Remove(item);
        }

        await dbContext.SaveChangesAsync();
        await LogAsync(dbContext, "list", "item_market", listingId, seller.Id, null, items[0].FurnitureItem.Id, items.Count, priceCredits, "active");

        await RefreshInventoryAsync(seller);
        return (true, "", listingId);
    }

    // List credit items in the Credit Exchange for a fixed $HOTEL price. Only credit items qualify.
    public static async Task<(bool Ok, string Error, int ListingId)> ListCreditExchangeAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic seller, List<int> ownedItemIds, decimal priceHotel)
    {
        if (priceHotel <= 0) return (false, "Enter a valid $HOTEL price.", 0);
        if (ownedItemIds.Count == 0) return (false, "Select a credit item to list.", 0);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var active = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM marketplace_listings WHERE seller_id = {0} AND status IN ('active','pending')",
            seller.Id).ToListAsync()).FirstOrDefault();
        if (active >= MaxActiveListings) return (false, "You already have 20 active listings.", 0);

        var items = seller.FurnitureItems
            .Where(x => ownedItemIds.Contains((int) x.Id) && x.PlacementData == null)
            .ToList();
        if (items.Count == 0) return (false, "Those items aren't in your inventory.", 0);

        var defIds = items.Select(x => x.FurnitureItem.Id).Distinct().ToList();
        var creditNames = new Dictionary<int, string>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, asset_name FROM furniture_items WHERE origin = 'credit_item' AND id IN (" + string.Join(",", defIds) + ")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) creditNames[reader.GetInt32(0)] = reader.GetString(1);
        }
        finally { await connection.CloseAsync(); }

        if (items.Any(x => !creditNames.ContainsKey(x.FurnitureItem.Id)))
            return (false, "Only credit items can be listed in the Credit Exchange.", 0);

        var unitValue = ParseCreditValue(creditNames[items[0].FurnitureItem.Id]);
        var totalValue = items.Sum(x => ParseCreditValue(creditNames[x.FurnitureItem.Id]));

        var listingId = await InsertListingAsync(dbContext,
            "INSERT INTO marketplace_listings (seller_id, section, status, furniture_item_id, quantity, unit_credit_value, total_credit_value, price_hotel, created_at) " +
            "VALUES ({0}, 'credit_exchange', 'active', {1}, {2}, {3}, {4}, {5}, {6})",
            seller.Id, items[0].FurnitureItem.Id, items.Count, unitValue, totalValue, priceHotel, DateTime.Now);

        foreach (var item in items)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO marketplace_listing_items (listing_id, furniture_item_id, limited_data, meta_data) VALUES ({0}, {1}, {2}, {3})",
                listingId, item.FurnitureItem.Id, (object?) item.LimitedData ?? DBNull.Value, (object?) item.MetaData ?? DBNull.Value);
            dbContext.Entry(item).State = EntityState.Deleted;
            seller.FurnitureItems.Remove(item);
        }

        await dbContext.SaveChangesAsync();
        await LogAsync(dbContext, "list", "credit_exchange", listingId, seller.Id, null, items[0].FurnitureItem.Id, items.Count, totalValue, "active");

        await RefreshInventoryAsync(seller);
        return (true, "", listingId);
    }

    // Buy an Item Market listing: charge buyer, burn 5%, pay seller 95%, deliver items, notify seller.
    public static async Task<(bool Ok, string Error)> BuyItemMarketAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic buyer, int listingId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // Lock the listing (active -> pending) so it can only be bought once.
        var claimed = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace_listings SET status = 'pending', updated_at = {1} WHERE id = {0} AND section = 'item_market' AND status = 'active'",
            listingId, DateTime.Now);
        if (claimed == 0) return (false, "That listing is no longer available.");

        var rows = await dbContext.Database.SqlQueryRaw<MarketRow>(
            "SELECT seller_id AS SellerId, price_credits AS PriceCredits FROM marketplace_listings WHERE id = {0}", listingId)
            .ToListAsync();
        var listing = rows.FirstOrDefault();
        if (listing == null) { await ReactivateAsync(dbContext, listingId); return (false, "Listing not found."); }

        if (listing.SellerId == buyer.Id)
        {
            await ReactivateAsync(dbContext, listingId);
            return (false, "You can't buy your own listing.");
        }

        var price = listing.PriceCredits ?? 0;
        if (!await CurrencyService.TakeCreditsAsync(playerRepository, dbContextFactory, buyer.Id, price))
        {
            await ReactivateAsync(dbContext, listingId);
            return (false, "You don't have enough credits.");
        }

        // 5% burned, 95% to the seller.
        var fee = (int) Math.Floor(price * FeePercent);
        var sellerProceeds = price - fee;
        await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, listing.SellerId, sellerProceeds);

        // Deliver the escrowed items to the buyer.
        var snapshots = await GetSnapshotsAsync(dbContext, listingId);
        await MaterializeAsync(dbContext, playerRepository, buyer.Id, snapshots);

        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace_listings SET status = 'sold', buyer_id = {1}, completed_at = {2} WHERE id = {0}",
            listingId, buyer.Id, DateTime.Now);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM marketplace_listing_items WHERE listing_id = {0}", listingId);

        await LogAsync(dbContext, "buy", "item_market", listingId, listing.SellerId, buyer.Id, null, snapshots.Count, price, "completed");

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO item_market_sales (listing_id, furniture_item_id, quantity, seller_id, buyer_id, price_credits, fee_credits, sold_at) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})",
            listingId, snapshots.Count > 0 ? snapshots[0].DefId : 0, snapshots.Count, listing.SellerId, buyer.Id, price, fee, DateTime.Now);

        await InboxService.SendAsync(playerRepository, dbContextFactory, listing.SellerId, "marketplace",
            "Item sold", $"Your marketplace listing sold for {price} credits ({sellerProceeds} after the 5% fee).");

        return (true, "");
    }

    // Cancel an active listing -> return the escrowed items to the seller.
    public static async Task<(bool Ok, string Error)> CancelListingAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic requester, int listingId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // Auctions can only be cancelled before the first bid.
        var owned = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace_listings SET status = 'cancelled', completed_at = {1} WHERE id = {0} AND seller_id = {2} AND status = 'active' AND (section <> 'auction' OR highest_bidder_id IS NULL)",
            listingId, DateTime.Now, requester.Id);
        if (owned == 0) return (false, "That listing can't be cancelled (auctions lock once bid on).");

        var snapshots = await GetSnapshotsAsync(dbContext, listingId);
        await MaterializeAsync(dbContext, playerRepository, requester.Id, snapshots);
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM marketplace_listing_items WHERE listing_id = {0}", listingId);

        await LogAsync(dbContext, "cancel", "item_market", listingId, requester.Id, null, null, snapshots.Count, null, "cancelled");
        return (true, "");
    }

    // ----- auction house (credits-only) -----

    public static async Task<(bool Ok, string Error, int ListingId)> CreateAuctionAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic seller, int ownedItemId, int startPriceCredits, int durationHours)
    {
        if (startPriceCredits <= 0) return (false, "Enter a valid starting price.", 0);
        if (!AuctionDurations.Contains(durationHours)) return (false, "Pick a valid duration.", 0);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var active = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM marketplace_listings WHERE seller_id = {0} AND status IN ('active','pending')", seller.Id).ToListAsync()).FirstOrDefault();
        if (active >= MaxActiveListings) return (false, "You already have 20 active listings.", 0);

        var item = seller.FurnitureItems.FirstOrDefault(x => (int) x.Id == ownedItemId && x.PlacementData == null);
        if (item == null) return (false, "That item isn't in your inventory.", 0);

        var marketable = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM furniture_items WHERE marketable = 1 AND id = {0}", item.FurnitureItem.Id).ToListAsync()).FirstOrDefault();
        if (marketable == 0) return (false, "That item can't be auctioned.", 0);

        var expiresAt = DateTime.Now.AddHours(durationHours);
        var listingId = await InsertListingAsync(dbContext,
            "INSERT INTO marketplace_listings (seller_id, section, status, furniture_item_id, quantity, price_credits, start_price_credits, highest_bid, duration_hours, expires_at, created_at) " +
            "VALUES ({0}, 'auction', 'active', {1}, 1, {2}, {2}, 0, {3}, {4}, {5})",
            seller.Id, item.FurnitureItem.Id, startPriceCredits, durationHours, expiresAt, DateTime.Now);

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO marketplace_listing_items (listing_id, furniture_item_id, limited_data, meta_data) VALUES ({0}, {1}, {2}, {3})",
            listingId, item.FurnitureItem.Id, (object?) item.LimitedData ?? DBNull.Value, (object?) item.MetaData ?? DBNull.Value);
        dbContext.Entry(item).State = EntityState.Deleted;
        seller.FurnitureItems.Remove(item);
        await dbContext.SaveChangesAsync();

        await LogAsync(dbContext, "list", "auction", listingId, seller.Id, null, item.FurnitureItem.Id, 1, startPriceCredits, "active");
        await RefreshInventoryAsync(seller);
        return (true, "", listingId);
    }

    public static async Task<(bool Ok, string Error)> PlaceBidAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic bidder, int listingId, int bidAmount)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var a = await ReadBidStateAsync(dbContext, listingId);
        if (a == null) return (false, "That auction is no longer available.");
        if (a.Value.ExpiresAt <= DateTimeOffset.Now.ToUnixTimeSeconds()) return (false, "That auction has ended.");
        if (a.Value.SellerId == bidder.Id) return (false, "You can't bid on your own auction.");

        var minBid = a.Value.HighestBid > 0 ? a.Value.HighestBid + MinBidIncrement : a.Value.StartPrice;
        if (bidAmount < minBid) return (false, $"Bid must be at least {minBid} credits.");

        if (!await CurrencyService.TakeCreditsAsync(playerRepository, dbContextFactory, bidder.Id, bidAmount))
            return (false, "You don't have enough credits.");

        // Anti-snipe: extend if ending within the window. Conditional update re-checks the min bid.
        var extend = (a.Value.ExpiresAt - DateTimeOffset.Now.ToUnixTimeSeconds()) <= AntiSnipeMinutes * 60;
        int affected;
        if (extend)
            affected = await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace_listings SET highest_bid = {1}, highest_bidder_id = {2}, expires_at = {3}, updated_at = {4} " +
                "WHERE id = {0} AND status = 'active' AND {1} >= (CASE WHEN highest_bid > 0 THEN highest_bid + {5} ELSE start_price_credits END)",
                listingId, bidAmount, bidder.Id, DateTime.Now.AddMinutes(AntiSnipeMinutes), DateTime.Now, MinBidIncrement);
        else
            affected = await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace_listings SET highest_bid = {1}, highest_bidder_id = {2}, updated_at = {3} " +
                "WHERE id = {0} AND status = 'active' AND {1} >= (CASE WHEN highest_bid > 0 THEN highest_bid + {4} ELSE start_price_credits END)",
                listingId, bidAmount, bidder.Id, DateTime.Now, MinBidIncrement);

        if (affected == 0)
        {
            await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, bidder.Id, bidAmount);
            return (false, "You were just outbid — try a higher amount.");
        }

        // Refund the previous highest bidder.
        if (a.Value.HighestBidder > 0 && a.Value.HighestBidder != bidder.Id)
        {
            await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, a.Value.HighestBidder, a.Value.HighestBid);
            await InboxService.SendAsync(playerRepository, dbContextFactory, a.Value.HighestBidder, "auction",
                "You were outbid", $"Someone outbid you on an auction. Your {a.Value.HighestBid} credits were refunded.");
        }

        await LogAsync(dbContext, "bid", "auction", listingId, a.Value.SellerId, bidder.Id, null, null, bidAmount, "active");
        return (true, "");
    }

    // Auto-close auctions whose timer has elapsed. Called by the per-minute settlement task.
    public static async Task SettleAuctionsAsync(IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory)
    {
        List<int> ids;
        await using (var ctx = await dbContextFactory.CreateDbContextAsync())
        {
            ids = await ctx.Database.SqlQueryRaw<int>(
                "SELECT id AS Value FROM marketplace_listings WHERE section = 'auction' AND status = 'active' AND expires_at IS NOT NULL AND expires_at <= {0}",
                DateTime.Now).ToListAsync();
        }

        foreach (var id in ids)
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();

            var claimed = await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE marketplace_listings SET status = 'pending' WHERE id = {0} AND status = 'active'", id);
            if (claimed == 0) continue;

            var a = await ReadBidStateAsync(dbContext, id);
            if (a == null) continue;
            var snapshots = await GetSnapshotsAsync(dbContext, id);

            if (a.Value.HighestBidder > 0)
            {
                await MaterializeAsync(dbContext, playerRepository, a.Value.HighestBidder, snapshots);
                var fee = (int) Math.Floor(a.Value.HighestBid * FeePercent);
                await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, a.Value.SellerId, a.Value.HighestBid - fee);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "UPDATE marketplace_listings SET status = 'sold', buyer_id = {1}, completed_at = {2} WHERE id = {0}", id, a.Value.HighestBidder, DateTime.Now);
                await InboxService.SendAsync(playerRepository, dbContextFactory, a.Value.SellerId, "auction",
                    "Auction sold", $"Your auction sold for {a.Value.HighestBid} credits ({a.Value.HighestBid - fee} after the 5% fee).");
                await InboxService.SendAsync(playerRepository, dbContextFactory, a.Value.HighestBidder, "auction",
                    "Auction won", $"You won an auction for {a.Value.HighestBid} credits — the item is in your inventory.");
                await LogAsync(dbContext, "auction_win", "auction", id, a.Value.SellerId, a.Value.HighestBidder, null, snapshots.Count, a.Value.HighestBid, "completed");
                await dbContext.Database.ExecuteSqlRawAsync(
                    "INSERT INTO auction_sales (listing_id, furniture_item_id, seller_id, winner_id, final_bid, fee_credits, sold_at) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})",
                    id, snapshots.Count > 0 ? snapshots[0].DefId : 0, a.Value.SellerId, a.Value.HighestBidder, a.Value.HighestBid, fee, DateTime.Now);
            }
            else
            {
                await MaterializeAsync(dbContext, playerRepository, a.Value.SellerId, snapshots);
                await dbContext.Database.ExecuteSqlRawAsync(
                    "UPDATE marketplace_listings SET status = 'expired', completed_at = {1} WHERE id = {0}", id, DateTime.Now);
                await InboxService.SendAsync(playerRepository, dbContextFactory, a.Value.SellerId, "auction",
                    "Auction expired", "Your auction ended with no bids — the item is back in your inventory.");
                await LogAsync(dbContext, "expire", "auction", id, a.Value.SellerId, null, null, snapshots.Count, null, "expired");
            }

            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM marketplace_listing_items WHERE listing_id = {0}", id);
        }
    }

    private readonly record struct BidState(long SellerId, int StartPrice, int HighestBid, long HighestBidder, long ExpiresAt);

    private static async Task<BidState?> ReadBidStateAsync(SadieDbContext dbContext, int listingId)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT seller_id, start_price_credits, highest_bid, COALESCE(highest_bidder_id,0), COALESCE(CAST(UNIX_TIMESTAMP(expires_at) AS SIGNED),0) FROM marketplace_listings WHERE id = @id";
            var p = command.CreateParameter(); p.ParameterName = "@id"; p.Value = listingId; command.Parameters.Add(p);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new BidState(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4));
        }
        finally { await connection.CloseAsync(); }
    }

    // ----- browse / query (for the client writers) -----

    public sealed record ListingView(int Id, string SellerName, int DefId, int AssetId, string Name, string Rarity, int Quantity, int PriceCredits, string PriceHotel, int TotalCreditValue, int StartPrice, int HighestBid, long ExpiresAt);

    public const int MinBidIncrement = 5;
    public const int AntiSnipeMinutes = 5;
    public static readonly int[] AuctionDurations = { 6, 12, 24, 48 };
    public sealed record SellableView(int OwnedId, int DefId, int AssetId, string Name, string Rarity, int CreditValue);

    // Credit items encode their credit worth in the asset name, e.g. CF_50_goldbar -> 50.
    public static int ParseCreditValue(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return 0;
        var parts = assetName.Split('_');
        return parts.Length >= 2 && int.TryParse(parts[1], out var value) ? value : 0;
    }

    private const string ListingSelect =
        "SELECT ml.id, p.username, ml.furniture_item_id, fi.asset_id, fi.name, COALESCE(fi.rarity,''), ml.quantity, " +
        "COALESCE(ml.price_credits,0), COALESCE(ml.price_hotel,0), COALESCE(ml.total_credit_value,0), " +
        "COALESCE(ml.start_price_credits,0), COALESCE(ml.highest_bid,0), COALESCE(CAST(UNIX_TIMESTAMP(ml.expires_at) AS SIGNED),0) " +
        "FROM marketplace_listings ml JOIN players p ON p.id = ml.seller_id JOIN furniture_items fi ON fi.id = ml.furniture_item_id ";

    public static Task<List<ListingView>> GetActiveListingsAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, string section) =>
        ReadListingsAsync(dbContextFactory,
            ListingSelect + "WHERE ml.section = @section AND ml.status = 'active' ORDER BY ml.created_at DESC LIMIT 200",
            ("@section", section));

    public static Task<List<ListingView>> GetMyListingsAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long sellerId) =>
        ReadListingsAsync(dbContextFactory,
            ListingSelect + "WHERE ml.seller_id = @seller AND ml.status IN ('active','pending') ORDER BY ml.created_at DESC",
            ("@seller", sellerId));

    private static async Task<List<ListingView>> ReadListingsAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, string sql, params (string Name, object Value)[] parameters)
    {
        var result = new List<ListingView>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p);
            }
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ListingView(
                    reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.GetDecimal(8).ToString("0.#########"), reader.GetInt32(9),
                    reader.GetInt32(10), reader.GetInt32(11), reader.GetInt64(12)));
            }
        }
        finally { await connection.CloseAsync(); }
        return result;
    }

    // The player's un-placed items eligible for a section's "list an item" picker:
    //   creditItems = false -> marketable rares/club (Item Market)
    //   creditItems = true  -> credit items only (Credit Exchange), with their credit value
    public static async Task<List<SellableView>> GetSellableAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, IPlayerLogic player, bool creditItems)
    {
        var owned = player.FurnitureItems.Where(x => x.PlacementData == null).ToList();
        if (owned.Count == 0) return [];

        var defIds = owned.Select(x => x.FurnitureItem.Id).Distinct().ToList();
        var filter = creditItems ? "origin = 'credit_item'" : "marketable = 1";

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var eligible = new Dictionary<int, (int AssetId, string Name, string Rarity, int CreditValue)>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, asset_id, asset_name, name, COALESCE(rarity,'') FROM furniture_items WHERE " + filter + " AND id IN (" + string.Join(",", defIds) + ")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var assetName = reader.GetString(2);
                eligible[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetString(3), reader.GetString(4),
                    creditItems ? ParseCreditValue(assetName) : 0);
            }
        }
        finally { await connection.CloseAsync(); }

        return owned
            .Where(x => eligible.ContainsKey(x.FurnitureItem.Id))
            .Select(x =>
            {
                var e = eligible[x.FurnitureItem.Id];
                return new SellableView((int) x.Id, x.FurnitureItem.Id, e.AssetId, e.Name, e.Rarity, e.CreditValue);
            })
            .ToList();
    }

    // ----- helpers -----

    private static async Task<List<Snapshot>> GetSnapshotsAsync(SadieDbContext dbContext, int listingId)
    {
        var result = new List<Snapshot>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT furniture_item_id, limited_data, meta_data FROM marketplace_listing_items WHERE listing_id = @l";
            var p = command.CreateParameter(); p.ParameterName = "@l"; p.Value = listingId; command.Parameters.Add(p);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new Snapshot(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2)));
            }
        }
        finally { await connection.CloseAsync(); }
        return result;
    }

    // Create fresh inventory rows for a recipient from snapshots; sync in-memory + client if online.
    private static async Task MaterializeAsync(
        SadieDbContext dbContext, IPlayerRepository playerRepository, long recipientId, List<Snapshot> snapshots)
    {
        if (snapshots.Count == 0) return;

        var recipientEntity = await dbContext.Set<Player>().FirstOrDefaultAsync(x => x.Id == recipientId);
        if (recipientEntity == null) return;

        var defIds = snapshots.Select(s => s.DefId).Distinct().ToList();
        var defs = await dbContext.Set<FurnitureItem>().Where(x => defIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        var created = new List<PlayerFurnitureItem>();
        foreach (var snap in snapshots)
        {
            if (!defs.TryGetValue(snap.DefId, out var def)) continue;
            var item = new PlayerFurnitureItem
            {
                Player = recipientEntity,
                FurnitureItem = def,
                LimitedData = snap.Limited,
                MetaData = snap.Meta,
                CreatedAt = DateTime.Now
            };
            dbContext.Entry(item).State = EntityState.Added;
            created.Add(item);
        }

        await dbContext.SaveChangesAsync();

        var online = playerRepository.GetPlayerLogicById(recipientId);
        if (online?.NetworkObject == null) return;

        foreach (var item in created) online.FurnitureItems.Add(item);

        await online.NetworkObject.WriteToStreamAsync(new PlayerInventoryUnseenItemsWriter
        {
            Count = created.Count, Category = 1, FurnitureItems = created
        });
        await online.NetworkObject.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
    }

    // Grant a single item to a player's inventory (used by the daily wheel's Rare Item Box).
    public static async Task GrantItemAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory, long recipientId, int defId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await MaterializeAsync(dbContext, playerRepository, recipientId, new List<Snapshot> { new(defId, null!, null!) });
    }

    // Grant a "Rare Box" present to a player's inventory (daily wheel). It opens (via the normal present
    // flow) into one of the configured rare_box_items. The box itself is non-tradeable; contents are not.
    public static async Task GrantRareBoxAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory, long recipientId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var boxDef = await dbContext.Set<FurnitureItem>().FirstOrDefaultAsync(x => x.AssetName == "rare_box");
        var recipientEntity = await dbContext.Set<Player>().FirstOrDefaultAsync(x => x.Id == recipientId);
        if (boxDef == null || recipientEntity == null) return;

        var item = new PlayerFurnitureItem
        {
            Player = recipientEntity, FurnitureItem = boxDef, LimitedData = "", MetaData = "", CreatedAt = DateTime.Now
        };
        dbContext.Entry(item).State = EntityState.Added;
        await dbContext.SaveChangesAsync();

        // base_furniture_item_id is a sentinel (the box def); OpenPresent detects rare_box and rolls the pool.
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_gifts (present_item_id, base_furniture_item_id, sender_id, sender_name, message, created_at) VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            item.Id, boxDef.Id, 0, "Daily Wheel", "Contains 1 unique item", DateTime.Now);

        var online = playerRepository.GetPlayerLogicById(recipientId);
        if (online?.NetworkObject == null) return;

        online.FurnitureItems.Add(item);
        await online.NetworkObject.WriteToStreamAsync(new PlayerInventoryUnseenItemsWriter
        {
            Count = 1, Category = 1, FurnitureItems = new List<PlayerFurnitureItem> { item }
        });
        await online.NetworkObject.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
    }

    private static async Task RefreshInventoryAsync(IPlayerLogic player)
    {
        if (player.NetworkObject != null)
        {
            await player.NetworkObject.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
        }
    }

    // Insert a listing and return its id (LAST_INSERT_ID on a held connection).
    private static async Task<int> InsertListingAsync(SadieDbContext dbContext, string sql, params object[] args)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(sql, args);
            return (int) (await dbContext.Database.SqlQueryRaw<long>("SELECT LAST_INSERT_ID() AS Value").ToListAsync()).First();
        }
        finally { await connection.CloseAsync(); }
    }

    private static async Task ReactivateAsync(SadieDbContext dbContext, int listingId) =>
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace_listings SET status = 'active' WHERE id = {0} AND status = 'pending'", listingId);

    private static async Task LogAsync(
        SadieDbContext dbContext, string eventType, string section, int listingId, long sellerId, long? buyerId,
        long? furnitureItemId, int? quantity, int? creditAmount, string status)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO marketplace_logs (event_type, section, listing_id, seller_id, buyer_id, furniture_item_id, quantity, credit_amount, status, created_at, completed_at) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {9})",
            eventType, section, listingId, sellerId,
            (object?) buyerId ?? DBNull.Value, (object?) furnitureItemId ?? DBNull.Value,
            (object?) quantity ?? DBNull.Value, (object?) creditAmount ?? DBNull.Value, status, DateTime.Now);
    }

    private sealed class MarketRow
    {
        public long SellerId { get; set; }
        public int? PriceCredits { get; set; }
    }
}
