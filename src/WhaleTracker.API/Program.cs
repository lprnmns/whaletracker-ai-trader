using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using WhaleTracker.API.Configuration;
using WhaleTracker.API.Hubs;
using WhaleTracker.API.Services;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Repositories;
using WhaleTracker.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

EnvFileLoader.LoadNearest(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("WhaleTracker.Infrastructure.Services.OkxService", LogLevel.Information);
builder.Logging.AddFilter("WhaleTracker.Infrastructure.Services.GroqService", LogLevel.Information);

// ================================================================
// YAPILANDIRMA (appsettings.json'dan okur)
// ================================================================
builder.Services.Configure<AppSettings>(builder.Configuration);

// ================================================================
// VERİTABANI (PostgreSQL + Entity Framework)
// ================================================================
builder.Services.AddDbContext<WhaleTrackerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ================================================================
// REPOSITORY (Veritabanı işlemleri)
// ================================================================
builder.Services.AddScoped<ITradeRepository, TradeRepository>();

// ================================================================
// HTTP CLIENT'LAR (Dış API'ler için)
// ================================================================
builder.Services.AddHttpClient<IZerionService, ZerionService>();
builder.Services.AddHttpClient<IOkxService, OkxService>();
builder.Services.AddHttpClient<IDecisionEngine, DecisionEngine>();
builder.Services.AddHttpClient<IAIService, GroqService>();
builder.Services.AddHttpClient<IHistoricalSwapScanner, EtherscanUniswapV3Scanner>();
builder.Services.AddHttpClient<IWalletActivityService, AlchemyWalletActivityService>();
builder.Services.AddHttpClient<INotificationService, TelegramNotificationService>();
builder.Services.AddHttpClient<ITraderPerformanceService, ZerionTraderPerformanceService>();
builder.Services.AddHttpClient<ITraderDiscoveryService, DuneTraderDiscoveryService>();

// ================================================================
// SERVİSLER
// ================================================================
builder.Services.AddScoped<IWhaleTrackerService, WhaleTrackerService>();
builder.Services.AddScoped<IInsiderDetectionService, InsiderDetectionService>();
builder.Services.AddScoped<IAiBiasMemoryService, AiBiasMemoryService>();
builder.Services.AddScoped<ILiveEventPublisher, SignalRLiveEventPublisher>();
builder.Services.AddSingleton<ITraderDiscoveryJobQueue, TraderDiscoveryJobQueue>();
builder.Services.AddSingleton<ITraderPerformanceJobQueue, TraderPerformanceJobQueue>();
builder.Services.AddHostedService<AutoTraderWorker>();
builder.Services.AddHostedService<TraderDiscoveryWorker>();
builder.Services.AddHostedService<TraderPerformanceWorker>();

// ================================================================
// AUTH (Cookie)
// ================================================================
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login.html";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (IsApiOrHubRequest(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (IsApiOrHubRequest(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Background Service - şimdilik devre dışı (test aşamasında)
// TODO: Testler tamamlandıktan sonra aktif et
// builder.Services.AddHostedService<WhaleTrackerService>();

// ================================================================
// API CONTROLLER'LAR
// ================================================================
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() 
    { 
        Title = "WhaleTracker API", 
        Version = "v1",
        Description = "Balina cüzdan takip ve kopya ticaret sistemi"
    });
});

// CORS (Web frontend için)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// ================================================================
// VERİTABANI BAŞLATMA
// ================================================================
var appSettings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppSettings>>().Value;
if (appSettings.Database.AutoEnsureCreated)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
        db.Database.EnsureCreated();
        EnsureTrackedWalletSchema(db);
        EnsureAiMemorySchema(db);
        EnsureRuntimeControlSchema(db);
        EnsureLiveEventSchema(db);
        EnsureTraderFinderSchema(db);
        EnsureTraderDiscoverySchema(db);
        app.Logger.LogInformation("Database schema ensured.");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database schema could not be ensured. Continuing without database bootstrap.");
    }
}

// ================================================================
// MIDDLEWARE PİPELİNE
// ================================================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "WhaleTracker API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<MissionControlHub>("/hubs/mission-control");

