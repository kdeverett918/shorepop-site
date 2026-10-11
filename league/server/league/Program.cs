using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Npgsql;
using Shorepop.League;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("League").Get<LeagueOptions>() ?? new LeagueOptions();
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 256 * 1024);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNameCaseInsensitive = false;
    json.SerializerOptions.MaxDepth = 16;
    json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<LeagueRepository>();
builder.Services.AddSingleton(new UgsJwksCache(new HttpClient(new SocketsHttpHandler {
    AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3)
}) { Timeout = TimeSpan.FromSeconds(3) }));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<LeagueOptions, UgsJwksCache>((jwt, options, cache) => UgsAuthentication.Configure(jwt, options, cache));
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    // Global bounded concurrency protects JWKS/auth work; replay requests also have a per-subject
    // sliding request limit after authentication. Proxy/IP identity is never treated as player auth.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter("server", _ => new() { PermitLimit = 32, QueueLimit = 0 }));
    options.AddPolicy("player", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? "anonymous", _ => new() {
            PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});
var app = builder.Build();
var requestSlots = new SemaphoreSlim(32, 32);
if (settings.Configured) app.Services.GetRequiredService<LeagueRepository>().Initialize();
app.Use(async (context, next) =>
{
    if (!requestSlots.Wait(0)) { context.Response.StatusCode = 429; return; }
    try
    {
        if (context.Request.Path.StartsWithSegments("/v1")) settings.RequireConfigured();
        await next(context);
    }
    catch (LeagueRefusal refusal)
    {
        context.Response.StatusCode = refusal.Status;
        await context.Response.WriteAsJsonAsync(new ErrorBody(refusal.Code));
    }
    catch (SqliteException error)
    {
        app.Logger.LogWarning("League storage refused operation with SQLite code {Code}", error.SqliteErrorCode);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new ErrorBody("storage_unavailable"));
    }
    catch (Exception error) when (error is NpgsqlException or TimeoutException)
    {
        app.Logger.LogWarning("League storage unavailable ({Kind})", error.GetType().Name);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new ErrorBody("storage_unavailable"));
    }
    finally { requestSlots.Release(); }
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/health", async (LeagueRepository repository, CancellationToken cancel) =>
{
    bool ready = settings.Configured;
    if (ready)
    {
        try { await repository.CheckReadinessAsync(cancel); }
        catch (Exception error) when (error is NpgsqlException or SqliteException or LeagueRefusal or TimeoutException or OperationCanceledException)
        { ready = false; }
    }
    return Results.Json(new { status = ready ? "ready" : "unavailable", storage = settings.DatabaseProvider,
        projectId = settings.ProjectId, rules = ReplayValidator.RulesVersion,
        repeatPolicy = settings.RepeatPolicy, boosterPolicy = settings.BoosterPolicy,
        source = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local" }, statusCode: ready ? 200 : 503);
});
app.MapPost("/v1/campaign/wins", (CampaignWin win, HttpContext context, LeagueRepository repository) =>
{
    try { return Results.Json(repository.Submit(context.User.FindFirst("sub")!.Value, win, DateTime.UtcNow)); }
    catch (LeagueRefusal refusal) when (refusal.Status is 409 or 422)
    {
        // Only a submission refusal carries its durable attempt identity. Clients retain ambiguous
        // proxy/error responses until a correlated receipt confirms a permanent rejection.
        return Results.Json(new ErrorBody(refusal.Code, win.Attempt?.Id), statusCode: refusal.Status);
    }
}).RequireAuthorization().RequireRateLimiting("player");
RecordEndpoint.Map(app);
app.MapGet("/v1/weekly", (int? neighbours, HttpContext context, LeagueRepository repository) =>
    repository.Weekly(context.User.FindFirst("sub")!.Value, neighbours ?? 7, DateTime.UtcNow)).RequireAuthorization().RequireRateLimiting("player");
app.Run();

public partial class Program;
