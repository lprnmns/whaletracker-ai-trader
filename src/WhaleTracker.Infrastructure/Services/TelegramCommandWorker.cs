using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;

namespace WhaleTracker.Infrastructure.Services;

public sealed class TelegramCommandWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramCommandWorker> _logger;
    private readonly TelegramSettings _settings;
    private long _offset;

    public TelegramCommandWorker(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ILogger<TelegramCommandWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value.Telegram;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled ||
            string.IsNullOrWhiteSpace(_settings.BotToken) ||
            string.IsNullOrWhiteSpace(_settings.ChatId))
        {
            return;
        }

        _logger.LogInformation("Telegram command worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram command polling failed.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://api.telegram.org/bot{_settings.BotToken}/getUpdates?timeout=20&offset={_offset}";
        var updates = await client.GetFromJsonAsync<TelegramUpdatesResponse>(url, cancellationToken);
        if (updates?.Ok != true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return;
        }

        foreach (var update in updates.Result)
        {
            _offset = Math.Max(_offset, update.UpdateId + 1);
            var message = update.Message;
            if (message == null || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var chatId = message.Chat.Id.ToString();
            if (!string.Equals(chatId, _settings.ChatId, StringComparison.Ordinal))
            {
                continue;
            }

            var text = await HandleCommandAsync(message.Text.Trim(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                await SendAsync(chatId, text, cancellationToken);
            }
        }
    }

    private async Task<string> HandleCommandAsync(string command, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        if (command.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            return """
                WhaleTracker komutlari:
                /status
                /leaderboard
                /positions
                /positions all
                /pnl
                """;
        }

        if (command.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            var enabled = await db.HyperliquidCopyTraders.CountAsync(x => x.IsEnabled, cancellationToken);
            var real = await db.HyperliquidCopyTraders.CountAsync(x => x.IsEnabled && x.ExecuteOrders, cancellationToken);
            var scores = await db.HyperliquidLiveScoreSnapshots
                .GroupBy(x => x.TraderAddress)
                .CountAsync(cancellationToken);
            var active = await db.HyperliquidLivePositions
                .CountAsync(x => x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN", cancellationToken);
            var liveActive = await db.HyperliquidLivePositions
                .CountAsync(x => x.OpenedFromTracking && x.Status == "LIVE_OPEN", cancellationToken);
            var baselineActive = await db.HyperliquidLivePositions
                .CountAsync(x => !x.OpenedFromTracking && x.Status == "BASELINE_OPEN", cancellationToken);
            var closed = await db.HyperliquidLivePositions
                .CountAsync(x => x.OpenedFromTracking && x.Status == "CLOSED", cancellationToken);
            var fills = await db.HyperliquidLiveFills.CountAsync(cancellationToken);
            return $"Durum\nEnabled trader: {enabled}\nReal execution: {real}\nScore uretilen: {scores}\nAktif toplam: {active}\nYeni/live aktif: {liveActive}\nBaseline aktif: {baselineActive}\nLive kapanan: {closed}\nKayitli fill: {fills}";
        }

        if (command.StartsWith("/leaderboard", StringComparison.OrdinalIgnoreCase))
        {
            var rows = (await db.HyperliquidLiveScoreSnapshots
                    .AsNoTracking()
                    .OrderByDescending(x => x.ScoredAt)
                    .Take(1000)
                    .ToListAsync(cancellationToken))
                .GroupBy(x => x.TraderAddress, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderByDescending(x => x.LiveScore)
                .ThenByDescending(x => x.Confidence)
                .Take(10)
                .ToList();
            if (rows.Count == 0)
            {
                return "No live scores yet.";
            }

            return "Live leaderboard\n" +
                "Not: PnL sadece takipten sonra acilan/kapanan pozisyonlardan gelir; baseline eski pozisyonlar skora girmez.\n" +
                string.Join("\n", rows.Select((row, index) =>
                    $"{index + 1}. {ShortAddress(row.TraderAddress)} score {row.LiveScore:0.0} conf {row.Confidence:0.0} livePnL ${row.NetPnlUsd:0.##} acct {row.PnlPctAccount:0.###}% liveAct {row.ActivePositions} closed {row.ClosedPositions} win {row.WinRate:0.#}%"));
        }

        if (command.StartsWith("/positions", StringComparison.OrdinalIgnoreCase))
        {
            var includeBaseline = command.Contains(" all", StringComparison.OrdinalIgnoreCase);
            var rows = (await db.HyperliquidLivePositions
                .AsNoTracking()
                .Where(x =>
                    includeBaseline
                        ? x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN"
                        : x.OpenedFromTracking && x.Status == "LIVE_OPEN")
                .OrderByDescending(x => x.LastSeenAt)
                .Take(200)
                .ToListAsync(cancellationToken))
                .OrderByDescending(x => Math.Abs(x.UnrealizedPnlUsd))
                .Take(12)
                .ToList();
            if (rows.Count == 0)
            {
                return includeBaseline
                    ? "Aktif source pozisyon yok."
                    : "Takip basladiktan sonra acilan aktif pozisyon yok. Eski aciklari gormek icin /positions all kullan.";
            }

            return (includeBaseline ? "Aktif pozisyonlar live+baseline\n" : "Yeni/live aktif pozisyonlar\n") +
                string.Join("\n", rows.Select(row =>
                    $"{ShortAddress(row.TraderAddress)} {row.OkxSymbol} {row.Side.ToUpperInvariant()} {row.PositionPctOfAccount:0.##}% acc value ${row.CurrentNotionalUsd:0.##} uPnL ${row.UnrealizedPnlUsd:0.##} {row.CopyStatus} {(row.OpenedFromTracking ? "live" : "base")}"));
        }

        if (command.StartsWith("/pnl", StringComparison.OrdinalIgnoreCase))
        {
            var latest = (await db.HyperliquidLiveScoreSnapshots
                    .AsNoTracking()
                    .OrderByDescending(x => x.ScoredAt)
                    .Take(1000)
                    .ToListAsync(cancellationToken))
                .GroupBy(x => x.TraderAddress, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            return $"Live PnL\nSadece takipten sonra acilan pozisyonlar.\nRealized: ${latest.Sum(x => x.RealizedPnlUsd):0.##}\nUnrealized: ${latest.Sum(x => x.UnrealizedPnlUsd):0.##}\nNet: ${latest.Sum(x => x.NetPnlUsd):0.##}";
        }

        return "Unknown command. Use /help.";
    }

    private async Task SendAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        await client.PostAsJsonAsync(
            $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage",
            new
            {
                chat_id = chatId,
                text,
                disable_web_page_preview = true
            },
            cancellationToken);
    }

    private static string ShortAddress(string address) =>
        address.Length <= 12 ? address : $"{address[..6]}...{address[^4..]}";

    private sealed class TelegramUpdatesResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public List<TelegramUpdate> Result { get; set; } = new();
    }

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; set; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("chat")]
        public TelegramChat Chat { get; set; } = new();

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    private sealed class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}
