using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public enum GooglePurchaseType { Charged, Test, Promo, Rewarded, Unverified }

public interface IGooglePlayApi
{
    bool Configured { get; }
    /// <summary>purchases.productsv2.getproductpurchasev2 body, or null when Google reports the token unknown/invalid.</summary>
    Task<JsonElement?> GetProductPurchaseAsync(string packageName, string token, CancellationToken cancel);
    /// <summary>purchases.subscriptionsv2.get body, or null when Google reports the token unknown/invalid.</summary>
    Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel);
    /// <summary>orders.get body for the order id proved by a purchases API response; null when unavailable.</summary>
    Task<JsonElement?> GetOrderAsync(string packageName, string orderId, CancellationToken cancel) => Task.FromResult<JsonElement?>(null);
    /// <summary>v1 purchases.products.get body for purchaseType / consumptionState inspection (WO-20261020-14, WO-20261120-650).</summary>
    Task<JsonElement?> GetLegacyProductPurchaseAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        Task.FromResult<JsonElement?>(null);
    /// <summary>POST purchases.products.acknowledge (WO-20261120-650).</summary>
    Task AcknowledgeProductAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        throw new VerifierRefusal(503, "google_finish_unavailable");
    /// <summary>POST purchases.products.consume (WO-20261120-650).</summary>
    Task ConsumeProductAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        throw new VerifierRefusal(503, "google_finish_unavailable");
    /// <summary>POST purchases.subscriptions.acknowledge (WO-20261120-650).</summary>
    Task AcknowledgeSubscriptionAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        throw new VerifierRefusal(503, "google_finish_unavailable");
}

/// <summary>
/// Google Play Developer API client using a service-account JSON (env GOOGLE_PLAY_SA_JSON, the key
/// file's text). Unset = not configured: every Google request fails closed with 503.
/// </summary>
public sealed class GooglePlayApi : IGooglePlayApi
{
    private const string Scope = "https://www.googleapis.com/auth/androidpublisher";
    private const string Base = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";
    private readonly HttpClient http;
    private readonly string? clientEmail, privateKeyPem, tokenUri;
    private readonly GrowingBoundsMonitor? bounds;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessExpires;

    public GooglePlayApi(HttpClient http, string? serviceAccountJson, GrowingBoundsMonitor? bounds = null)
    {
        this.http = http;
        this.bounds = bounds;
        if (string.IsNullOrWhiteSpace(serviceAccountJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(serviceAccountJson);
            var root = doc.RootElement;
            clientEmail = root.GetProperty("client_email").GetString();
            privateKeyPem = root.GetProperty("private_key").GetString();
            tokenUri = root.TryGetProperty("token_uri", out var uri) ? uri.GetString() : "https://oauth2.googleapis.com/token";
            if (tokenUri != "https://oauth2.googleapis.com/token") tokenUri = "https://oauth2.googleapis.com/token";
        }
        catch (Exception) { clientEmail = privateKeyPem = null; }
    }

    public bool Configured => !string.IsNullOrWhiteSpace(clientEmail) && !string.IsNullOrWhiteSpace(privateKeyPem);

    public Task<JsonElement?> GetProductPurchaseAsync(string packageName, string token, CancellationToken cancel) =>
        GetAsync($"{Uri.EscapeDataString(packageName)}/purchases/productsv2/tokens/{Uri.EscapeDataString(token)}", cancel);

    public Task<JsonElement?> GetLegacyProductPurchaseAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        GetAsync($"{Uri.EscapeDataString(packageName)}/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(token)}", cancel);

    public Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel) =>
        GetAsync($"{Uri.EscapeDataString(packageName)}/purchases/subscriptionsv2/tokens/{Uri.EscapeDataString(token)}", cancel);

    public Task<JsonElement?> GetOrderAsync(string packageName, string orderId, CancellationToken cancel) =>
        GetAsync($"{Uri.EscapeDataString(packageName)}/orders/{Uri.EscapeDataString(orderId)}", cancel);

    public Task AcknowledgeProductAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        PostAsync($"{Uri.EscapeDataString(packageName)}/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(token)}:acknowledge", "{}", cancel);

    public Task ConsumeProductAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        PostAsync($"{Uri.EscapeDataString(packageName)}/purchases/products/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(token)}:consume", null, cancel);

