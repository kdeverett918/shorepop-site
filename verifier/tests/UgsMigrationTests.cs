using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Shorepop.Verifier;

internal static class UgsMigrationTests
{
    private const string OldProject = "26418e78-9eb7-42c5-955c-b4c141443252";
    private const string NewProject = "aea5f7f0-ebcb-4745-98c5-378bb2ad2feb";

    public static void Run(Action<string, Action> test)
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "migration-test-key" };
        var settings = Options(OldProject, NewProject);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        string Token(string project, string? issuer = null, DateTime? expires = null,
            DateTime? notBefore = null, string algorithm = SecurityAlgorithms.RsaSha256,
            SecurityKey? signingKey = null, bool duplicateProject = false)
        {
            var claims = new List<Claim> { new("sub", "existingPlayer123"), new("project_id", project) };
            if (duplicateProject) claims.Add(new("project_id", OldProject));
            var payload = new JwtPayload(issuer ?? UgsAuthentication.Issuer, null, claims,
                notBefore ?? DateTime.UtcNow.AddMinutes(-5), expires ?? DateTime.UtcNow.AddMinutes(10));
            return handler.WriteToken(new JwtSecurityToken(
                new JwtHeader(new SigningCredentials(signingKey ?? key, algorithm)), payload));
        }

        bool Accepts(string token, VerifierOptions? options = null)
        {
            var validation = UgsAuthentication.Parameters();
            validation.IssuerSigningKey = key;
            try
            {
                var principal = handler.ValidateToken(token, validation, out _);
                return UgsAuthentication.ValidClaims(principal, options ?? settings);
            }
            catch (SecurityTokenException) { return false; }
        }

        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        test("UGS migration: both exact signed project IDs accepted and subject retained", () =>
        {
            Check(Accepts(Token(OldProject)), "old project refused");
            Check(Accepts(Token(NewProject)), "new project refused");
            var validation = UgsAuthentication.Parameters(); validation.IssuerSigningKey = key;
            var principal = handler.ValidateToken(Token(NewProject), validation, out _);
            Check(principal.FindFirst("sub")!.Value == "existingPlayer123", "subject changed");
        });
        test("UGS migration: unrelated and duplicate project claims refused", () =>
        {
            Check(!Accepts(Token("00000000-0000-0000-0000-000000000001")), "unlisted project accepted");
            Check(!Accepts(Token(NewProject, duplicateProject: true)), "multiple project claims accepted");
            Check(!Accepts(Token(NewProject), Options(OldProject, null)), "additional project accepted without configuration");
        });
        test("UGS migration: signature issuer and algorithm checks remain enforced", () =>
        {
            Check(!Accepts(Token(NewProject, issuer: "https://example.invalid")), "wrong issuer accepted");
            Check(!Accepts(Token(NewProject, signingKey: new RsaSecurityKey(otherRsa))), "wrong signature accepted");
            Check(!Accepts(Token(NewProject, algorithm: SecurityAlgorithms.RsaSha512)), "wrong algorithm accepted");
        });
        test("UGS migration: expired and future tokens refused", () =>
        {
            Check(!Accepts(Token(NewProject, expires: DateTime.UtcNow.AddMinutes(-2))), "expired token accepted");
            Check(!Accepts(Token(NewProject, notBefore: DateTime.UtcNow.AddMinutes(2))), "future token accepted");
        });
        test("UGS migration: allowlist parsing is exact and rejects invalid configuration", () =>
        {
            Check(Options(NewProject, " " + OldProject + " ").AllowsProject(OldProject), "trimmed ID refused");
            Check(!Options(NewProject, OldProject).AllowsProject(OldProject.ToUpperInvariant()), "non-exact ID accepted");
            foreach (string invalid in new[] { "*", OldProject + ",", "not-a-project", string.Join(',', Enumerable.Repeat(OldProject, 9)) })
            {
                bool refused = false;
                try { Options(NewProject, invalid); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "invalid additional projects accepted");
            }
        });
    }

    private static VerifierOptions Options(string primary, string? additional) =>
        VerifierOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["UGS_PROJECT_ID"] = primary, ["UGS_ADDITIONAL_PROJECT_IDS"] = additional }).Build());
}
