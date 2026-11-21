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
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = GrowingBoundsMonitor.MaxRequestBodyBytes);
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
builder.Services.AddSingleton<SideJobStatusTracker>();
builder.Services.AddSingleton<GrowingBoundsMonitor>();
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
builder.Services.AddSingleton<IAppleServerApi>(sp => new AppStoreServerApi(
    storeHttp, settings.AppleKeyId, settings.AppleIssuerId, settings.ApplePrivateKey, settings.AppleBundleId,
    sp.GetRequiredService<GrowingBoundsMonitor>()));
builder.Services.AddSingleton<IGooglePlayApi>(sp => new GooglePlayApi(
    storeHttp, settings.GoogleServiceAccountJson, sp.GetRequiredService<GrowingBoundsMonitor>()));
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
    sp.GetRequiredService<ILogger<RevenueProofRetentionService>>(), sp.GetRequiredService<SideJobStatusTracker>()));
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
var boundsMonitor = app.Services.GetRequiredService<GrowingBoundsMonitor>();
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.ContentLength is long contentLength && contentLength > 0)
            boundsMonitor.Observe("request_body_bytes", contentLength, GrowingBoundsMonitor.MaxRequestBodyBytes);
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

// Routing health check (Render healthCheckPath): checks ONLY process configuration and canonical
// settlement ledger reachability. Side jobs (proof retention/export) and growing-bound warnings
// NEVER fail /healthz and are reported separately on /status (WO-20261118-396).
app.MapGet("/healthz", async (HttpContext context, IAppleServerApi appleApi, IGooglePlayApi google) =>
{
    bool available = settings.Configured;
    string storage = "unconfigured";
    if (available)
    {
        try
        {
            var store = app.Services.GetRequiredService<IPurchaseStore>();
            (available, storage) = await VerifierHealth.CheckRoutingReadinessAsync(settings.Configured, store, context.RequestAborted);
        }
        catch (Exception error) when (error is NpgsqlException or SqliteException or TimeoutException or OperationCanceledException)
        {
            available = false; storage = "unavailable";
            app.Logger.LogWarning("readiness storage unavailable");
        }
    }
    return Results.Json(new
    {
        status = !settings.Configured ? "unconfigured" : available ? "ok" : "unavailable",
        storage,
        apple = "jws_chain_g3",
        appleServerApi = appleApi.Configured,
        google = google.Configured ? "configured" : "not_configured",
        store = settings.StoreKind switch { "postgres" => "postgres_durable", "memory" => "memory", _ => "sqlite_ephemeral" },
        build = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local",
        revenueProofRecovery = revenueProofs != null,
        catalogProducts = ShorepopCatalog.ProductIds.Count,
    }, statusCode: available ? 200 : 503);
});

// Separate non-routing status route for unattended probes, side-job alerts and growing-bound warnings (WO-20261118-396).
app.MapGet("/status", (SideJobStatusTracker sideJobs, GrowingBoundsMonitor bounds, IAppleServerApi appleApi, IGooglePlayApi google) =>
{
    StoreStorageMetrics? metrics = null;
    if (settings.Configured)
    {
        try
        {
            metrics = app.Services.GetRequiredService<IPurchaseStore>().GetStorageMetrics();
            bounds.ObserveStorage(metrics);
        }
        catch (Exception error) when (error is NpgsqlException or SqliteException or TimeoutException)
        {
            app.Logger.LogWarning("status storage metrics unavailable");
        }
    }
    var jobSnapshots = sideJobs.Snapshot();
    var warnings = bounds.Warnings;
    bool degraded = jobSnapshots.Values.Any(j => j.Status != "ok" && j.Status != "idle") || warnings.Count > 0;
    return Results.Json(new
    {
        status = !settings.Configured ? "unconfigured" : degraded ? "degraded" : "ok",
        store = settings.StoreKind switch { "postgres" => "postgres_durable", "memory" => "memory", _ => "sqlite_ephemeral" },
        appleServerApi = appleApi.Configured,
        google = google.Configured ? "configured" : "not_configured",
        catalogProducts = ShorepopCatalog.ProductIds.Count,
        storageMetrics = metrics,
        sideJobs = jobSnapshots,
        boundWarnings = warnings,
        build = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT") ?? "local",
    });
});

