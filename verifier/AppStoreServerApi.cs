using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public sealed record AppleServerTransactionLookup(
    string? SignedTransactionInfo, string ResolvedEnvironment, bool UsedSandboxFallback);

public sealed record AppleServerSubscriptionLookup(
    IReadOnlyList<string> SignedTransactions, string ResolvedEnvironment, bool UsedSandboxFallback);

public interface IAppleServerApi
{
    bool Configured { get; }
    /// <summary>signedTransactionInfo for one transaction, or null when Apple reports it unknown.</summary>
    Task<string?> GetTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel);
    /// <summary>signedTransactionInfo of every lastTransactions row of Get All Subscription Statuses.</summary>
    Task<IReadOnlyList<string>> GetSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel);

    /// <summary>Transaction lookup preserving whether Production fell back one-way to Sandbox (WO-20261114-810).</summary>
    async Task<AppleServerTransactionLookup> LookupTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel) =>
        new(await GetTransactionInfoAsync(transactionId, environment, cancel), environment, false);

    /// <summary>Subscription lookup preserving whether Production fell back one-way to Sandbox (WO-20261114-810).</summary>
    async Task<AppleServerSubscriptionLookup> LookupSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel) =>
        new(await GetSubscriptionLastTransactionsAsync(transactionId, environment, cancel), environment, false);
}

/// <summary>
/// App Store Server API client, active only when APPLE_IAP_KEY_ID, APPLE_IAP_ISSUER_ID and
/// APPLE_IAP_PRIVATE_KEY (the .p8 PEM text) are all set. Responses are JWS and are verified by the caller
/// with the same chain verifier as client-supplied transactions; nothing here is trusted on its own.
/// In Production scope only, performs a one-way Production -> Sandbox fallback on transaction-not-found
/// (404 / 4040010 / 4040008), sandbox-receipt (21007), or Production API unavailability so Apple App Review
/// sandbox purchases settle cleanly (WO-20261114-810). Sandbox scope never queries Production.
/// </summary>
public sealed class AppStoreServerApi : IAppleServerApi
{
    public const string ProductionBase = "https://api.storekit.itunes.apple.com";
    public const string SandboxBase = "https://api.storekit-sandbox.itunes.apple.com";
    private const int TransactionIdNotFoundError = 4040010;
    private const int OriginalTransactionIdNotFoundError = 4040008;
    private const int SandboxReceiptStatus = 21007;

    private readonly HttpClient http;
    private readonly string? keyId, issuerId, privateKeyPem, bundleId;
    private readonly GrowingBoundsMonitor? bounds;

    private enum AttemptOutcome { Success, NotFound, SandboxReceiptError, Unavailable, Rejected }

    public AppStoreServerApi(HttpClient http, string? keyId, string? issuerId, string? privateKeyPem, string bundleId,
        GrowingBoundsMonitor? bounds = null)
    {
        this.http = http; this.keyId = keyId; this.issuerId = issuerId; this.bundleId = bundleId; this.bounds = bounds;
        // Render env values often carry literal "\n" instead of newlines.
        this.privateKeyPem = privateKeyPem?.Replace("\\n", "\n");
    }

    public bool Configured => !string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(issuerId) && !string.IsNullOrWhiteSpace(privateKeyPem);