// ================================================================
// BAŞLANGIÇ MESAJI
// ================================================================
app.Logger.LogInformation("🐋 WhaleTracker API başlatılıyor...");
app.Logger.LogInformation("📊 Swagger: https://localhost:5001");
app.Logger.LogInformation("🔧 Environment: {Env}", app.Environment.EnvironmentName);

app.Run();

static bool IsApiOrHubRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/api") ||
           request.Path.StartsWithSegments("/hubs");
}

static void EnsureTrackedWalletSchema(WhaleTrackerDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS tracked_wallets (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            wallet_address VARCHAR(100) NOT NULL,
            label VARCHAR(120) NOT NULL DEFAULT '',
            source VARCHAR(60) NOT NULL DEFAULT 'manual',
            chain VARCHAR(40) NOT NULL DEFAULT 'ethereum',
            is_active BOOLEAN NOT NULL DEFAULT TRUE,
            confidence_score NUMERIC NOT NULL DEFAULT 0,
            estimated_profit_usd NUMERIC NOT NULL DEFAULT 0,
            asset_symbol VARCHAR(20) NOT NULL DEFAULT '',
            historical_scan_id BIGINT NULL,
            insider_candidate_id BIGINT NULL,
            notes TEXT NOT NULL DEFAULT '',
            last_checked_at TIMESTAMPTZ NULL,
            last_seen_tx_hash VARCHAR(100) NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE UNIQUE INDEX IF NOT EXISTS ix_tracked_wallets_wallet_address
        ON tracked_wallets (wallet_address);
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_tracked_wallets_is_active
        ON tracked_wallets (is_active);
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_tracked_wallets_confidence_score
        ON tracked_wallets (confidence_score);
        """);
}

static void EnsureTraderFinderSchema(WhaleTrackerDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS trader_scans (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            start_utc TIMESTAMPTZ NOT NULL,
            end_utc TIMESTAMPTZ NOT NULL,
            minimum_starting_value_usd NUMERIC NOT NULL DEFAULT 100000,
            requested_top INTEGER NOT NULL DEFAULT 10,
            evaluated_wallet_count INTEGER NOT NULL DEFAULT 0,
            qualified_wallet_count INTEGER NOT NULL DEFAULT 0,
            state VARCHAR(40) NOT NULL DEFAULT 'QUEUED',
            progress_percent INTEGER NOT NULL DEFAULT 0,
            current_stage VARCHAR(80) NOT NULL DEFAULT 'queued',
            status_message TEXT NOT NULL DEFAULT '',
            error_message TEXT NOT NULL DEFAULT '',
            candidate_wallets_json TEXT NOT NULL DEFAULT '[]',
            progress_log_json TEXT NOT NULL DEFAULT '[]',
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS trader_candidates (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            trader_scan_id BIGINT NOT NULL REFERENCES trader_scans(id) ON DELETE CASCADE,
            wallet_address VARCHAR(100) NOT NULL,
            starting_value_usd NUMERIC NOT NULL DEFAULT 0,
            ending_value_usd NUMERIC NOT NULL DEFAULT 0,
            received_external_usd NUMERIC NOT NULL DEFAULT 0,
            sent_external_usd NUMERIC NOT NULL DEFAULT 0,
            total_fees_usd NUMERIC NOT NULL DEFAULT 0,
            adjusted_profit_usd NUMERIC NOT NULL DEFAULT 0,
            adjusted_return_percent NUMERIC NOT NULL DEFAULT 0,
            realized_gain_usd NUMERIC NOT NULL DEFAULT 0,
            positive_period_percent NUMERIC NOT NULL DEFAULT 0,
            maximum_drawdown_percent NUMERIC NOT NULL DEFAULT 0,
            score NUMERIC NOT NULL DEFAULT 0,
            start_point_utc TIMESTAMPTZ NOT NULL,
            end_point_utc TIMESTAMPTZ NOT NULL,
            chart_period VARCHAR(20) NOT NULL DEFAULT '',
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS ix_trader_scans_created_at
        ON trader_scans (created_at);

        CREATE INDEX IF NOT EXISTS ix_trader_candidates_wallet_address
        ON trader_candidates (wallet_address);

        CREATE INDEX IF NOT EXISTS ix_trader_candidates_score
        ON trader_candidates (score);

        CREATE INDEX IF NOT EXISTS ix_trader_candidates_adjusted_profit
        ON trader_candidates (adjusted_profit_usd);

        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS state VARCHAR(40) NOT NULL DEFAULT 'COMPLETED';
        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS progress_percent INTEGER NOT NULL DEFAULT 100;
        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS current_stage VARCHAR(80) NOT NULL DEFAULT 'completed';
        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS status_message TEXT NOT NULL DEFAULT '';
        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS error_message TEXT NOT NULL DEFAULT '';
        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS candidate_wallets_json TEXT NOT NULL DEFAULT '[]';
        ALTER TABLE trader_scans
            ADD COLUMN IF NOT EXISTS progress_log_json TEXT NOT NULL DEFAULT '[]';
        ALTER TABLE trader_candidates
            ADD COLUMN IF NOT EXISTS positive_period_percent NUMERIC NOT NULL DEFAULT 0;
        ALTER TABLE trader_candidates
            ADD COLUMN IF NOT EXISTS maximum_drawdown_percent NUMERIC NOT NULL DEFAULT 0;
        """);
}