    public Task AcknowledgeSubscriptionAsync(string packageName, string productId, string token, CancellationToken cancel) =>
        PostAsync($"{Uri.EscapeDataString(packageName)}/purchases/subscriptions/{Uri.EscapeDataString(productId)}/tokens/{Uri.EscapeDataString(token)}:acknowledge", "{}", cancel);

    private async Task<JsonElement?> GetAsync(string path, CancellationToken cancel)
    {
        if (!Configured) throw new VerifierRefusal(503, "google_validation_not_configured");
        using var request = new HttpRequestMessage(HttpMethod.Get, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancel));
        using var response = await http.SendAsync(request, cancel);
        // Google answers an invalid/foreign token with 400 or 404 and a 410 for a purged one.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Gone) return null;
        if (!response.IsSuccessStatusCode) throw new VerifierRefusal(503, "google_api_unavailable");
        var body = await response.Content.ReadAsByteArrayAsync(cancel);
        bounds?.Observe("google_api_response_bytes", body.Length, GrowingBoundsMonitor.MaxStoreApiResponseBytes);
        if (body.Length > GrowingBoundsMonitor.MaxStoreApiResponseBytes) throw new VerifierRefusal(503, "google_api_unavailable");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task PostAsync(string path, string? jsonBody, CancellationToken cancel)
    {
        if (!Configured) throw new VerifierRefusal(503, "google_validation_not_configured");
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancel));
        if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode) throw new VerifierRefusal(503, "google_finish_unavailable");
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            if (accessToken != null && DateTimeOffset.UtcNow < accessExpires) return accessToken;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" }));
            string claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["iss"] = clientEmail!, ["scope"] = Scope, ["aud"] = tokenUri!, ["iat"] = now, ["exp"] = now + 3600,
            }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem!.Replace("\\n", "\n"));
            string assertion = header + "." + claims + "." +
                B64(rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion,
            });
            using var response = await http.PostAsync(tokenUri, form, cancel);
            if (!response.IsSuccessStatusCode) throw new VerifierRefusal(503, "google_auth_unavailable");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            accessToken = doc.RootElement.GetProperty("access_token").GetString();
            int lifetime = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int s) ? s : 3600;
            accessExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, lifetime - 120));
            return accessToken ?? throw new VerifierRefusal(503, "google_auth_unavailable");
        }
        finally { gate.Release(); }
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Interprets Play Developer API responses into a <see cref="StoreVerdict"/>.</summary>
public static class GooglePlayInterpreter
{
    /// <summary>
    /// Allowed clock drift between Play's order record and subscription start record (WO-20261118-898).
    /// Play keeps independent clocks on orders.get and subscriptionsv2.get, so an order period can start
    /// up to ~10 minutes before the subscription record's startTime.
    /// </summary>
    public static readonly TimeSpan OrderPeriodClockSlack = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Classifies Play v1 purchaseType strictly by key presence (WO-20261020-14):
    /// absent = Charged; 0 = Test (license tester); 1 = Promo; 2 = Rewarded;
    /// explicit null, bool, string or undocumented value = Unverified (never collapsed into Charged).
    /// </summary>
    public static GooglePurchaseType ClassifyPurchaseType(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return GooglePurchaseType.Unverified;
        if (!body.TryGetProperty("purchaseType", out var pt)) return GooglePurchaseType.Charged;
        if (pt.ValueKind != JsonValueKind.Number || !pt.TryGetInt32(out int value)) return GooglePurchaseType.Unverified;
        return value switch
        {
            0 => GooglePurchaseType.Test,
            1 => GooglePurchaseType.Promo,
            2 => GooglePurchaseType.Rewarded,
            _ => GooglePurchaseType.Unverified,
        };
    }

    /// <summary>True when an order's period/processed time falls within <see cref="OrderPeriodClockSlack"/> of subscription start.</summary>
    public static bool IsOrderPeriodWithinSlack(long orderTimeMs, long subscriptionStartMs)
    {
        if (subscriptionStartMs <= 0 || orderTimeMs <= 0) return true;
        long slackMs = (long)OrderPeriodClockSlack.TotalMilliseconds;
        return orderTimeMs >= subscriptionStartMs - slackMs;
    }

