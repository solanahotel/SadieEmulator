using Sadie.API;
using Sadie.API.Networking;
using Sadie.Db.Models.Rooms.Chat;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Drop-in replacement for Sadie.Networking.Writers.Moderation.ModToolRoomChatLogWriter
// (PacketId 3434, moderator "Chat Logs"). The shipped writer serialized the chat-message
// COUNT with WriteInteger (4 bytes), but the client's ChatRecordData parser reads it with
// readShort (2 bytes) — so the count came through as 0 and the stream desynced, leaving
// the chat log empty. This matches the parser exactly:
//   recordType(byte)=1, contextCount(short)=2, ["roomName" string][roomName],
//   ["roomId" int][roomId], chatCount(SHORT), then per line:
//   timestamp(string), habboId(int), username(string), message(string), highlight(bool).
[PacketId(3434)]
public class FixedRoomChatLogWriter : AbstractPacketWriter
{
    public required int RoomId { get; init; }
    public required string RoomName { get; init; }
    public required List<RoomChatMessage> Messages { get; init; }

    // In-memory chat messages are created with a PlayerId but no Player navigation, so the
    // username is resolved by the handler (online users + DB) and passed in here.
    public required IReadOnlyDictionary<long, string> UsernamesById { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteByte(1); // ChatRecordData.TYPE_ROOM_CHAT

        writer.WriteShort(2); // context entries: roomName, roomId

        writer.WriteString("roomName");
        writer.WriteByte(2); // value type 2 = string
        writer.WriteString(RoomName);

        writer.WriteString("roomId");
        writer.WriteByte(1); // value type 1 = int
        writer.WriteInteger(RoomId);

        writer.WriteShort((short) Messages.Count);

        foreach (var message in Messages)
        {
            var username = message.Player?.Username
                ?? (UsernamesById.TryGetValue(message.PlayerId, out var name) ? name : "");

            writer.WriteString(message.CreatedAt.ToString("HH:mm"));
            writer.WriteInteger((int) message.PlayerId);
            writer.WriteString(username);
            writer.WriteString(message.Message ?? "Unable to display message");
            writer.WriteBool(false);
        }
    }
}