static void EnsureTraderDiscoverySchema(WhaleTrackerDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS trader_discovery_runs (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            provider VARCHAR(30) NOT NULL DEFAULT 'dune',
            execution_id VARCHAR(80) NOT NULL DEFAULT '',
            state VARCHAR(40) NOT NULL DEFAULT '',
            lookback_days INTEGER NOT NULL DEFAULT 28,
            minimum_active_weeks INTEGER NOT NULL DEFAULT 3,
            minimum_meaningful_swaps INTEGER NOT NULL DEFAULT 4,
            minimum_swap_usd NUMERIC NOT NULL DEFAULT 1500,
            candidate_limit INTEGER NOT NULL DEFAULT 200,
            candidate_count INTEGER NOT NULL DEFAULT 0,
            progress_percent INTEGER NOT NULL DEFAULT 0,
            current_stage VARCHAR(80) NOT NULL DEFAULT 'queued',
            status_message TEXT NOT NULL DEFAULT '',
            error_message TEXT NOT NULL DEFAULT '',
            progress_log_json TEXT NOT NULL DEFAULT '[]',
            started_at_utc TIMESTAMPTZ NOT NULL,
            completed_at_utc TIMESTAMPTZ NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS trader_discovery_candidates (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            trader_discovery_run_id BIGINT NOT NULL
                REFERENCES trader_discovery_runs(id) ON DELETE CASCADE,
            wallet_address VARCHAR(100) NOT NULL,
            meaningful_swap_count INTEGER NOT NULL DEFAULT 0,
            active_week_count INTEGER NOT NULL DEFAULT 0,
            approved_notional_usd NUMERIC NOT NULL DEFAULT 0,
            average_swap_usd NUMERIC NOT NULL DEFAULT 0,
            maximum_daily_swaps INTEGER NOT NULL DEFAULT 0,
            distinct_major_assets INTEGER NOT NULL DEFAULT 0,
            copyability_score NUMERIC NOT NULL DEFAULT 0,
            active_chain_count INTEGER NOT NULL DEFAULT 0,
            active_chains_json TEXT NOT NULL DEFAULT '[]',
            first_trade_utc TIMESTAMPTZ NOT NULL,
            last_trade_utc TIMESTAMPTZ NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS ix_trader_discovery_runs_created_at
        ON trader_discovery_runs (created_at);

        CREATE INDEX IF NOT EXISTS ix_trader_discovery_runs_execution_id
        ON trader_discovery_runs (execution_id);

        CREATE INDEX IF NOT EXISTS ix_trader_discovery_candidates_wallet
        ON trader_discovery_candidates (wallet_address);

        CREATE INDEX IF NOT EXISTS ix_trader_discovery_candidates_notional
        ON trader_discovery_candidates (approved_notional_usd);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_trader_discovery_candidates_run_wallet
        ON trader_discovery_candidates (trader_discovery_run_id, wallet_address);

        ALTER TABLE trader_discovery_runs
            ADD COLUMN IF NOT EXISTS progress_percent INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE trader_discovery_runs
            ADD COLUMN IF NOT EXISTS current_stage VARCHAR(80) NOT NULL DEFAULT 'queued';
        ALTER TABLE trader_discovery_runs
            ADD COLUMN IF NOT EXISTS status_message TEXT NOT NULL DEFAULT '';
        ALTER TABLE trader_discovery_runs
            ADD COLUMN IF NOT EXISTS error_message TEXT NOT NULL DEFAULT '';
        ALTER TABLE trader_discovery_runs
            ADD COLUMN IF NOT EXISTS progress_log_json TEXT NOT NULL DEFAULT '[]';
        ALTER TABLE trader_discovery_candidates
            ADD COLUMN IF NOT EXISTS average_swap_usd NUMERIC NOT NULL DEFAULT 0;
        ALTER TABLE trader_discovery_candidates
            ADD COLUMN IF NOT EXISTS maximum_daily_swaps INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE trader_discovery_candidates
            ADD COLUMN IF NOT EXISTS distinct_major_assets INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE trader_discovery_candidates
            ADD COLUMN IF NOT EXISTS copyability_score NUMERIC NOT NULL DEFAULT 0;
        """);
}

static void EnsureAiMemorySchema(WhaleTrackerDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS ai_bias_state (
            id VARCHAR(60) PRIMARY KEY,
            bias_score NUMERIC NOT NULL DEFAULT 0,
            direction VARCHAR(20) NOT NULL DEFAULT 'NEUTRAL',
            symbol_weights_json TEXT NOT NULL DEFAULT '{{}}',
            summary TEXT NOT NULL DEFAULT '',
            event_count INTEGER NOT NULL DEFAULT 0,
            last_event_at TIMESTAMPTZ NULL,
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS ai_decision_events (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            tx_hash VARCHAR(100) NOT NULL DEFAULT '',
            wallet_address VARCHAR(100) NOT NULL DEFAULT '',
            movement_type VARCHAR(40) NOT NULL DEFAULT '',
            symbol VARCHAR(20) NOT NULL DEFAULT '',
            movement_usd NUMERIC NOT NULL DEFAULT 0,
            wallet_balance_usd NUMERIC NOT NULL DEFAULT 0,
            action VARCHAR(30) NOT NULL DEFAULT 'IGNORE',
            should_trade BOOLEAN NOT NULL DEFAULT FALSE,
            confidence INTEGER NOT NULL DEFAULT 0,
            bias_delta NUMERIC NOT NULL DEFAULT 0,
            bias_score_after NUMERIC NOT NULL DEFAULT 0,
            ignored_reason TEXT NOT NULL DEFAULT '',
            reasoning TEXT NOT NULL DEFAULT '',
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_ai_decision_events_tx_hash
        ON ai_decision_events (tx_hash);
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_ai_decision_events_created_at
        ON ai_decision_events (created_at);
        """);
}

static void EnsureRuntimeControlSchema(WhaleTrackerDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS runtime_control (
            id VARCHAR(60) PRIMARY KEY,
            auto_trading_enabled BOOLEAN NOT NULL DEFAULT FALSE,
            polling_interval_seconds INTEGER NOT NULL DEFAULT 30,
            last_worker_heartbeat_at TIMESTAMPTZ NULL,
            last_scan_started_at TIMESTAMPTZ NULL,
            last_scan_completed_at TIMESTAMPTZ NULL,
            last_error TEXT NOT NULL DEFAULT '',
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """);

    db.Database.ExecuteSqlRaw("""
        INSERT INTO runtime_control (id, auto_trading_enabled, polling_interval_seconds)
        VALUES ('global', FALSE, 30)
        ON CONFLICT (id) DO NOTHING;
        """);
}

static void EnsureLiveEventSchema(WhaleTrackerDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS live_events (
            id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            type VARCHAR(80) NOT NULL DEFAULT '',
            severity VARCHAR(20) NOT NULL DEFAULT 'info',
            wallet_address VARCHAR(100) NOT NULL DEFAULT '',
            tx_hash VARCHAR(100) NOT NULL DEFAULT '',
            symbol VARCHAR(20) NOT NULL DEFAULT '',
            usd_value NUMERIC NULL,
            summary TEXT NOT NULL DEFAULT '',
            payload_json TEXT NOT NULL DEFAULT '{{}}',
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_live_events_created_at
        ON live_events (created_at);
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_live_events_wallet_address
        ON live_events (wallet_address);
        """);

    db.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS ix_live_events_type
        ON live_events (type);
        """);
}
