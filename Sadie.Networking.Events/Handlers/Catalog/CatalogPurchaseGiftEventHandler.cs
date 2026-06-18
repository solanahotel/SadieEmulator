using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Db.Models.Catalog.Pages;
using Sadie.Db.Models.Furniture;
using Sadie.Db.Models.Players;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Enums.Game.Catalog;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Catalog;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Players.Inventory;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Catalog;

// Buy a catalog item AS A GIFT for another player. Charges the buyer, then drops a wrapped
// present (present_gen) into the recipient's inventory; the wrapped item is recorded in
// player_gifts so OpenPresentEventHandler can hand it over when the present is opened.
[PacketId(EventHandlerId.CatalogPurchaseGift)]
public class CatalogPurchaseGiftEventHandler(
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int PageId { get; set; }
    public int ItemId { get; set; }
    public string ExtraData { get; set; } = "";
    public string ReceivingName { get; set; } = "";
    public string GiftMessage { get; set; } = "";
    public int SpriteId { get; set; }
    public int BoxId { get; set; }
    public int RibbonId { get; set; }
    public bool ShowMyFace { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player?.Data == null)
        {
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var page = await dbContext
            .Set<CatalogPage>()
            .Include(x => x.Items)
            .ThenInclude(x => x.FurnitureItems)
            .FirstOrDefaultAsync(x => x.Id == PageId);

        var catalogItem = page?.Items.FirstOrDefault(x => x.Id == ItemId);

        if (catalogItem == null || catalogItem.FurnitureItems.Count == 0)
        {
            await client.WriteToStreamAsync(new CatalogPurchaseFailedWriter { Error = (int) CatalogPurchaseError.Server });
            return;
        }

        // Resolve the recipient (online first, then DB) BEFORE charging.
        var recipientLogic = playerRepository.GetPlayerLogicByUsername(ReceivingName);
        var recipientId = recipientLogic?.Id ?? (await playerRepository.GetPlayerByUsernameAsync(ReceivingName))?.Id;

        if (recipientId == null)
        {
            await client.WriteToStreamAsync(new GiftReceiverNotFoundWriter());
            return;
        }

        var recipientPlayer = await dbContext.Set<Player>().FirstOrDefaultAsync(x => x.Id == recipientId.Value);
        var presentDef = await dbContext.Set<FurnitureItem>().FirstOrDefaultAsync(x => x.AssetName == "present_gen");

        if (recipientPlayer == null || presentDef == null)
        {
            await client.WriteToStreamAsync(new CatalogPurchaseFailedWriter { Error = (int) CatalogPurchaseError.Server });
            return;
        }

        if (!await CatalogPurchaseEventHandler.TryChargeForCatalogItemPurchaseAsync(client, catalogItem, 1))
        {
            return;
        }

        var wrappedFurni = catalogItem.FurnitureItems.First();

        var present = new PlayerFurnitureItem
        {
            Player = recipientPlayer,
            FurnitureItem = presentDef,
            LimitedData = "1:1",
            MetaData = "0",
            CreatedAt = DateTime.Now
        };

        dbContext.Entry(present).State = EntityState.Added;
        await dbContext.SaveChangesAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_gifts (present_item_id, base_furniture_item_id, sender_id, sender_name, message, created_at) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, NOW())",
            present.Id, wrappedFurni.Id, client.Player.Id, client.Player.Username, GiftMessage ?? "");

        // Deliver to the recipient if they're online (shows up in their inventory).
        if (recipientLogic != null)
        {
            recipientLogic.FurnitureItems.Add(present);

            if (recipientLogic.NetworkObject != null)
            {
                await recipientLogic.NetworkObject.WriteToStreamAsync(new PlayerInventoryUnseenItemsWriter
                {
                    Count = 1,
                    Category = 1,
                    FurnitureItems = new List<PlayerFurnitureItem> { present }
                });

                await recipientLogic.NetworkObject.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
            }
        }

        // Confirm the purchase to the buyer.
        await client.WriteToStreamAsync(new CatalogPurchaseOkWriter
        {
            Id = catalogItem.Id,
            Name = catalogItem.Name,
            Rented = false,
            CostCredits = catalogItem.CostCredits,
            CostPoints = catalogItem.CostPoints,
            CostPointsType = catalogItem.CostPointsType,
            CanGift = false,
            FurnitureItems = catalogItem.FurnitureItems,
            Amount = 1,
            ClubLevel = catalogItem.RequiresClubMembership ? 1 : 0,
            CanPurchaseBundles = false,
            Metadata = catalogItem.MetaData,
            IsLimited = false,
            LimitedItemSeriesSize = 0,
            AmountLeft = 0
        });
    }
}
