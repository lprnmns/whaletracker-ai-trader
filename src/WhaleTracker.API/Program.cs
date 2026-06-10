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

// ================================================================
// SERVİSLER
// ================================================================
builder.Services.AddScoped<IWhaleTrackerService, WhaleTrackerService>();

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
// VERİTABANI MİGRASYONU (Otomatik tablo oluşturma)
// ================================================================
// TODO: PostgreSQL Docker çalışırken aktif et
// using (var scope = app.Services.CreateScope())
// {
//     var db = scope.ServiceProvider.GetRequiredService<WhaleTrackerDbContext>();
//     
//     // Veritabanı yoksa oluştur ve migration uygula
//     db.Database.EnsureCreated();
// }

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
