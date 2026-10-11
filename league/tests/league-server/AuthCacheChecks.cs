using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shorepop.League;

namespace LeagueServerChecks;

internal static partial class Program
{
    static async Task AuthenticationRefreshRecovery()
    {
        const string expectedProject = "aea5f7f0-ebcb-4745-98c5-378bb2ad2feb";
        ClaimsPrincipal Claims(string project, bool duplicateSubject = false, bool includeNotBefore = true)
        {
            var claims = new List<Claim> { new("sub", "playerA"), new("project_id", project) };
            if (duplicateSubject) claims.Add(new("sub", "playerA"));
            if (includeNotBefore) claims.Add(new("nbf", "1"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims));
        }

        Check(UgsAuthentication.ValidClaims(Claims(expectedProject), expectedProject));
        Check(!UgsAuthentication.ValidClaims(Claims("11111111-1111-4111-8111-111111111111"), expectedProject));
        Check(!UgsAuthentication.ValidClaims(Claims(expectedProject.ToUpperInvariant()), expectedProject));
        Check(!UgsAuthentication.ValidClaims(Claims(expectedProject), expectedProject.ToUpperInvariant()));
        Check(!UgsAuthentication.ValidClaims(Claims(expectedProject, duplicateSubject: true), expectedProject));
        Check(!UgsAuthentication.ValidClaims(Claims(expectedProject, includeNotBefore: false), expectedProject));

        using RSA rsa = RSA.Create(2048);
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "local-refresh-recovery-key" };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
        jwk.Alg = "RS256";
        jwk.Use = "sig";
        string keys = JsonSerializer.Serialize(new { keys = new[] { jwk } });
        var transport = new RefreshRecoveryJwks(keys);
        using var client = new HttpClient(transport);
        DateTime clock = Now;
        var cache = new UgsJwksCache(client, () => clock);

        var first = await cache.GetConfigurationAsync(default);
        Check(first.Issuer == UgsAuthentication.Issuer && first.SigningKeys.Count == 1 &&
            first.SigningKeys.Single().KeyId == publicKey.KeyId && transport.Calls == 1 && !cache.Unavailable);
        Check(ReferenceEquals(first, await cache.GetConfigurationAsync(default)) && transport.Calls == 1);

        // Force rotation while the eight-hour cache is still fresh. A failed fetch must make
        // that cached configuration unavailable until a later successful refresh.
        clock = clock.AddSeconds(61);
        transport.Status = HttpStatusCode.ServiceUnavailable;
        cache.RequestRefresh();
        bool failed = false;
        try { await cache.GetConfigurationAsync(default); }
        catch (HttpRequestException error) { failed = error.StatusCode == HttpStatusCode.ServiceUnavailable; }
        Check(failed && transport.Calls == 2 && cache.Unavailable);

        bool throttled = false;
        try { await cache.GetConfigurationAsync(default); }
        catch (InvalidOperationException) { throttled = true; }
        Check(throttled && transport.Calls == 2 && cache.Unavailable);

        clock = clock.AddSeconds(61);
        transport.Status = HttpStatusCode.OK;
        var recovered = await cache.GetConfigurationAsync(default);
        Check(!ReferenceEquals(first, recovered) && recovered.SigningKeys.Count == 1 &&
            recovered.SigningKeys.Single().KeyId == publicKey.KeyId && transport.Calls == 3 && !cache.Unavailable);
        Check(ReferenceEquals(recovered, await cache.GetConfigurationAsync(default)) && transport.Calls == 3);

        await AuthenticationRefreshHttpRecovery(expectedProject,
            new RsaSecurityKey(rsa) { KeyId = publicKey.KeyId }, keys);
    }

    static async Task AuthenticationRefreshHttpRecovery(string expectedProject, RsaSecurityKey signingKey, string keys)
    {
        // Exercise the production JwtBearer events on loopback, including the HTTP response
        // when metadata retrieval throws. Only the JWKS transport and cache clock are local fixtures.
        var transport = new RefreshRecoveryJwks(keys);
        using var jwksClient = new HttpClient(transport);
        DateTime clock = Now;
        var cache = new UgsJwksCache(jwksClient, () => clock);
        var settings = new LeagueOptions { ProjectId = expectedProject };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt => UgsAuthentication.Configure(jwt, settings, cache));
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/authentication-check", (HttpContext context) =>
            Results.Ok(new { player = context.User.FindFirst("sub")?.Value })).RequireAuthorization();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };
            var tokenHandler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
            long issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string Token(string project, bool duplicateSubject = false, bool includeNotBefore = true)
            {
                var claims = new Dictionary<string, object> {
                    ["iss"] = UgsAuthentication.Issuer,
                    ["sub"] = duplicateSubject ? new[] { "playerA", "playerA" } : "playerA",
                    ["project_id"] = project,
                    ["exp"] = issued + 1200
                };
                if (includeNotBefore) claims["nbf"] = issued - 60;
                return tokenHandler.CreateToken(JsonSerializer.Serialize(claims),
                    new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
            }
            async Task Response(string token, HttpStatusCode expectedStatus, string? expectedError = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/authentication-check");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == expectedStatus);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(expectedError == null
                    ? body.RootElement.GetProperty("player").GetString() == "playerA"
                    : body.RootElement.GetProperty("code").GetString() == expectedError);
            }

            string valid = Token(expectedProject);
            await Response(valid, HttpStatusCode.OK);
            Check(transport.Calls == 1 && !cache.Unavailable);

            clock = clock.AddSeconds(61);
            transport.Status = HttpStatusCode.ServiceUnavailable;
            cache.RequestRefresh();
            await Response(valid, HttpStatusCode.ServiceUnavailable, "authentication_keys_unavailable");
            Check(transport.Calls == 2 && cache.Unavailable);
            await Response(valid, HttpStatusCode.ServiceUnavailable, "authentication_keys_unavailable");
            Check(transport.Calls == 2 && cache.Unavailable);

            clock = clock.AddSeconds(61);
            transport.Status = HttpStatusCode.OK;
            await Response(valid, HttpStatusCode.OK);
            Check(transport.Calls == 3 && !cache.Unavailable);

            foreach (string rejected in new[] {
                Token("11111111-1111-4111-8111-111111111111"),
                Token(expectedProject.ToUpperInvariant()),
                Token(expectedProject, duplicateSubject: true),
                Token(expectedProject, includeNotBefore: false)
            }) await Response(rejected, HttpStatusCode.Unauthorized, "authentication_required");
            Check(transport.Calls == 3 && !cache.Unavailable);
        }
        finally { await app.StopAsync(); }
    }

    sealed class RefreshRecoveryJwks(string content) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri ==
                "https://player-auth.services.api.unity.com/.well-known/jwks.json");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(content) });
        }
    }
}
