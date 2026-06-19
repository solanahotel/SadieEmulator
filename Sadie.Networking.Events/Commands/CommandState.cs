using System.Collections.Concurrent;

namespace Sadie.Networking.Events.Commands;

// Runtime state shared by chat commands and the systems they affect. Static so the chat
// handler / roller task can read them without DI plumbing into compiled assemblies.

// Per-room chat mutes (set by :mute / :room mute, checked in OnChatMessageAsync).
public static class ChatMuteStore
{
    private static readonly ConcurrentDictionary<(long Room, long Player), byte> MutedInRoom = new();
    private static readonly ConcurrentDictionary<long, byte> MutedRooms = new();

    public static void MutePlayer(long roomId, long playerId) => MutedInRoom[(roomId, playerId)] = 1;
    public static void UnmutePlayer(long roomId, long playerId) => MutedInRoom.TryRemove((roomId, playerId), out _);
    public static void MuteRoom(long roomId) => MutedRooms[roomId] = 1;
    public static void UnmuteRoom(long roomId) => MutedRooms.TryRemove(roomId, out _);

    public static bool IsMuted(long roomId, long playerId) =>
        MutedRooms.ContainsKey(roomId) || MutedInRoom.ContainsKey((roomId, playerId));
}

// Players currently invisible (set by :invisible, used to know whether to hide or show).
public static class InvisibleStore
{
    private static readonly ConcurrentDictionary<long, byte> Invisible = new();

    public static bool IsInvisible(long playerId) => Invisible.ContainsKey(playerId);
    public static void SetInvisible(long playerId, bool invisible)
    {
        if (invisible) Invisible[playerId] = 1;
        else Invisible.TryRemove(playerId, out _);
    }
}

// Global timed chat mutes (set by the mod-tool Mute sanction, checked in OnChatMessageAsync).
public static class GlobalMuteStore
{
    private static readonly ConcurrentDictionary<long, DateTime> MutedUntil = new();

    public static void Mute(long playerId, TimeSpan duration) => MutedUntil[playerId] = DateTime.Now.Add(duration);
    public static void Unmute(long playerId) => MutedUntil.TryRemove(playerId, out _);

    public static bool IsMuted(long playerId)
    {
        if (!MutedUntil.TryGetValue(playerId, out var until)) return false;
        if (DateTime.Now >= until) { MutedUntil.TryRemove(playerId, out _); return false; }
        return true;
    }
}

// Roller / furniture-processing cycle length in ms (set by :setspeed, read by the task).
public static class RollerSpeedConfig
{
    public static int CycleMs = 1333;
}
