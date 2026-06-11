using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using WhaleTracker.API.Hubs;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.API.Services;

public class SignalRLiveEventPublisher : ILiveEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WhaleTrackerDbContext _db;
    private readonly IHubContext<MissionControlHub> _hub;

    public SignalRLiveEventPublisher(WhaleTrackerDbContext db, IHubContext<MissionControlHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task<LiveEventEnvelope> PublishAsync(
        string type,
        string summary,
        string walletAddress = "",
        string txHash = "",
        string symbol = "",
        decimal? usdValue = null,
        object? payload = null,
        string severity = "info",
        CancellationToken cancellationToken = default)
    {
        var entity = new LiveEventEntity
        {
            Type = type,
            Severity = string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim(),
            WalletAddress = walletAddress.Trim().ToLowerInvariant(),
            TxHash = txHash.Trim(),
            Symbol = symbol.Trim().ToUpperInvariant(),
            UsdValue = usdValue,
            Summary = summary.Trim(),
            PayloadJson = payload == null ? "{}" : JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = DateTime.UtcNow
        };

        _db.LiveEvents.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        var envelope = new LiveEventEnvelope
        {
            Id = entity.Id,
            Type = entity.Type,
            Severity = entity.Severity,
            WalletAddress = entity.WalletAddress,
            TxHash = entity.TxHash,
            Symbol = entity.Symbol,
            UsdValue = entity.UsdValue,
            Summary = entity.Summary,
            PayloadJson = entity.PayloadJson,
            CreatedAt = entity.CreatedAt
        };

        await _hub.Clients.All.SendAsync("liveEvent", envelope, cancellationToken);
        return envelope;
    }
}
