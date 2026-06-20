using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Navigator;
using Sadie.API.Game.Rooms;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Db.Models.Navigator;
using Sadie.Db.Models.Rooms;
using Sadie.Enums.Game.Players;
using Sadie.Networking.Writers.Navigator;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Navigator;

[PacketId(EventHandlerId.NavigatorSearch)]
public class NavigatorSearchEventHandler(
    IDbContextFactory<SadieDbContext> dbContextFactory,
    INavigatorRoomProvider navigatorRoomProvider,
    IRoomRepository roomRepository)
    : INetworkPacketEventHandler
{
    public string? TabName { get; set; }
    public string? SearchQuery { get; set; }
    
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null)
        {
            return;
        }
        
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        
        var tab = await dbContext.Set<NavigatorTab>()
            .Include(x => x.Categories)
            .FirstOrDefaultAsync(x => x.Name == TabName);

        if (tab == null)
        {
            return;
        }

        var categories = tab.
            Categories.
            OrderBy(x => x.OrderId).
            ToList();

        var categoryRoomMap = new Dictionary<NavigatorCategory, List<Room>>();

        if (!string.IsNullOrEmpty(SearchQuery))
        {
            categoryRoomMap[new NavigatorCategory
            {
                Name = "Search Results",
                CodeName = "",
                OrderId = 0,
                TabId = 1
            }] = await navigatorRoomProvider.GetRoomsForSearchQueryAsync(SearchQuery);
        }
        else
        {
            foreach (var category in categories)
            {
                categoryRoomMap.Add(category, await navigatorRoomProvider.GetRoomsForCategoryNameAsync(client.Player, category.CodeName));
            }
        }
        
        // "Invisible in navigator" (access_type 3): hide those rooms from normal players. Admins
        // (any-room-rights) see every room regardless, and owners always see their own.
        if (!client.Player.HasPermission(PlayerPermissionName.AnyRoomRights))
        {
            var invisibleIds = (await dbContext.Database
                .SqlQueryRaw<long>("SELECT room_id AS Value FROM room_settings WHERE access_type = 3")
                .ToListAsync()).ToHashSet();

            if (invisibleIds.Count > 0)
            {
                foreach (var key in categoryRoomMap.Keys.ToList())
                {
                    categoryRoomMap[key] = categoryRoomMap[key]
                        .Where(r => !invisibleIds.Contains(r.Id) || r.OwnerId == client.Player.Id)
                        .ToList();
                }
            }
        }

        var searchResultPagesWriter = new NavigatorSearchResultPagesWriter
        {
            TabName = TabName,
            SearchQuery = SearchQuery,
            CategoryRoomMap = categoryRoomMap,
            RoomRepository = roomRepository
        };
        
        await client.WriteToStreamAsync(searchResultPagesWriter);
    }
}