    /// <summary>Use only the successful order id returned by the verified purchase, never a client-supplied id.</summary>
    public static string RevenueOrderId(JsonElement purchase, string productId, bool subscription)
    {
        if (!subscription) return Text(purchase, "orderId");
        if (purchase.ValueKind != JsonValueKind.Object || !purchase.TryGetProperty("lineItems", out var items) ||
            items.ValueKind != JsonValueKind.Array) return "";
        string id = "";
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "productId") != productId) continue;
            string next = Text(item, "latestSuccessfulOrderId");
            if (next.Length == 0 || (id.Length > 0 && id != next)) return "";
            id = next;
        }
        return id;
    }

    // Subscription startTime belongs to the original purchase token, not the current renewal.
    public static long RevenuePurchaseTime(JsonElement purchase, bool subscription) =>
        subscription ? 0 : Timestamp(Text(purchase, "purchaseCompletionTime"));

    /// <summary>
    /// Attach gross paid economics from orders.get, after checking its identity, single product,
    /// non-test classification, and subscription order-period clock slack (WO-20261118-898, WO-20261020-14).
    /// Unknown economics do not alter the already verified purchase or entitlement.
    /// </summary>
    public static StoreVerdict WithOrderRevenue(StoreVerdict verdict, JsonElement order, string expectedOrderId,
        string purchaseToken, JsonElement verifiedPurchase)
    {
        // License-test orders never produce verified revenue or positive economics (WO-20261020-14).
        if (verdict.TestOrder || ShorepopCatalog.IsTestEnvironment(verdict.Environment))
            return verdict with
            {
                RevenueVerified = false, GrossRevenueMicros = 0, RevenueCurrency = "",
                RevenueTransactionId = "", RevenuePurchasedAtUnixMs = 0, RevenueRefunded = false,
            };
        if (expectedOrderId.Length == 0 || expectedOrderId.Length > 256 || expectedOrderId == purchaseToken ||
            Text(order, "orderId") != expectedOrderId) return verdict;
        string returnedToken = Text(order, "purchaseToken");
        if (order.TryGetProperty("purchaseToken", out var tokenValue) && tokenValue.ValueKind != JsonValueKind.String) return verdict;
        if (returnedToken.Length > 0 && returnedToken != purchaseToken) return verdict;
        if (!order.TryGetProperty("lineItems", out var items) || items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() != 1 || Text(items[0], "productId") != verdict.ProductId) return verdict;
        var item = items[0];
        if (item.TryGetProperty("oneTimePurchaseDetails", out var details) &&
            (details.ValueKind != JsonValueKind.Object || (details.TryGetProperty("quantity", out var quantity) &&
             (quantity.ValueKind != JsonValueKind.Number || !quantity.TryGetInt32(out int q) || q != 1)))) return verdict;
        // Order creation can precede settlement by days for a pending payment. Use the processed
        // event or the successful product purchase timestamp, never the time of this validation.
        long purchasedAt = 0;
        if (order.TryGetProperty("orderHistory", out var history) && history.ValueKind == JsonValueKind.Object &&
            history.TryGetProperty("processedEvent", out var processed)) purchasedAt = Timestamp(Text(processed, "eventTime"));
        if (purchasedAt <= 0) purchasedAt = Timestamp(Text(verifiedPurchase, "purchaseCompletionTime"));
        if (verdict.Kind == ProductKind.Subscription)
        {
            long subStartMs = Timestamp(Text(verifiedPurchase, "startTime"));
            long periodStartMs = Timestamp(Text(order, "periodStartTime"));
            long checkTimeMs = periodStartMs > 0 ? periodStartMs : purchasedAt;
            if (subStartMs > 0 && checkTimeMs > 0 && !IsOrderPeriodWithinSlack(checkTimeMs, subStartMs))
                return verdict;
        }
        string state = Text(order, "state");
        // Play supports refunds without revocation. Keep the purchase API's entitlement verdict;
        // suppress positive analytics only for this economic order, not all future token renewals.
        if (state == "REFUNDED") return verdict with
        {
            RevenueRefunded = true, RevenueTransactionId = expectedOrderId, RevenuePurchasedAtUnixMs = purchasedAt,
        };
        if (state != "PROCESSED") return verdict;
        if (!order.TryGetProperty("total", out var total) || !TryMoney(total, out long grossMicros, out string currency)) return verdict;
        if (purchasedAt <= 0) return verdict;
        return verdict with
        {
            RevenueVerified = true, GrossRevenueMicros = grossMicros, RevenueCurrency = currency,
            RevenueTransactionId = expectedOrderId, RevenuePurchasedAtUnixMs = purchasedAt,
        };
    }

    private static bool TryMoney(JsonElement money, out long micros, out string currency)
    {
        micros = 0;
        currency = Text(money, "currencyCode");
        if (!RevenueValues.IsCurrency(currency)) return false;
        long units = 0;
        int nanos = 0;
        // Protobuf JSON omits zero-valued units/nanos. The total and currency are still required.
        if (money.TryGetProperty("units", out var u) && (u.ValueKind != JsonValueKind.String ||
            !long.TryParse(u.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out units))) return false;
        if (money.TryGetProperty("nanos", out var n) && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out nanos))) return false;
        if (units < 0 || nanos < 0 || nanos >= 1_000_000_000 || nanos % 1000 != 0) return false;
        try { micros = checked(units * 1_000_000 + nanos / 1000); }
        catch (OverflowException) { return false; }
        return true;
    }

    private static long Timestamp(string raw) => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var at) && at.ToUnixTimeMilliseconds() > 0 ? at.ToUnixTimeMilliseconds() : 0;

    public static StoreVerdict Product(JsonElement body, string packageName, string productId, string token)
    {
        if (!body.TryGetProperty("productLineItem", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1)
            throw new VerifierRefusal(422, "line_items_invalid");
        var item = items[0];
        if (Text(item, "productId") != productId) throw new VerifierRefusal(422, "product_mismatch");
        int quantity = 1;
        if (item.TryGetProperty("productOfferDetails", out var offer) && offer.TryGetProperty("quantity", out var q))
            quantity = q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out int n) ? n : -1;
        if (quantity != 1) throw new VerifierRefusal(422, "quantity_not_one");
        string state = body.TryGetProperty("purchaseStateContext", out var ctx) ? Text(ctx, "purchaseState") : "";
        bool refunded = state switch
        {
            "PURCHASED" => false,
            "CANCELLED" => true,
            "PENDING" => throw new VerifierRefusal(409, "purchase_pending"),
            _ => throw new VerifierRefusal(422, "purchase_state_unknown"),
        };
        bool hasTestContext = body.TryGetProperty("testPurchaseContext", out var tc) && tc.ValueKind != JsonValueKind.Null;
        var purchaseType = ClassifyPurchaseType(body);
        bool isTest = hasTestContext || purchaseType == GooglePurchaseType.Test;
        string environment = isTest ? "LicenseTest" : "Production";
        return new StoreVerdict(ShorepopCatalog.GoogleStore, packageName, productId, token, environment, refunded, 0, null, null, 0,
            ShorepopCatalog.CatalogKind(productId), TestOrder: isTest);
    }

    /// <summary>Interprets Play v1 purchases.products.get responses with strict purchaseType classification (WO-20261020-14).</summary>
    public static StoreVerdict LegacyProduct(JsonElement body, string packageName, string productId, string token)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new VerifierRefusal(422, "purchase_state_unknown");
        string returnedProduct = Text(body, "productId");
        if (returnedProduct.Length > 0 && returnedProduct != productId) throw new VerifierRefusal(422, "product_mismatch");
        if (body.TryGetProperty("quantity", out var q) && (q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out int count) || count != 1))
            throw new VerifierRefusal(422, "quantity_not_one");
        if (!body.TryGetProperty("purchaseState", out var ps) || ps.ValueKind != JsonValueKind.Number || !ps.TryGetInt32(out int state))
            throw new VerifierRefusal(422, "purchase_state_unknown");
        bool refunded = state switch
        {
            0 => false,
            1 => true,
            2 => throw new VerifierRefusal(409, "purchase_pending"),
            _ => throw new VerifierRefusal(422, "purchase_state_unknown"),
        };
        var purchaseType = ClassifyPurchaseType(body);
        bool isTest = purchaseType == GooglePurchaseType.Test;
        string environment = isTest ? "LicenseTest" : "Production";
        return new StoreVerdict(ShorepopCatalog.GoogleStore, packageName, productId, token, environment, refunded, 0, null, null, 0,
            ShorepopCatalog.CatalogKind(productId), TestOrder: isTest);
    }

    public static StoreVerdict Subscription(JsonElement body, string packageName, string productId, string token)
    {
        string state = Text(body, "subscriptionState");
        if (state is "SUBSCRIPTION_STATE_PENDING") throw new VerifierRefusal(409, "purchase_pending");
        if (state is "" or "SUBSCRIPTION_STATE_UNSPECIFIED" or "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED")
            throw new VerifierRefusal(422, "purchase_state_unknown");
        if (!body.TryGetProperty("lineItems", out var items) || items.ValueKind != JsonValueKind.Array) throw new VerifierRefusal(422, "line_items_invalid");
        // Protobuf JSON omits false booleans; read missing autoRenewing/autoRenewEnabled as false (WO-20261118-898).
        bool autoRenewing = OptionalBool(body, "autoRenewing");
        long expiry = 0;
        bool found = false;
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "productId") != productId) continue;
            found = true;
            if (OptionalBool(item, "autoRenewing")) autoRenewing = true;
            if (item.TryGetProperty("autoRenewingPlan", out var plan) && plan.ValueKind == JsonValueKind.Object &&
                OptionalBool(plan, "autoRenewEnabled"))
                autoRenewing = true;
            if (DateTimeOffset.TryParse(Text(item, "expiryTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var at))
                expiry = Math.Max(expiry, at.UtcTicks);
        }
        if (!found) throw new VerifierRefusal(422, "product_mismatch");
        if (expiry <= 0) throw new VerifierRefusal(422, "expiry_missing");
        bool isTest = body.TryGetProperty("testPurchase", out var tp) && tp.ValueKind != JsonValueKind.Null;
        string environment = isTest ? "LicenseTest" : "Production";
        string linked = Text(body, "linkedPurchaseToken");
        // Revocation/refund of a subscription shows up as EXPIRED with expiryTime moved to the revocation.
        return new StoreVerdict(ShorepopCatalog.GoogleStore, packageName, productId, token, environment, false, expiry,
            linked.Length == 0 ? null : linked, null, 0, ProductKind.Subscription, TestOrder: isTest, AutoRenewing: autoRenewing);
    }

    /// <summary>
    /// Re-validates a Google Play purchase re-read after acknowledge()/consume() threw (WO-20261120-650).
    /// Returns "consumed" or "acknowledged" ONLY when package, product, token, purchaseState, quantity,
    /// account binding and the required finished state all verify; fails closed otherwise.
    /// </summary>
    public static string ValidateFinishReread(
        JsonElement body, string expectedPackageName, string expectedProductId, string expectedToken,
        ProductKind kind, string? expectedAccountId, string? authenticatedPlayerId = null)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new VerifierRefusal(403, "google_finish_reread_invalid");

        if (body.TryGetProperty("packageName", out var pkg))
        {
            if (pkg.ValueKind != JsonValueKind.String || pkg.GetString() != expectedPackageName)
                throw new VerifierRefusal(403, "google_finish_package_mismatch");
        }
        if (body.TryGetProperty("purchaseToken", out var tok))
        {
            if (tok.ValueKind != JsonValueKind.String || tok.GetString() != expectedToken)
                throw new VerifierRefusal(403, "google_finish_token_mismatch");
        }

        string? actualAccountId = ReadObfuscatedAccountId(body);
        if (!string.IsNullOrEmpty(expectedAccountId))
        {
            if (string.IsNullOrEmpty(actualAccountId) || !string.Equals(actualAccountId, expectedAccountId, StringComparison.Ordinal))
                throw new VerifierRefusal(403, "google_finish_account_mismatch");
        }
        else if (!string.IsNullOrEmpty(actualAccountId) && !string.IsNullOrEmpty(authenticatedPlayerId))
        {
            bool matchesPlayer = string.Equals(actualAccountId, authenticatedPlayerId, StringComparison.Ordinal) ||
                AppAccountTokens.Normalize(actualAccountId) == AppAccountTokens.Derive(authenticatedPlayerId);
            if (!matchesPlayer)
                throw new VerifierRefusal(403, "google_finish_account_mismatch");
        }

        if (kind == ProductKind.Subscription)
        {
            string state = Text(body, "subscriptionState");
            if (state is not ("SUBSCRIPTION_STATE_ACTIVE" or "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" or
                "SUBSCRIPTION_STATE_CANCELED" or "SUBSCRIPTION_STATE_ON_HOLD" or "SUBSCRIPTION_STATE_PAUSED"))
                throw new VerifierRefusal(403, "google_finish_purchase_state_invalid");

            if (!body.TryGetProperty("lineItems", out var subItems) || subItems.ValueKind != JsonValueKind.Array ||
                !subItems.EnumerateArray().Any(i => Text(i, "productId") == expectedProductId))
                throw new VerifierRefusal(403, "google_finish_product_mismatch");

            if (!IsAcknowledged(body))
                throw new VerifierRefusal(503, "google_finish_not_acknowledged");
            return "acknowledged";
        }

        // One-time product: supports both Play v1 (purchases.products.get) and v2 (purchases.productsv2).
        bool isV2 = body.TryGetProperty("productLineItem", out var v2Items);
        if (isV2)
        {
            if (v2Items.ValueKind != JsonValueKind.Array || v2Items.GetArrayLength() != 1 ||
                Text(v2Items[0], "productId") != expectedProductId)
                throw new VerifierRefusal(403, "google_finish_product_mismatch");
            var item = v2Items[0];
            if (item.TryGetProperty("productOfferDetails", out var offer) && offer.TryGetProperty("quantity", out var qv2) &&
                (qv2.ValueKind != JsonValueKind.Number || !qv2.TryGetInt32(out int q2) || q2 != 1))
                throw new VerifierRefusal(403, "google_finish_quantity_invalid");
            string pState = body.TryGetProperty("purchaseStateContext", out var ctx) ? Text(ctx, "purchaseState") : "";
            if (pState != "PURCHASED")
                throw new VerifierRefusal(403, "google_finish_purchase_state_invalid");

            if (kind == ProductKind.Consumable)
            {
                if (!IsConsumed(body) && !IsConsumed(item))
                    throw new VerifierRefusal(503, "google_finish_not_consumed");
                return "consumed";
            }
            if (!IsAcknowledged(body) && !IsAcknowledged(item))
                throw new VerifierRefusal(503, "google_finish_not_acknowledged");
            return "acknowledged";
        }

        // Play v1 shape: { purchaseState: 0, consumptionState: 1, acknowledgementState: 1, quantity?, productId?, obfuscatedExternalAccountId? }
        if (body.TryGetProperty("productId", out var prodProp) &&
            (prodProp.ValueKind != JsonValueKind.String || prodProp.GetString() != expectedProductId))
            throw new VerifierRefusal(403, "google_finish_product_mismatch");
        if (body.TryGetProperty("quantity", out var qv1) &&
            (qv1.ValueKind != JsonValueKind.Number || !qv1.TryGetInt32(out int q1) || q1 != 1))
            throw new VerifierRefusal(403, "google_finish_quantity_invalid");
        if (!body.TryGetProperty("purchaseState", out var psProp) || psProp.ValueKind != JsonValueKind.Number ||
            !psProp.TryGetInt32(out int purchaseState) || purchaseState != 0)
            throw new VerifierRefusal(403, "google_finish_purchase_state_invalid");

        if (kind == ProductKind.Consumable)
        {
            if (!IsConsumed(body))
                throw new VerifierRefusal(503, "google_finish_not_consumed");
            return "consumed";
        }
        if (!IsAcknowledged(body))
            throw new VerifierRefusal(503, "google_finish_not_acknowledged");
        return "acknowledged";
    }

    private static string? ReadObfuscatedAccountId(JsonElement body)
    {
        string direct = Text(body, "obfuscatedExternalAccountId");
        if (direct.Length > 0) return direct;
        if (body.TryGetProperty("externalAccountIdentifiers", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            string nested = Text(ext, "obfuscatedExternalAccountId");
            if (nested.Length > 0) return nested;
        }
        return null;
    }

    private static bool IsAcknowledged(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("acknowledgementState", out var ack)) return false;
        if (ack.ValueKind == JsonValueKind.Number && ack.TryGetInt32(out int i)) return i == 1;
        if (ack.ValueKind == JsonValueKind.String) return ack.GetString() == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED";
        return false;
    }

    private static bool IsConsumed(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("consumptionState", out var cons)) return false;
        if (cons.ValueKind == JsonValueKind.Number && cons.TryGetInt32(out int i)) return i == 1;
        if (cons.ValueKind == JsonValueKind.String) return cons.GetString() == "CONSUMPTION_STATE_CONSUMED";
        return false;
    }

    /// <summary>Reads a Play protobuf JSON boolean, treating an absent key as false (WO-20261118-898).</summary>
    internal static bool OptionalBool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new VerifierRefusal(422, "purchase_state_unknown"),
        };
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
