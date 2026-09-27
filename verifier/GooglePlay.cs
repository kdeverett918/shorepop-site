using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public interface IGooglePlayApi
{
    bool Configured { get; }
    /// <summary>purchases.productsv2.getproductpurchasev2 body, or null when Google reports the token unknown/invalid.</summary>
    Task<JsonElement?> GetProductPurchaseAsync(string packageName, string token, CancellationToken cancel);
    /// <summary>purchases.subscriptionsv2.get body, or null when Google reports the token unknown/invalid.</summary>
    Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel);
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
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessExpires;

    public GooglePlayApi(HttpClient http, string? serviceAccountJson)
    {
        this.http = http;
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

    public Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel) =>
        GetAsync($"{Uri.EscapeDataString(packageName)}/purchases/subscriptionsv2/tokens/{Uri.EscapeDataString(token)}", cancel);

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
        if (body.Length > 1024 * 1024) throw new VerifierRefusal(503, "google_api_unavailable");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
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
        string environment = body.TryGetProperty("testPurchaseContext", out _) ? "LicenseTest" : "Production";
        return new StoreVerdict(ShorepopCatalog.GoogleStore, packageName, productId, token, environment, refunded, 0, null, null, 0);
    }

    public static StoreVerdict Subscription(JsonElement body, string packageName, string productId, string token)
    {
        string state = Text(body, "subscriptionState");
        if (state is "SUBSCRIPTION_STATE_PENDING") throw new VerifierRefusal(409, "purchase_pending");
        if (state is "" or "SUBSCRIPTION_STATE_UNSPECIFIED" or "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED")
            throw new VerifierRefusal(422, "purchase_state_unknown");
        if (!body.TryGetProperty("lineItems", out var items) || items.ValueKind != JsonValueKind.Array) throw new VerifierRefusal(422, "line_items_invalid");
        long expiry = 0;
        bool found = false;
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "productId") != productId) continue;
            found = true;
            if (DateTimeOffset.TryParse(Text(item, "expiryTime"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                expiry = Math.Max(expiry, at.UtcTicks);
        }
        if (!found) throw new VerifierRefusal(422, "product_mismatch");
        if (expiry <= 0) throw new VerifierRefusal(422, "expiry_missing");
        string environment = body.TryGetProperty("testPurchase", out _) ? "LicenseTest" : "Production";
        string linked = Text(body, "linkedPurchaseToken");
        // Revocation/refund of a subscription shows up as EXPIRED with expiryTime moved to the revocation.
        return new StoreVerdict(ShorepopCatalog.GoogleStore, packageName, productId, token, environment, false, expiry,
            linked.Length == 0 ? null : linked, null, 0);
    }

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
