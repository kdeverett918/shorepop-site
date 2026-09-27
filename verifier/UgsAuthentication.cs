// Adapted from server/league/UgsAuthentication.cs (same validation: Unity JWKS, issuer, RS256, lifetime, single sub, project_id).
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Shorepop.Verifier;

public static class UgsAuthentication
{
    public const string Issuer = "https://player-auth.services.api.unity.com";
    public const string Jwks = Issuer + "/.well-known/jwks.json";
    public static TokenValidationParameters Parameters() => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer,
        // UGS documents project_id as its project boundary; an aud claim is not required.
        ValidateAudience = false, ValidateLifetime = true, RequireExpirationTime = true,
        RequireSignedTokens = true, ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub"
    };
    public static bool ValidClaims(ClaimsPrincipal principal, string projectId)
    {
        var subjects = principal.FindAll("sub").ToArray();
        var projects = principal.FindAll("project_id").ToArray();
        return Guid.TryParse(projectId, out _) && subjects.Length == 1 && projects.Length == 1 &&
            VerifierOptions.ValidPlayer(subjects[0].Value) && projects[0].Value == projectId &&
            principal.HasClaim(c => c.Type == "nbf");
    }
    public static void Configure(JwtBearerOptions jwt, VerifierOptions settings, UgsJwksCache cache)
    {
        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = true;
        jwt.TokenValidationParameters = Parameters();
        jwt.ConfigurationManager = cache;
        jwt.RefreshOnIssuerKeyNotFound = true;
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal == null || !ValidClaims(context.Principal, settings.ProjectId))
                    context.Fail("Invalid UGS subject or project.");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                if (cache.Unavailable) context.HttpContext.Items["ugs-keys-unavailable"] = true;
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                bool unavailable = context.HttpContext.Items.ContainsKey("ugs-keys-unavailable");
                context.Response.StatusCode = unavailable ? 503 : 401;
                await context.Response.WriteAsJsonAsync(new ErrorBody(unavailable ? "authentication_keys_unavailable" : "authentication_required"));
            }
        };
    }
}

/// <summary>Only the fixed Unity HTTPS endpoint is fetched. Empty/expired caches fail closed.</summary>
public sealed class UgsJwksCache(HttpClient client) : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private OpenIdConnectConfiguration? cached;
    private DateTime expires = DateTime.MinValue;
    private DateTime lastFetch = DateTime.MinValue;
    private volatile bool refresh;
    public bool Unavailable { get; private set; }
    public void RequestRefresh() => refresh = true;
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        await gate.WaitAsync(cancel);
        try
        {
            DateTime now = DateTime.UtcNow;
            bool tooSoon = now - lastFetch < TimeSpan.FromMinutes(1);
            if (cached != null && now < expires && (!refresh || tooSoon)) return cached;
            if (tooSoon) throw new InvalidOperationException("Unity signing keys unavailable; refresh throttled.");
            lastFetch = now;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var response = await client.GetAsync(UgsAuthentication.Jwks, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 262144) throw new HttpRequestException("JWKS too large.");
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + count > 262144) throw new HttpRequestException("JWKS too large.");
                    buffer.Write(chunk, 0, count);
                }
                var jwks = new JsonWebKeySet(Encoding.UTF8.GetString(buffer.ToArray()));
                var next = new OpenIdConnectConfiguration { Issuer = UgsAuthentication.Issuer };
                foreach (var key in jwks.Keys)
                    if (key.Kty == "RSA" && key.Alg == "RS256" && key.Use == "sig" && !string.IsNullOrEmpty(key.Kid) && key.KeySize >= 2048)
                        next.SigningKeys.Add(key);
                if (next.SigningKeys.Count == 0) throw new HttpRequestException("No supported Unity signing keys.");
                cached = next; expires = now.AddHours(8); refresh = false; Unavailable = false;
                return next;
            }
            catch
            {
                Unavailable = true;
                // A forced rotation refresh failure also fails the current validation. No expired
                // or silently stale key fallback is used to turn a retrieval failure into success.
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
