using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shorepop.League;

namespace LeagueServerChecks;

internal static partial class Program
{
    static async Task RecordHttpBinding(CampaignWin fixture)
    {
        // The production RecordEndpoint and UgsAuthentication run over loopback Kestrel.
        // Only the JWKS transport is replaced with ephemeral local fixture keys. There is no
        // production credential, auth bypass, configurable alternate issuer, or external call.
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "record-route-local-fixture" };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key); jwk.Alg = "RS256"; jwk.Use = "sig";
        var transport = new FakeJwks(JsonSerializer.Serialize(new { keys = new[] { jwk } }));
        using var jwksClient = new HttpClient(transport);
        var cache = new UgsJwksCache(jwksClient);
        var config = Config("record-http"); var repository = New(config);
        repository.Submit("playerA", fixture, Now);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(repository);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt => UgsAuthentication.Configure(jwt, config, cache));
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(rate => rate.AddFixedWindowLimiter("player", limit => {
            limit.PermitLimit = 30; limit.Window = TimeSpan.FromMinutes(1); limit.QueueLimit = 0;
        }));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (LeagueRefusal refusal)
            {
                context.Response.StatusCode = refusal.Status;
                await context.Response.WriteAsJsonAsync(new ErrorBody(refusal.Code));
            }
        });
        app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
        RecordEndpoint.Map(app);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(3) };
            string path = "/v1/records/" + fixture.Attempt!.StableSlot + "?rules=" + ReplayValidator.RulesVersion;
            using var anonymous = await client.GetAsync(path);
            Check(anonymous.StatusCode == HttpStatusCode.Unauthorized);
            string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
                Issuer = UgsAuthentication.Issuer, NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
                Subject = new ClaimsIdentity([new Claim("sub", "playerA"), new Claim("project_id", Project)]),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var omitted = await client.GetAsync(path);
            string omittedJson = await omitted.Content.ReadAsStringAsync();
            Check(omitted.StatusCode == HttpStatusCode.OK);
            var actual = JsonSerializer.Deserialize<RecordBest>(omittedJson, WireJson.Options)!;
            Check(actual.Known && actual.Score == fixture.Score && actual.StableSlot == fixture.Attempt.StableSlot && actual.Category == LeagueOptions.Category);
            using var explicitPolicy = await client.GetAsync(path + "&category=unboosted");
            Check(explicitPolicy.StatusCode == HttpStatusCode.OK && await explicitPolicy.Content.ReadAsStringAsync() == omittedJson);
            using var mismatch = await client.GetAsync(path + "&category=boosted");
            string mismatchJson = await mismatch.Content.ReadAsStringAsync();
            Check((int)mismatch.StatusCode == 422 && mismatchJson.Contains("invalid_record_scope"));
            using var empty = await client.GetAsync(path + "&category=");
            Check((int)empty.StatusCode == 422);
            Check(transport.Calls == 1); // The requests used the actual bearer validator and cached fixture JWKS.
            File.WriteAllText(Path.Combine(root, "record-http.json"), JsonSerializer.Serialize(new {
                authentication = "Production RS256 UGS bearer validator with ephemeral local JWT/JWKS fixture; no real credentials",
                anonymous = (int)anonymous.StatusCode, omittedCategory = (int)omitted.StatusCode, omittedBody = actual,
                matchingCategory = (int)explicitPolicy.StatusCode, mismatchedCategory = (int)mismatch.StatusCode,
                mismatchBody = mismatchJson, emptyCategory = (int)empty.StatusCode, jwksRequests = transport.Calls
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { await app.StopAsync(); }
    }
}