    public async Task<string?> GetTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel) =>
        (await LookupTransactionInfoAsync(transactionId, environment, cancel)).SignedTransactionInfo;

    public async Task<IReadOnlyList<string>> GetSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel) =>
        (await LookupSubscriptionLastTransactionsAsync(transactionId, environment, cancel)).SignedTransactions;

    public async Task<AppleServerTransactionLookup> LookupTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel)
    {
        var (doc, resolvedEnv, fallback) = await GetWithFallbackAsync($"/inApps/v1/transactions/{Uri.EscapeDataString(transactionId)}", environment, cancel);
        using (doc)
        {
            if (doc == null) return new AppleServerTransactionLookup(null, resolvedEnv, fallback);
            string? signed = doc.RootElement.TryGetProperty("signedTransactionInfo", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            return new AppleServerTransactionLookup(signed, resolvedEnv, fallback);
        }
    }

    public async Task<AppleServerSubscriptionLookup> LookupSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel)
    {
        var (doc, resolvedEnv, fallback) = await GetWithFallbackAsync($"/inApps/v1/subscriptions/{Uri.EscapeDataString(transactionId)}", environment, cancel);
        var list = new List<string>();
        using (doc)
        {
            if (doc != null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in data.EnumerateArray())
                {
                    if (!group.TryGetProperty("lastTransactions", out var last) || last.ValueKind != JsonValueKind.Array) continue;
                    foreach (var row in last.EnumerateArray())
                        if (row.TryGetProperty("signedTransactionInfo", out var s) && s.ValueKind == JsonValueKind.String) list.Add(s.GetString()!);
                }
            }
        }
        return new AppleServerSubscriptionLookup(list, resolvedEnv, fallback);
    }

    private async Task<(JsonDocument? Doc, string ResolvedEnvironment, bool UsedSandboxFallback)> GetWithFallbackAsync(
        string path, string environment, CancellationToken cancel)
    {
        if (!Configured) throw new VerifierRefusal(503, "apple_server_api_not_configured");
        if (environment == "Production")
        {
            var (prodOutcome, prodDoc) = await SendOnceAsync(ProductionBase, path, cancel);
            if (prodOutcome == AttemptOutcome.Success) return (prodDoc, "Production", false);
            if (prodOutcome == AttemptOutcome.Rejected) throw new VerifierRefusal(422, "apple_server_api_rejected");

            // One-way Production -> Sandbox fallback (WO-20261114-810): only on Production not-found
            // (404 / 4040010 / 4040008), sandbox-receipt (21007), or Production unavailability.
            var (sbxOutcome, sbxDoc) = await SendOnceAsync(SandboxBase, path, cancel);
            if (sbxOutcome == AttemptOutcome.Success) return (sbxDoc, "Sandbox", true);
            if (sbxOutcome == AttemptOutcome.NotFound)
            {
                if (prodOutcome == AttemptOutcome.Unavailable)
                    throw new VerifierRefusal(503, "apple_server_api_unavailable");
                return (null, prodOutcome == AttemptOutcome.SandboxReceiptError ? "Sandbox" : "Production",
                    prodOutcome == AttemptOutcome.SandboxReceiptError);
            }
            if (sbxOutcome == AttemptOutcome.Rejected) throw new VerifierRefusal(422, "apple_server_api_rejected");
            throw new VerifierRefusal(503, "apple_server_api_unavailable");
        }

        // Sandbox scope never queries Production.
        var (outcome, doc) = await SendOnceAsync(SandboxBase, path, cancel);
        return outcome switch
        {
            AttemptOutcome.Success => (doc, "Sandbox", false),
            AttemptOutcome.NotFound => (null, "Sandbox", false),
            AttemptOutcome.Rejected => throw new VerifierRefusal(422, "apple_server_api_rejected"),
            _ => throw new VerifierRefusal(503, "apple_server_api_unavailable"),
        };
    }

    private async Task<(AttemptOutcome Outcome, JsonDocument? Doc)> SendOnceAsync(string baseUrl, string path, CancellationToken cancel)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
            response = await http.SendAsync(request, cancel);
        }
        catch (HttpRequestException)
        {
            return (AttemptOutcome.Unavailable, null);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (AttemptOutcome.Unavailable, null);
        }

        using (response)
        {
            byte[] body;
            try { body = await response.Content.ReadAsByteArrayAsync(cancel); }
            catch (HttpRequestException) { return (AttemptOutcome.Unavailable, null); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return (AttemptOutcome.Unavailable, null); }

            bounds?.Observe("apple_api_response_bytes", body.Length, GrowingBoundsMonitor.MaxStoreApiResponseBytes);
            if (body.Length > GrowingBoundsMonitor.MaxStoreApiResponseBytes)
                return (AttemptOutcome.Unavailable, null);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var doc = JsonDocument.Parse(body);
                    if (HasSandboxReceiptStatus(doc.RootElement))
                    {
                        doc.Dispose();
                        return (AttemptOutcome.SandboxReceiptError, null);
                    }
                    return (AttemptOutcome.Success, doc);
                }
                catch (JsonException)
                {
                    return (AttemptOutcome.Unavailable, null);
                }
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return (AttemptOutcome.NotFound, null);

            if (IsSandboxReceiptErrorBody(body))
                return (AttemptOutcome.SandboxReceiptError, null);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
                return (AttemptOutcome.Rejected, null);

            return (AttemptOutcome.Unavailable, null);
        }
    }

    private static bool IsSandboxReceiptErrorBody(byte[] body)
    {
        if (body.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return HasSandboxReceiptStatus(doc.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool HasSandboxReceiptStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int err) &&
            err is TransactionIdNotFoundError or OriginalTransactionIdNotFoundError or SandboxReceiptStatus)
            return true;
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int st) &&
            st == SandboxReceiptStatus)
            return true;
        return false;
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