// Read-only catalog metadata route for release parity checks and unattended probes (WO-20261013-64, WO-20261118-396).
app.MapGet("/v1/catalog", () => Results.Json(new
{
    googlePackageId = settings.GooglePackageId,
    appleBundleId = settings.AppleBundleId,
    count = ShorepopCatalog.ProductIds.Count,
    products = ShorepopCatalog.ProductIds.Order(StringComparer.Ordinal).Select(id => new
    {
        id,
        kind = ShorepopCatalog.CatalogKind(id).ToString(),
    }).ToArray(),
}));

app.MapPost("/v1/purchases/validate", async (NativePurchaseProof proof, HttpContext context, PurchaseService service, IPurchaseStore store, GrowingBoundsMonitor bounds) =>
{
    string player = context.User.FindFirst("sub")!.Value;
    var result = await service.ValidateAsync(player, proof, context.RequestAborted);
    if (revenueProofs != null && RevenueProofBinding.TryBind(proof, result, settings.GoogleLicensePublicKey, out var receipt, out var signature, bounds))
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
    var economics = await service.RefreshOwnedRevenueAsync(player, request.Store, request.ApplicationId, row.TransactionId, context.RequestAborted, request.InstallId);
    if (economics.Refunded || economics.TestOrder || ShorepopCatalog.IsTestEnvironment(economics.Environment))
        throw new VerifierRefusal(404, "revenue_proof_not_found");
    if (ShorepopCatalog.IsSubscription(request.ProductId) && !economics.RevenueRefunded &&
        !service.AuthorizeSubscriptionRevenueProof(player, request.Store, request.ApplicationId, economics.RevenueTransactionId, request.InstallId))
        throw new VerifierRefusal(404, "revenue_proof_install_mismatch");
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
app.MapPost("/v1/purchases/finish", async (GoogleFinishRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(await service.FinishGooglePurchaseAsync(context.User.FindFirst("sub")!.Value, request, context.RequestAborted)))
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/subscription-revenue/pending", (SubscriptionRevenuePendingRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(service.GetPendingSubscriptionRevenueCharges(context.User.FindFirst("sub")!.Value, request)))
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/subscription-revenue/confirm", (SubscriptionRevenueConfirmRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(service.ConfirmSubscriptionRevenue(context.User.FindFirst("sub")!.Value, request)))
    .RequireAuthorization().RequireRateLimiting("player");
app.MapPost("/v1/purchases/reconcile", async (ReconcileRequest request, HttpContext context, PurchaseService service) =>
    Results.Json(await service.ReconcileAsync(context.User.FindFirst("sub")!.Value, request, context.RequestAborted)))
    .RequireAuthorization().RequireRateLimiting("player");

app.MapPost("/v1/privacy/revenue-proofs/erase", (PrivacyScopeRequest request, HttpContext context) =>
{
    if (revenueProofs == null) throw new VerifierRefusal(503, "privacy_storage_unavailable");
    var result = revenueProofs.EraseForPlayer(context.User.FindFirst("sub")!.Value,
        request.Store ?? "", request.ApplicationId ?? "", DateTimeOffset.UtcNow);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { result.ClearedProofCount, result.AlreadySuppressed, result.SuppressedAtUnixMs,
        SettlementRecordsRetained = true });
}).RequireAuthorization().RequireRateLimiting("player");

app.MapPost("/v1/privacy/account-deletion", (PrivacyScopeRequest request, HttpContext context) =>
{
    if (revenueProofs == null) throw new VerifierRefusal(503, "privacy_storage_unavailable");
    string playerId = context.User.FindFirst("sub")!.Value;
    var result = revenueProofs.RequestAccountDeletion(playerId,
        request.Store ?? "", request.ApplicationId ?? "", request.AnalyticsInstallId, DateTimeOffset.UtcNow,
        request.FirebaseAppInstanceId);
    context.Response.Headers.CacheControl = "no-store";
    // Acceptance is durable initiation, not a claim that every provider has completed erasure.
    return Results.Json(new { result.RequestId, result.Status, result.RequestedAtUnixMs,
        SettlementRecordsRetained = true, PlayerId = playerId, request.Store, request.ApplicationId });
}).RequireAuthorization().RequireRateLimiting("player");
app.Run();

public partial class Program;
