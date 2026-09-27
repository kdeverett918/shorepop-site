using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Shorepop.Verifier;

var builder = WebApplication.CreateBuilder(args);
var settings = VerifierOptions.FromConfiguration(builder.Configuration);
string? port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port) && int.TryParse(port, out int portNumber)) builder.WebHost.UseUrls($"http://0.0.0.0:{portNumber}");
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(json => { json.IncludeScopes = false; json.UseUtcTimestamp = true; });
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNameCaseInsensitive = true;
    // Unity JsonUtility matches field names exactly: answer in the client's PascalCase.
    json.SerializerOptions.PropertyNamingPolicy = null;
    json.SerializerOptions.MaxDepth = 8;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new UgsJwksCache(new HttpClient(new SocketsHttpHandler {
    AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3)
}) { Timeout = TimeSpan.FromSeconds(3) }));
var storeHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(10) };
builder.Services.AddSingleton<IPurchaseStore>(_ => settings.StoreKind == "memory"
    ? new InMemoryPurchaseStore() : new SqlitePurchaseStore(settings.DatabasePath));
builder.Services.AddSingleton(AppleJwsVerifier.Production());
builder.Services.AddSingleton<IAppleServerApi>(new AppStoreServerApi(storeHttp, settings.AppleKeyId, settings.AppleIssuerId, settings.ApplePrivateKey, settings.BundleId));
builder.Services.AddSingleton<IGooglePlayApi>(new GooglePlayApi(storeHttp, settings.GoogleServiceAccountJson));
builder.Services.AddSingleton(sp => new PurchaseService(
    sp.GetRequiredService<AppleJwsVerifier>(),
    new AppleTransactionChecker(sp.GetRequiredService<AppleJwsVerifier>(), settings.BundleId, settings.AppleEnvironments),
    sp.GetRequiredService<IAppleServerApi>(), sp.GetRequiredService<IGooglePlayApi>(), sp.GetRequiredService<IPurchaseStore>(),
    settings.BundleId, sp.GetRequiredService<ILogger<PurchaseService>>()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<VerifierOptions, UgsJwksCache>((jwt, options, cache) => UgsAuthentication.Configure(jwt, options, cache));
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter("server", _ => new() { PermitLimit = 32, QueueLimit = 0 }));
    // Per authenticated UGS player (never per IP): 20 verifier calls a minute.
    options.AddPolicy("player", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? "anonymous", _ => new() {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});
// No CORS middleware is registered: browsers get no Access-Control-* headers, so cross-origin calls fail.

var app = builder.Build();
// Resolve the store at startup so a bad disk path fails the deploy instead of the first purchase.
if (settings.Configured) app.Services.GetRequiredService<IPurchaseStore>();
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Path.StartsWithSegments("/v1") && !settings.Configured) throw new VerifierRefusal(503, "verifier_unconfigured");
        await next(context);
    }
    catch (VerifierRefusal refusal)
    {
        app.Logger.LogInformation("refused {Path} {Status} {Code}", context.Request.Path.Value, refusal.Status, refusal.Code);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = refusal.Status;
        await context.Response.WriteAsJsonAsync(new ErrorBody(refusal.Code));
    }
    catch (BadHttpRequestException bad)
    {
        context.Response.StatusCode = bad.StatusCode;
        await context.Response.WriteAsJsonAsync(new ErrorBody("bad_request"));
    }
    catch (JsonException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new ErrorBody("malformed_json"));
    }
    catch (SqliteException error)
    {
        app.Logger.LogWarning("storage refused operation with SQLite code {Code}", error.SqliteErrorCode);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new ErrorBody("storage_unavailable"));
    }
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", (IAppleServerApi appleApi, IGooglePlayApi google) => Results.Json(new
{
    status = settings.Configured ? "ok" : "unconfigured",
    apple = "jws_chain_g3",
    appleServerApi = appleApi.Configured,
    google = google.Configured ? "configured" : "not_configured",
    store = settings.StoreKind == "memory" ? "memory" : "sqlite_ephemeral",
}));
app.MapPost("/v1/purchases/validate", async (NativePurchaseProof proof, HttpContext context, PurchaseService service) =>
    Results.Json(await service.ValidateAsync(context.User.FindFirst("sub")!.Value, proof, context.RequestAborted)))
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/reconcile", async (ReconcileRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(await service.ReconcileAsync(context.User.FindFirst("sub")!.Value, request, context.RequestAborted)))
    .RequireAuthorization().RequireRateLimiting("player");
app.Run();

public partial class Program;
