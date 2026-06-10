using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using WhaleTracker.API.Configuration;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Repositories;
using WhaleTracker.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

EnvFileLoader.LoadNearest(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();

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

// ================================================================
// SERVİSLER
// ================================================================
builder.Services.AddScoped<IWhaleTrackerService, WhaleTrackerService>();
builder.Services.AddScoped<IInsiderDetectionService, InsiderDetectionService>();
builder.Services.AddScoped<IAiBiasMemoryService, AiBiasMemoryService>();
builder.Services.AddHostedService<AutoTraderWorker>();

// ================================================================
// AUTH (Cookie)
// ================================================================
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login.html";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
    });

builder.Services.AddAuthorization();

// Background Service - şimdilik devre dışı (test aşamasında)
// TODO: Testler tamamlandıktan sonra aktif et
// builder.Services.AddHostedService<WhaleTrackerService>();

// ================================================================
// API CONTROLLER'LAR
// ================================================================
builder.Services.AddControllers();

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

// ================================================================
// BAŞLANGIÇ MESAJI
// ================================================================
app.Logger.LogInformation("🐋 WhaleTracker API başlatılıyor...");
app.Logger.LogInformation("📊 Swagger: https://localhost:5001");
app.Logger.LogInformation("🔧 Environment: {Env}", app.Environment.EnvironmentName);

app.Run();

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
