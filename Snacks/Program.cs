using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Services;
using Snacks.Hubs;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Configure form options for large file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2_147_483_648; // 2GB
    options.ValueLengthLimit = int.MaxValue;
    options.ValueCountLimit = int.MaxValue;
    options.KeyLengthLimit = int.MaxValue;
    options.MemoryBufferThreshold = int.MaxValue;
});

// Configure Kestrel server limits
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 2_147_483_648; // 2GB
});

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.WriteIndented = true;
    });

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

// Database — SQLite with WAL mode for crash resilience
var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
var configDir = Path.Combine(workDir, "config");
Directory.CreateDirectory(configDir);
var dbPath = Path.Combine(configDir, "snacks.db");

builder.Services.AddDbContextFactory<SnacksDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<MediaFileRepository>();

builder.Services.AddSingleton<FfprobeService>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<TranscodingService>();
builder.Services.AddSingleton<AutoScanService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoScanService>());

var app = builder.Build();

// Initialize database — apply migrations and set pragmas
var mediaFileRepo = app.Services.GetRequiredService<MediaFileRepository>();
await mediaFileRepo.InitializeAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // Remove HTTPS redirection for Docker container
    // app.UseHsts();
}

// Don't redirect to HTTPS in production when running in container
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<TranscodingHub>("/transcodingHub");

app.Run();
