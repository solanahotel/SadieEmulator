using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Players.Other;
using Sadie.Networking.Writers.Players.Purse;

namespace Sadie.Networking.Events.Achievements;

// Computed per-achievement view sent to the client (mirrors the Nitro AchievementData fields).
public sealed class AchievementView
{
    public int AchievementId { get; init; }
    public int Level { get; init; }
    public string BadgeCode { get; init; } = "";
    public int ScoreAtStartOfLevel { get; init; }
    public int ScoreLimit { get; init; }
    public int LevelRewardPoints { get; init; }
    public int LevelRewardPointType { get; init; }
    public int CurrentPoints { get; init; }
    public bool FinalLevel { get; init; }
    public string Category { get; init; } = "";
    public string SubCategory { get; init; } = "";
    public int LevelCount { get; init; }
    public int DisplayMethod { get; init; }
}

internal sealed class AchievementLevel
{
    public int Level { get; init; }
    public int ProgressNeeded { get; init; }
    public int Points { get; init; }
    public int RewardAmount { get; init; }
    public int RewardType { get; init; }
}

internal sealed class AchievementGroup
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public int GroupId { get; init; }
    public required List<AchievementLevel> Levels { get; init; } // ascending by level
    public int MaxLevel => Levels.Count == 0 ? 0 : Levels[^1].Level;

    public AchievementLevel? GetLevel(int level) => Levels.FirstOrDefault(x => x.Level == level);

    // Cumulative progress threshold to COMPLETE a given level (progress_needed is cumulative).
    public int ThresholdFor(int level) => GetLevel(level)?.ProgressNeeded ?? 0;
}

// The achievement engine: a cached catalog (the `achievements` table), per-player progress in
// `player_achievement_progress`, and the level-up rewards (achievement score, pixels, badge). It was
// entirely missing — the old handler sent an empty list and nothing ever granted progress.
public static class AchievementService
{
    private static Dictionary<string, AchievementGroup>? _byName;
    private static List<AchievementGroup>? _ordered;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string BadgeCodeFor(string name, int level) => $"ACH_{name}{level}";

    public static async Task EnsureLoadedAsync(IDbContextFactory<SadieDbContext> dbContextFactory)
    {
        if (_byName != null) return;

        await Gate.WaitAsync();
        try
        {
            if (_byName != null) return;

            var groups = new Dictionary<string, AchievementGroup>();
            var levelsByName = new Dictionary<string, List<AchievementLevel>>();
            var categoryByName = new Dictionary<string, string>();
            var minIdByName = new Dictionary<string, int>();

            await using var dbContext = await dbContextFactory.CreateDbContextAsync();
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, category, level, reward_amount, reward_type, points, progress_needed FROM achievements ORDER BY name, level";

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.GetInt32(0);
                    var name = reader.GetString(1);
                    var category = reader.GetString(2);

                    if (!levelsByName.TryGetValue(name, out var levels))
                    {
                        levels = [];
                        levelsByName[name] = levels;
                        categoryByName[name] = category;
                        minIdByName[name] = id;
                    }

                    if (id < minIdByName[name]) minIdByName[name] = id;

                    levels.Add(new AchievementLevel
                    {
                        Level = reader.GetInt32(3),
                        RewardAmount = reader.GetInt32(4),
                        RewardType = reader.GetInt32(5),
                        Points = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        ProgressNeeded = reader.GetInt32(7)
                    });
                }
            }
            finally
            {
                await connection.CloseAsync();
            }

            foreach (var (name, levels) in levelsByName)
            {
                levels.Sort((a, b) => a.Level.CompareTo(b.Level));
                groups[name] = new AchievementGroup
                {
                    Name = name,
                    Category = categoryByName[name],
                    GroupId = minIdByName[name],
                    Levels = levels
                };
            }

            _byName = groups;
            _ordered = groups.Values.OrderBy(x => x.GroupId).ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<List<AchievementView>> BuildListAsync(IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await EnsureLoadedAsync(dbContextFactory);

        var progress = await LoadProgressAsync(dbContextFactory, playerId);
        var views = new List<AchievementView>(_ordered!.Count);

        foreach (var group in _ordered!)
        {
            progress.TryGetValue(group.Name, out var p);
            views.Add(BuildView(group, p.Progress, p.Level));
        }

        return views;
    }

