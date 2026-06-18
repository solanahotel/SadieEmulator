using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Sadie.API.Game.Players;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Db.Models.Players;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Players.Permission;
using Sadie.Networking.Writers.Players.Subscriptions;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Club;

// Server-to-server client for the CMS Solana verifier (X-Internal-Secret). The CMS
// owns the on-chain verification + anti-replay; the emulator only grants membership
// once the CMS confirms a finalized payment. Fail-closed: any error => null.
internal static class CmsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<JsonElement?> PostAsync(IConfiguration config, string path, object body)
    {
        try
        {
            var baseUrl = config["Cms:BaseUrl"];
            var secret = config["Cms:InternalSecret"];

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(secret))
            {
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}");
            req.Headers.Add("X-Internal-Secret", secret);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}

[PacketId(EventHandlerId.ClubPaymentIntent)]
public class ClubPaymentIntentEventHandler(IConfiguration config) : INetworkPacketEventHandler
{
    public int PackageId { get; init; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null)
        {
            return;
        }

        var resp = await CmsClient.PostAsync(config, "/api/internal/club/intent",
            new { player_id = client.Player.Id, package_id = PackageId });

        if (resp is not { } r || !r.TryGetProperty("treasury", out _))
        {
            var error = resp is { } e && e.TryGetProperty("error", out var ee) ? ee.GetString() : "intent_failed";

            await client.WriteToStreamAsync(new ClubPaymentIntentResultWriter
            {
                Ok = false,
                PackageId = PackageId,
                Error = error ?? "intent_failed"
            });
            return;
        }

        await client.WriteToStreamAsync(new ClubPaymentIntentResultWriter
        {
            Ok = true,
            PackageId = PackageId,
            Treasury = r.GetProperty("treasury").GetString() ?? "",
            Lamports = r.GetProperty("lamports").GetInt64().ToString(),
            Sol = r.GetProperty("sol").GetDouble().ToString(CultureInfo.InvariantCulture),
            Reference = r.GetProperty("reference").GetString() ?? "",
            Network = r.GetProperty("network").GetString() ?? "",
            PriceUsd = r.GetProperty("price_usd").GetDouble().ToString(CultureInfo.InvariantCulture)
        });
    }
}

[PacketId(EventHandlerId.ClubPaymentSubmit)]
public class ClubPaymentSubmitEventHandler(
    IConfiguration config,
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IPlayerHelperService playerHelperService) : INetworkPacketEventHandler
{
    public int PackageId { get; init; }
    public string Signature { get; init; } = "";

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null)
        {
            return;
        }

        var resp = await CmsClient.PostAsync(config, "/api/internal/club/verify",
            new { player_id = client.Player.Id, package_id = PackageId, signature = Signature });

        var ok = resp is { } root && root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();

        if (!ok)
        {
            var error = resp is { } e && e.TryGetProperty("error", out var ee) ? ee.GetString() : "verification_failed";

            await client.WriteToStreamAsync(new ClubPaymentResultWriter
            {
                Ok = false,
                Message = error ?? "verification_failed"
            });
            return;
        }

        var durationDays = resp!.Value.GetProperty("duration_days").GetInt32();
        var now = DateTime.Now;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var subscription = await dbContext.Subscriptions.FirstOrDefaultAsync(x => x.Name == "HABBO_CLUB");

        if (subscription == null)
        {
            await client.WriteToStreamAsync(new ClubPaymentResultWriter { Ok = false, Message = "club_not_configured" });
            return;
        }

        // Extend an existing (active or lapsed) membership, else create a new one.
        // ExpiresAt is init-only, so extend via a raw UPDATE rather than mutating.
        var existing = await dbContext.PlayerSubscriptions
            .FirstOrDefaultAsync(x => x.PlayerId == client.Player.Id && x.SubscriptionId == subscription.Id);

        DateTimeOffset newExpiry;

        if (existing != null)
        {
            var baseTime = existing.ExpiresAt > now ? existing.ExpiresAt : now;
            newExpiry = baseTime.AddDays(durationDays);

            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE player_subscriptions SET expires_at = {0} WHERE id = {1}",
                newExpiry.DateTime, existing.Id);
        }
        else
        {
            newExpiry = now.AddDays(durationDays);
            dbContext.PlayerSubscriptions.Add(new PlayerSubscription
            {
                PlayerId = client.Player.Id,
                SubscriptionId = subscription.Id,
                CreatedAt = now,
                ExpiresAt = newExpiry
            });
            await dbContext.SaveChangesAsync();
        }

        // Reflect live in the in-memory session so the club gate works immediately
        // (replace, since ExpiresAt is init-only).
        var memSub = client.Player.Subscriptions.FirstOrDefault(x => x.Subscription?.Name == "HABBO_CLUB");

        if (memSub != null)
        {
            client.Player.Subscriptions.Remove(memSub);
        }

        client.Player.Subscriptions.Add(new PlayerSubscription
        {
            PlayerId = client.Player.Id,
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            CreatedAt = now,
            ExpiresAt = newExpiry
        });

        client.Player.State.LastSubscriptionModification = now;

        await client.WriteToStreamAsync(new PlayerPermissionsWriter
        {
            Club = 2,
            Rank = client.Player.Roles.Count != 0 ? client.Player.Roles.Max(x => x.Id) : 1,
            Ambassador = true
        });

        if (playerHelperService.GetSubscriptionWriterAsync(client.Player, "HABBO_CLUB") is PlayerSubscriptionWriter subWriter)
        {
            await client.WriteToStreamAsync(subWriter);
        }

        await client.WriteToStreamAsync(new ClubPaymentResultWriter
        {
            Ok = true,
            Message = "ok",
            DaysLeft = (int)(newExpiry - now).TotalDays
        });
    }
}
