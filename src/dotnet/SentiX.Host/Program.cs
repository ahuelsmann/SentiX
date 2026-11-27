using Microsoft.EntityFrameworkCore;
using SentiX.Domain;
using SentiX.Infrastructure;
using SentiX.Ingestion;
using SentiX.NativeBridge;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:8080");
}

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var path = configuration["Sentix:ConfigPath"] ??
        Path.Combine(AppContext.BaseDirectory, "config", "universe.json");
    return UniverseConfigLoader.Load(path);
});

builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    return new NativeBridgeClient(configuration["Sentix:NativeSocketPath"] ?? NativeBridgeClient.DefaultSocketPath,
        timeProvider: services.GetRequiredService<TimeProvider>());
});

builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    return new AlpacaOptions(
        config["Alpaca:ApiKey"] ?? config["ALPACA_API_KEY"],
        config["Alpaca:SecretKey"] ?? config["ALPACA_SECRET_KEY"],
        config.GetValue<bool?>("Alpaca:PaperMode") ?? config.GetValue("ALPACA_PAPER_MODE", true),
        config.GetValue<bool?>("Alpaca:AllowShorting") ?? config.GetValue("ALPACA_ALLOW_SHORTING", false));
});

builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    return new XOptions(
        config["X:BearerToken"] ?? config["X_BEARER_TOKEN"],
        config.GetValue<int?>("X:DailyPostCap") ?? config.GetValue("X_DAILY_POST_CAP", XOptions.DefaultDailyPostCap));
});

builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    return new FinnhubOptions(config["Finnhub:ApiKey"] ?? config["FINNHUB_API_KEY"]);
});

builder.Services.AddDbContextFactory<SentiXDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("Default"),
        postgres => postgres.SetPostgresVersion(17, 0)));

builder.Services.AddSingleton<IMarketEventPipeline>(services => new MarketEventPipeline(
    services.GetRequiredService<IDbContextFactory<SentiXDbContext>>(),
    services.GetRequiredService<NativeBridgeClient>(),
    services.GetRequiredService<ILogger<MarketEventPipeline>>(),
    !string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>().GetConnectionString("Default"))));

builder.Services.AddHostedService<AlpacaNewsCollector>();
builder.Services.AddHttpClient("XFilteredStream", http => http.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService(services => new XIngestionWorker(
    services.GetRequiredService<XOptions>(),
    services.GetRequiredService<UniverseConfig>(),
    services.GetRequiredService<IMarketEventPipeline>(),
    services.GetRequiredService<IHttpClientFactory>().CreateClient("XFilteredStream"),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<XIngestionWorker>>()));

var app = builder.Build();
_ = app.Services.GetRequiredService<UniverseConfig>();

app.MapGet("/health", async (NativeBridgeClient bridge, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var check = await bridge.HealthCheckAsync(context.RequestAborted);
    string nativeStatus = check.Value switch
    {
        { Status: NativeStatus.Starting } => "starting",
        { Status: NativeStatus.Degraded } or { ModelLoaded: false } => "degraded",
        { Status: NativeStatus.Ready } when !check.IsFailClosed => "ready",
        _ => "unavailable"
    };
    return Results.Ok(new
    {
        status = "ok",
        native = nativeStatus,
        phase = 1,
        tradingEnabled = false
    });
});

if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.Run();

public partial class Program;