    private static AchievementView BuildView(AchievementGroup group, int progress, int completedLevel)
    {
        var maxLevel = group.MaxLevel;
        var maxed = completedLevel >= maxLevel;
        var displayLevel = maxed ? maxLevel : completedLevel + 1;
        var levelData = group.GetLevel(displayLevel) ?? group.Levels[^1];

        var start = displayLevel <= 1 ? 0 : group.ThresholdFor(displayLevel - 1);
        var limit = group.ThresholdFor(displayLevel);

        return new AchievementView
        {
            AchievementId = group.GroupId,
            Level = displayLevel,
            BadgeCode = BadgeCodeFor(group.Name, displayLevel),
            ScoreAtStartOfLevel = start,
            ScoreLimit = Math.Max(start + 1, limit),
            LevelRewardPoints = levelData.Points,
            LevelRewardPointType = -1, // credits
            CurrentPoints = maxed ? limit : progress,
            FinalLevel = displayLevel >= maxLevel,
            Category = group.Category,
            SubCategory = group.Category,
            LevelCount = maxLevel,
            DisplayMethod = 0
        };
    }

    private static async Task<Dictionary<string, (int Progress, int Level)>> LoadProgressAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        var result = new Dictionary<string, (int, int)>();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT achievement_name, progress, level FROM player_achievement_progress WHERE player_id = @p";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p";
            parameter.Value = playerId;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        return result;
    }

    // Increment a player's progress on an achievement by `amount`. If `daily`, it only counts once
    // per calendar day (used for Login / hotel-presence style achievements).
    public static Task AddProgressAsync(IDbContextFactory<SadieDbContext> dbContextFactory, IPlayerLogic player, string name, int amount, bool daily = false) =>
        ApplyAsync(dbContextFactory, player, name, amount, absolute: false, daily: daily);

    // Set a player's progress to an absolute value (used for count-based achievements like friends or
    // days registered, where we know the current total).
    public static Task SetProgressAsync(IDbContextFactory<SadieDbContext> dbContextFactory, IPlayerLogic player, string name, int value) =>
        ApplyAsync(dbContextFactory, player, name, value, absolute: true, daily: false);

    private static async Task ApplyAsync(IDbContextFactory<SadieDbContext> dbContextFactory, IPlayerLogic player, string name, int amount, bool absolute, bool daily)
    {
        await EnsureLoadedAsync(dbContextFactory);

        if (_byName == null || !_byName.TryGetValue(name, out var group) || group.MaxLevel == 0)
        {
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // Current stored progress.
        var current = (await dbContext.Database
            .SqlQueryRaw<int>("SELECT progress AS Value FROM player_achievement_progress WHERE player_id = {0} AND achievement_name = {1}", player.Id, name)
            .ToListAsync()).Cast<int?>().FirstOrDefault();

        var oldLevel = (await dbContext.Database
            .SqlQueryRaw<int>("SELECT level AS Value FROM player_achievement_progress WHERE player_id = {0} AND achievement_name = {1}", player.Id, name)
            .ToListAsync()).FirstOrDefault();

        if (daily)
        {
            var todayCounts = (await dbContext.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM player_achievement_progress WHERE player_id = {0} AND achievement_name = {1} AND last_progress_at IS NOT NULL AND DATE(last_progress_at) = CURDATE()", player.Id, name)
                .ToListAsync()).FirstOrDefault();

            if (todayCounts > 0) return; // already counted today
        }

        var oldProgress = current ?? 0;
        var newProgress = absolute ? Math.Max(oldProgress, amount) : oldProgress + amount;

        var maxThreshold = group.ThresholdFor(group.MaxLevel);
        if (newProgress > maxThreshold) newProgress = maxThreshold;

        if (newProgress == oldProgress && current.HasValue && !daily) return; // nothing changed

        var newLevel = 0;
        foreach (var level in group.Levels)
        {
            if (newProgress >= level.ProgressNeeded) newLevel = level.Level;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_achievement_progress (player_id, achievement_name, progress, level, last_progress_at) VALUES ({0}, {1}, {2}, {3}, {4}) " +
            "ON DUPLICATE KEY UPDATE progress = VALUES(progress), level = VALUES(level), last_progress_at = VALUES(last_progress_at)",
            player.Id, name, newProgress, newLevel, DateTime.Now);

        var leveledUp = newLevel > oldLevel;

        if (leveledUp)
        {
            await GrantLevelRewardsAsync(dbContext, player, group, oldLevel, newLevel);
        }

        // Always refresh the single achievement so the progress bar moves client-side.
        var network = player.NetworkObject;
        if (network != null)
        {
            await network.WriteToStreamAsync(new AchievementUpdateWriter { Achievement = BuildView(group, newProgress, newLevel) });
        }
    }

    private static async Task GrantLevelRewardsAsync(SadieDbContext dbContext, IPlayerLogic player, AchievementGroup group, int oldLevel, int newLevel)
    {
        var scoreGain = 0;
        var creditGain = 0;

        for (var level = oldLevel + 1; level <= newLevel; level++)
        {
            var data = group.GetLevel(level);
            if (data == null) continue;
            scoreGain += data.Points;
            // All achievements reward Credits (any configured reward amount, regardless of its type).
            creditGain += data.RewardAmount;
        }

        if (scoreGain != 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE player_data SET achievement_score = achievement_score + {0} WHERE player_id = {1}", scoreGain, player.Id);
        }

        if (creditGain != 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE player_data SET credit_balance = credit_balance + {0} WHERE player_id = {1}", creditGain, player.Id);

            // Sync the live balance + client display.
            var balance = (await dbContext.Database.SqlQueryRaw<int>(
                "SELECT credit_balance AS Value FROM player_data WHERE player_id = {0}", player.Id).ToListAsync()).FirstOrDefault();
            player.Data.CreditBalance = balance;
            if (player.NetworkObject != null)
                await player.NetworkObject.WriteToStreamAsync(new PlayerCreditsBalanceWriter { Credits = balance });
        }

        // Badge: replace the previous level's achievement badge with the new one.
        var newBadgeCode = BadgeCodeFor(group.Name, newLevel);
        var removedBadgeCode = oldLevel > 0 ? BadgeCodeFor(group.Name, oldLevel) : "";

        if (oldLevel > 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE pb FROM player_badges pb JOIN badges b ON b.id = pb.badge_id WHERE pb.player_id = {0} AND b.code = {1}", player.Id, removedBadgeCode);
        }

        var badgeId = await EnsureBadgeAsync(dbContext, newBadgeCode);
        if (badgeId > 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_badges (player_id, badge_id, slot) VALUES ({0}, {1}, 0)", player.Id, badgeId);
        }

        var network = player.NetworkObject;
        if (network == null) return;

        var newLevelData = group.GetLevel(newLevel);

        await network.WriteToStreamAsync(new AchievementNotificationWriter
        {
            CategoryType = 0,
            Level = newLevel,
            BadgeId = badgeId,
            BadgeCode = newBadgeCode,
            Points = newLevelData?.Points ?? 0,
            LevelRewardPoints = newLevelData?.RewardAmount ?? 0,
            LevelRewardPointType = -1, // credits
            BonusPoints = 0,
            AchievementId = group.GroupId,
            RemovedBadgeCode = removedBadgeCode,
            Category = group.Category,
            ShowDialogToUser = true
        });

        var newScore = (player.Data?.AchievementScore ?? 0) + scoreGain;
        await network.WriteToStreamAsync(new PlayerAchievementScoreWriter { AchievementScore = newScore });
    }

    // badges.code is a longtext, so look it up (no unique index) and create it on demand.
    private static async Task<int> EnsureBadgeAsync(SadieDbContext dbContext, string code)
    {
        var existing = (await dbContext.Database
            .SqlQueryRaw<int>("SELECT id AS Value FROM badges WHERE code = {0} LIMIT 1", code)
            .ToListAsync()).FirstOrDefault();

        if (existing > 0) return existing;

        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("INSERT INTO badges (code) VALUES ({0})", code);
            return (int) (await dbContext.Database
                .SqlQueryRaw<long>("SELECT LAST_INSERT_ID() AS Value")
                .ToListAsync()).FirstOrDefault();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
