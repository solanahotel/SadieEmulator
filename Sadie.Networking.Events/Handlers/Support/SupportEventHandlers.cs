using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Support;

// Support / ticket packets (9030-9036). Staff actions are gated on the "admin" permission.

[PacketId(9030)]
public class SupportOpenEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public string Subject { get; set; } = "";
    public string Category { get; set; } = "general";
    public string Body { get; set; } = "";

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var (ok, error, ticketId) = await SupportService.OpenTicketAsync(dbContextFactory, client.Player, Subject, Category, Body);
        await client.WriteToStreamAsync(new SupportResultWriter { Ok = ok, Message = ok ? "Ticket opened!" : error });
        if (ok)
        {
            var ticket = await SupportService.GetTicketAsync(dbContextFactory, client.Player.Id, client.Player.HasPermission("admin"), ticketId);
            if (ticket != null) await client.WriteToStreamAsync(new SupportTicketWriter { Ticket = ticket });
        }
    }
}

[PacketId(9031)]
public class SupportListEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int Scope { get; set; } // 0 = my tickets, 1 = all (staff)

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var staffViewAll = Scope == 1 && client.Player.HasPermission("admin");
        var tickets = await SupportService.ListTicketsAsync(dbContextFactory, client.Player.Id, staffViewAll);
        await client.WriteToStreamAsync(new SupportTicketsWriter { Tickets = tickets });
    }
}

[PacketId(9032)]
public class SupportGetEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int TicketId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var ticket = await SupportService.GetTicketAsync(dbContextFactory, client.Player.Id, client.Player.HasPermission("admin"), TicketId);
        if (ticket == null) { await client.WriteToStreamAsync(new SupportResultWriter { Ok = false, Message = "Ticket not available." }); return; }
        await client.WriteToStreamAsync(new SupportTicketWriter { Ticket = ticket });
    }
}

[PacketId(9033)]
public class SupportPostEventHandler(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int TicketId { get; set; }
    public string Body { get; set; } = "";

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var isStaff = client.Player.HasPermission("admin");
        var (ok, error) = await SupportService.PostMessageAsync(playerRepository, dbContextFactory, client.Player.Id, client.Player.Username, isStaff, TicketId, Body);
        if (!ok) { await client.WriteToStreamAsync(new SupportResultWriter { Ok = false, Message = error }); return; }
        var ticket = await SupportService.GetTicketAsync(dbContextFactory, client.Player.Id, isStaff, TicketId);
        if (ticket != null) await client.WriteToStreamAsync(new SupportTicketWriter { Ticket = ticket });
    }
}

[PacketId(9034)]
public class SupportCloseEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int TicketId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var isStaff = client.Player.HasPermission("admin");
        var (ok, error) = await SupportService.CloseTicketAsync(dbContextFactory, client.Player.Id, isStaff, TicketId);
        await client.WriteToStreamAsync(new SupportResultWriter { Ok = ok, Message = ok ? "Ticket closed." : error });
        if (ok)
        {
            var ticket = await SupportService.GetTicketAsync(dbContextFactory, client.Player.Id, isStaff, TicketId);
            if (ticket != null) await client.WriteToStreamAsync(new SupportTicketWriter { Ticket = ticket });
        }
    }
}

[PacketId(9035)]
public class SupportRestrictEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int TargetId { get; set; }
    public bool Banned { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || !client.Player.HasPermission("admin")) return;
        await SupportService.SetBanAsync(dbContextFactory, TargetId, Banned, $"By {client.Player.Username}");
        await client.WriteToStreamAsync(new SupportResultWriter { Ok = true, Message = Banned ? "User restricted from tickets." : "Restriction lifted." });
    }
}

// Poll one ticket for live updates (returns the silent-update writer, so the client refreshes the open
// conversation without changing the user's view). Catches replies made from Housekeeping too.
[PacketId(9037)]
public class SupportPollEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int TicketId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var ticket = await SupportService.GetTicketAsync(dbContextFactory, client.Player.Id, client.Player.HasPermission("admin"), TicketId);
        if (ticket != null) await client.WriteToStreamAsync(new SupportTicketUpdateWriter { Ticket = ticket });
    }
}

[PacketId(9036)]
public class SupportGuideCheckEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var show = await SupportService.ConsumeGuideFlagAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new SupportGuideWriter { Show = show });
    }
}
