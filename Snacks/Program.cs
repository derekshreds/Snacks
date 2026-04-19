using Microsoft.EntityFrameworkCore;
using Snacks.Data;
using Snacks.Services;
using Snacks.Hubs;
using Snacks.Models;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Determine listening address.
// Docker: always 0.0.0.0:6767 (container isolation provides security).
// Electron: localhost by default, 0.0.0.0 when cluster mode is enabled (checked via cluster.json).
var listenUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
bool bindAllInterfaces = false;

if (listenUrl == null)
{
    // Not set by Electron — Docker or standalone. Default to all interfaces.
    listenUrl = "http://0.0.0.0:6767";
    bindAllInterfaces = true;
}
else
{
    // If the URL already specifies a wildcard host, bind all interfaces.
    var firstUrl = listenUrl.Split(';')[0];
    if (firstUrl.Contains("://+") || firstUrl.Contains("://*") || firstUrl.Contains("://0.0.0.0"))
    {
        bindAllInterfaces = true;
    }
    else
    {
        // Electron sets ASPNETCORE_URLS to localhost. Check if cluster is enabled to decide binding.
        var clusterConfigPath = Path.Combine(
            Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Snacks", "work"),
            "config", "cluster.json");

        if (File.Exists(clusterConfigPath))
        {
            try
            {
                var clusterJson = File.ReadAllText(clusterConfigPath);
                using var clusterDoc = System.Text.Json.JsonDocument.Parse(clusterJson);
                if (clusterDoc.RootElement.TryGetProperty("enabled", out var enabled)
                    && enabled.GetBoolean()
                    && clusterDoc.RootElement.TryGetProperty("role", out var role)
                    && role.GetString() != "standalone")
                {
                    bindAllInterfaces = true;
                }
            }
            catch { }
        }
    }
}

// Parse the port from the resolved listen URL.
var normalizedUrl = listenUrl.Split(';')[0].Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0");
var listenPort = new Uri(normalizedUrl).Port;

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Explicit Kestrel config overrides ASPNETCORE_URLS — no duplicate listeners.
    if (bindAllInterfaces)
        serverOptions.ListenAnyIP(listenPort);
    else
        serverOptions.ListenLocalhost(listenPort);

    // 100MB — chunks are 50MB, leaves headroom.
    serverOptions.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

// Configure form options for large file uploads.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024; // 100MB
    options.ValueLengthLimit = 256 * 1024;                // 256KB per value
    options.ValueCountLimit = 1024;                       // Max 1024 form fields
    options.KeyLengthLimit = 2048;                        // 2KB per key
    options.MemoryBufferThreshold = 64 * 1024;            // 64KB before buffering to disk
});

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

// Database — SQLite with WAL mode for crash resilience.
var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Snacks", "work");
var configDir = Path.Combine(workDir, "config");
Directory.CreateDirectory(configDir);
var dbPath = Path.Combine(configDir, "snacks.db");

builder.Services.AddDbContextFactory<SnacksDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<MediaFileRepository>();

builder.Services.AddSingleton<FfprobeService>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<ConfigFileService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<IntegrationService>();
builder.Services.AddSingleton<Snacks.Services.Ocr.TessdataResolver>();
builder.Services.AddSingleton<Snacks.Services.Ocr.NativeOcrService>();
builder.Services.AddSingleton<SubtitleExtractionService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<TranscodingService>();
builder.Services.AddSingleton<StateTransitionService>();
builder.Services.AddSingleton<ClusterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterService>());
builder.Services.AddScoped<ClusterAuthFilter>();
builder.Services.AddScoped<LocalNetworkOnlyFilter>();
builder.Services.AddSingleton<AutoScanService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoScanService>());

var app = builder.Build();

// Initialize database — apply migrations and set pragmas.
var mediaFileRepo = app.Services.GetRequiredService<MediaFileRepository>();
await mediaFileRepo.InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// HTTPS redirection disabled in containers for flexibility.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

app.UseRouting();

app.UseMiddleware<AuthMiddleware>();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers(); // Attribute-routed controllers (ClusterController)
app.MapHub<TranscodingHub>("/transcodingHub");

app.Run();
