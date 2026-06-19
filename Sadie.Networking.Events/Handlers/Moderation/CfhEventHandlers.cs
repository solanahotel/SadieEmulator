using Sadie.API;
using Sadie.API.Game.Players;
using Sadie.API.Networking;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Networking.Events.Moderation;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Players;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Moderation;

internal static class ModBroadcast
{
    private const string Permission = "moderator";

    public static async Task ToModeratorsAsync(IPlayerRepository playerRepository, AbstractPacketWriter writer)
    {
        foreach (var player in playerRepository.GetAll())
        {
            if (player.NetworkObject != null && player.HasPermission(Permission))
            {
                await player.NetworkObject.WriteToStreamAsync(writer);
            }
        }
    }
}

// Player submits a Call for Help (CALL_FOR_HELP = 1691). Wire: message, topicIndex, reportedUserId,
// reportedRoomId, then the chat-evidence entries (which we don't need for the ticket itself).
[PacketId(1691)]
public class CallForHelpEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public string Message { get; set; } = "";
    public int TopicIndex { get; set; }
    public int ReportedUserId { get; set; }
    public int ReportedRoomId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        var reported = playerRepository.GetPlayerLogicById(ReportedUserId);

        var issue = CfhIssueStore.Add(
            (int) client.Player.Id, client.Player.Username,
            ReportedUserId, reported?.Username ?? "",
            TopicIndex, Message, ReportedRoomId);

        await ModBroadcast.ToModeratorsAsync(playerRepository, new IssueInfoWriter { Issue = issue.ToIssueData() });
    }
}

// Moderator picks an issue (PICK_ISSUES = 15). Wire: count, issueId(s), retryEnabled, retryCount, message.
// The client always sends a single issue, so we read count + the first id.
[PacketId(15)]
public class PickIssuesEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int Count { get; set; }
    public int IssueId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || !client.Player.HasPermission("moderator")) return;

        if (!CfhIssueStore.TryGet(IssueId, out var issue))
        {
            await client.WriteToStreamAsync(new PlayerAlertWriter { Message = "That ticket no longer exists." });
            return;
        }

        // Already held by a different moderator.
        if (issue.State == 2 && issue.PickerUserId != (int) client.Player.Id)
        {
            await client.WriteToStreamAsync(new PlayerAlertWriter { Message = $"That ticket is already being handled by {issue.PickerUsername}." });
            return;
        }

        issue.State = 2;
        issue.PickerUserId = (int) client.Player.Id;
        issue.PickerUsername = client.Player.Username;

        await ModBroadcast.ToModeratorsAsync(playerRepository, new IssueInfoWriter { Issue = issue.ToIssueData() });
    }
}

// Moderator resolves/closes an issue (CLOSE_ISSUES = 2067). Wire: resolutionType, count, issueId(s).
[PacketId(2067)]
public class CloseIssuesEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int ResolutionType { get; set; }
    public int Count { get; set; }
    public int IssueId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || !client.Player.HasPermission("moderator")) return;

        CfhIssueStore.Remove(IssueId);

        await ModBroadcast.ToModeratorsAsync(playerRepository, new IssueDeletedWriter { IssueId = IssueId });
    }
}

// Moderator releases a picked issue back to the open queue (RELEASE_ISSUES = 1572). Wire: count, issueId(s).
[PacketId(1572)]
public class ReleaseIssuesEventHandler(IPlayerRepository playerRepository) : INetworkPacketEventHandler
{
    public int Count { get; set; }
    public int IssueId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || !client.Player.HasPermission("moderator")) return;

        if (!CfhIssueStore.TryGet(IssueId, out var issue)) return;

        issue.State = 1;
        issue.PickerUserId = 0;
        issue.PickerUsername = "";

        await ModBroadcast.ToModeratorsAsync(playerRepository, new IssueInfoWriter { Issue = issue.ToIssueData() });
    }
}
