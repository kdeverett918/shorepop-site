using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public interface IAppleServerApi
{
    bool Configured { get; }
    /// <summary>signedTransactionInfo for one transaction, or null when Apple reports it unknown.</summary>
    Task<string?> GetTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel);
    /// <summary>signedTransactionInfo of every lastTransactions row of Get All Subscription Statuses.</summary>
    Task<IReadOnlyList<string>> GetSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel);
}

/// <summary>
/// App Store Server API client, active only when APPLE_IAP_KEY_ID, APPLE_IAP_ISSUER_ID and
/// APPLE_IAP_PRIVATE_KEY (the .p8 PEM text) are all set. Responses are JWS and are verified by the caller
/// with the same chain verifier as client-supplied transactions; nothing here is trusted on its own.
/// </summary>
public sealed class AppStoreServerApi : IAppleServerApi
{
    private const string ProductionBase = "https://api.storekit.itunes.apple.com";
    private const string SandboxBase = "https://api.storekit-sandbox.itunes.apple.com";
    private readonly HttpClient http;
    private readonly string? keyId, issuerId, privateKeyPem, bundleId;

    public AppStoreServerApi(HttpClient http, string? keyId, string? issuerId, string? privateKeyPem, string bundleId)
    {
        this.http = http; this.keyId = keyId; this.issuerId = issuerId; this.bundleId = bundleId;
        // Render env values often carry literal "\n" instead of newlines.
        this.privateKeyPem = privateKeyPem?.Replace("\\n", "\n");
    }

    public bool Configured => !string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(issuerId) && !string.IsNullOrWhiteSpace(privateKeyPem);

    public async Task<string?> GetTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel)
    {
        using var doc = await GetAsync($"/inApps/v1/transactions/{Uri.EscapeDataString(transactionId)}", environment, cancel);
        if (doc == null) return null;
        return doc.RootElement.TryGetProperty("signedTransactionInfo", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    public async Task<IReadOnlyList<string>> GetSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel)
    {
        using var doc = await GetAsync($"/inApps/v1/subscriptions/{Uri.EscapeDataString(transactionId)}", environment, cancel);
        var list = new List<string>();
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var group in data.EnumerateArray())
        {
            if (!group.TryGetProperty("lastTransactions", out var last) || last.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in last.EnumerateArray())
                if (row.TryGetProperty("signedTransactionInfo", out var s) && s.ValueKind == JsonValueKind.String) list.Add(s.GetString()!);
        }
        return list;
    }

    private async Task<JsonDocument?> GetAsync(string path, string environment, CancellationToken cancel)
    {
        if (!Configured) throw new VerifierRefusal(503, "apple_server_api_not_configured");
        string baseUrl = environment == "Production" ? ProductionBase : SandboxBase;
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        using var response = await http.SendAsync(request, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new VerifierRefusal(503, "apple_server_api_unavailable");
        var body = await response.Content.ReadAsByteArrayAsync(cancel);
        if (body.Length > 1024 * 1024) throw new VerifierRefusal(503, "apple_server_api_unavailable");
        return JsonDocument.Parse(body);
    }

    private string Token()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["alg"] = "ES256", ["kid"] = keyId!, ["typ"] = "JWT" }));
        string claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = issuerId!, ["iat"] = now, ["exp"] = now + 600, ["aud"] = "appstoreconnect-v1", ["bid"] = bundleId!,
        }));
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return header + "." + claims + "." + B64(signature);
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
