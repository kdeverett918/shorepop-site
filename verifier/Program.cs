using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Npgsql;
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
builder.Services.AddSingleton<IPurchaseStore>(_ => settings.StoreKind switch
{
    "memory" => new InMemoryPurchaseStore(),
    "sqlite" => new SqlitePurchaseStore(settings.DatabasePath),
    "postgres" => new PostgresPurchaseStore(settings.DatabaseUrl
        ?? throw new InvalidOperationException("VERIFIER_DATABASE_URL is required for the postgres store."), settings.SubscriptionRevisionEpoch),
    _ => throw new InvalidOperationException("VERIFIER_STORE must be memory, sqlite or postgres."),
});
builder.Services.AddSingleton(AppleJwsVerifier.Production());
builder.Services.AddSingleton<IAppleServerApi>(new AppStoreServerApi(storeHttp, settings.AppleKeyId, settings.AppleIssuerId, settings.ApplePrivateKey, settings.AppleBundleId));
builder.Services.AddSingleton<IGooglePlayApi>(new GooglePlayApi(storeHttp, settings.GoogleServiceAccountJson));
builder.Services.AddSingleton(sp => new PurchaseService(
    sp.GetRequiredService<AppleJwsVerifier>(),
    new AppleTransactionChecker(sp.GetRequiredService<AppleJwsVerifier>(), settings.AppleBundleId, settings.AppleEnvironments),
    sp.GetRequiredService<IAppleServerApi>(), sp.GetRequiredService<IGooglePlayApi>(), sp.GetRequiredService<IPurchaseStore>(),
    settings.GooglePackageId, sp.GetRequiredService<ILogger<PurchaseService>>(), appleBundleId: settings.AppleBundleId));
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

RevenueProofStore? revenueProofs = null;
builder.Services.AddHostedService(sp => new RevenueProofRetentionService(() => revenueProofs,
    sp.GetRequiredService<ILogger<RevenueProofRetentionService>>()));
var app = builder.Build();
// Resolve the store at startup so a bad disk path fails the deploy instead of the first purchase.
if (settings.Configured) app.Services.GetRequiredService<IPurchaseStore>();
if (settings.Configured && settings.StoreKind == "postgres" && !string.IsNullOrWhiteSpace(settings.RevenueProofKey))
{
    byte[] proofKey;
    try { proofKey = Convert.FromBase64String(settings.RevenueProofKey); }
    catch (FormatException) { throw new InvalidOperationException("VERIFIER_REVENUE_PROOF_KEY must encode 32 bytes."); }
    if (proofKey.Length != 32) throw new InvalidOperationException("VERIFIER_REVENUE_PROOF_KEY must encode 32 bytes.");
    revenueProofs = new RevenueProofStore(settings.DatabaseUrl!, proofKey);
    CryptographicOperations.ZeroMemory(proofKey);
}
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
    catch (NpgsqlException error)
    {
        // SQL and connection parameters can contain private transaction data: log only the SQLSTATE.
        app.Logger.LogWarning("storage refused operation with PostgreSQL state {State}", error.SqlState ?? "unavailable");
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
    store = settings.StoreKind switch { "postgres" => "postgres_durable", "memory" => "memory", _ => "sqlite_ephemeral" },
    build = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local",
    revenueProofRecovery = revenueProofs != null,
}));
app.MapPost("/v1/purchases/validate", async (NativePurchaseProof proof, HttpContext context, PurchaseService service, IPurchaseStore store) =>
{
    string player = context.User.FindFirst("sub")!.Value;
    var result = await service.ValidateAsync(player, proof, context.RequestAborted);
    if (revenueProofs != null && RevenueProofBinding.TryBind(proof, result, settings.GoogleLicensePublicKey, out var receipt, out var signature))
    {
        try
        {
            var row = store.List(player, result.Store, result.ApplicationId).First(r => r.TransactionId == result.TransactionId);
            revenueProofs.Save(row, player, receipt, signature, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is NpgsqlException or CryptographicException or VerifierRefusal or TimeoutException)
        {
            // A measurement proof failure must not reverse a valid grant or store acknowledgment.
            app.Logger.LogWarning("revenue proof was not retained; purchase verification completed");
        }
    }
    return Results.Json(result);
})
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/revenue-proof", async (RevenueProofRequest request, HttpContext context, PurchaseService service, IPurchaseStore store) =>
{
    if (revenueProofs == null) throw new VerifierRefusal(503, "revenue_proof_recovery_unconfigured");
    if (!ShorepopCatalog.MatchesApplication(request.Store, request.ApplicationId) ||
        !ShorepopCatalog.ProductIds.Contains(request.ProductId) || request.LedgerTransactionKey?.Length != 67)
        throw new VerifierRefusal(422, "revenue_proof_request_invalid");
    string player = context.User.FindFirst("sub")!.Value;
    var row = store.List(player, request.Store, request.ApplicationId).FirstOrDefault(r =>
        r.ProductId == request.ProductId && RevenueProofStore.LedgerKey(r.Store, r.ApplicationId, r.TransactionId) == request.LedgerTransactionKey);
    if (row == null) throw new VerifierRefusal(404, "revenue_proof_not_found");
    var economics = await service.RefreshOwnedRevenueAsync(player, request.Store, request.ApplicationId, row.TransactionId, context.RequestAborted);
    if (economics.Refunded) throw new VerifierRefusal(404, "revenue_proof_not_found");
    var recovered = economics.RevenueRefunded ? null : revenueProofs.Get(player, request.Store, request.ApplicationId, request.LedgerTransactionKey, request.ProductId, DateTimeOffset.UtcNow);
    if (recovered == null && !economics.RevenueRefunded) throw new VerifierRefusal(404, "revenue_proof_not_found");
    // A Google subscription reuses its token. A concurrent renewal must not pair an old order's
    // economics with a newly cached receipt (or the reverse).
    if (recovered != null && !RevenueProofBinding.MatchesRecovered(recovered, economics, settings.GoogleLicensePublicKey))
        throw new VerifierRefusal(404, "revenue_proof_order_mismatch");
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new RevenueProofResponse
    {
        Store = request.Store, ProductId = request.ProductId, LedgerTransactionKey = request.LedgerTransactionKey,
        Receipt = recovered?.Receipt ?? "", Signature = recovered?.Signature ?? "",
        RevenueVerified = economics.RevenueVerified, RevenueRefunded = economics.RevenueRefunded, GrossRevenueMicros = economics.GrossRevenueMicros,
        RevenueCurrency = economics.RevenueCurrency, RevenueTransactionId = economics.RevenueTransactionId,
        RevenuePurchasedAtUnixMs = economics.RevenuePurchasedAtUnixMs,
    });
})
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/ack", (AckRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(service.Ack(context.User.FindFirst("sub")!.Value, request)))
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/reconcile", async (ReconcileRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(await service.ReconcileAsync(context.User.FindFirst("sub")!.Value, request, context.RequestAborted)))
    .RequireAuthorization().RequireRateLimiting("player");
app.Run();

public partial class Program;
