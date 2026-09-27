namespace Shorepop.Verifier;

/// <summary>All configuration comes from environment variables (see README.md). Secrets are never logged.</summary>
public sealed class VerifierOptions
{
    public string ProjectId { get; init; } = "";
    public string BundleId { get; init; } = ShorepopCatalog.BundleId;
    public string DatabasePath { get; init; } = "/home/app/data/verifier.db";
    public string StoreKind { get; init; } = "sqlite";
    public IReadOnlySet<string> AppleEnvironments { get; init; } = AppleTransactionChecker.DefaultEnvironments;
    public string? GoogleServiceAccountJson { get; init; }
    public string? AppleKeyId { get; init; }
    public string? AppleIssuerId { get; init; }
    public string? ApplePrivateKey { get; init; }

    public bool Configured => Guid.TryParse(ProjectId, out _) && BundleId == ShorepopCatalog.BundleId;

    public static VerifierOptions FromConfiguration(IConfiguration c)
    {
        string? environments = c["APPLE_ALLOWED_ENVIRONMENTS"];
        return new VerifierOptions
        {
            ProjectId = c["UGS_PROJECT_ID"] ?? "",
            BundleId = string.IsNullOrWhiteSpace(c["APP_BUNDLE_ID"]) ? ShorepopCatalog.BundleId : c["APP_BUNDLE_ID"]!,
            DatabasePath = string.IsNullOrWhiteSpace(c["VERIFIER_DB_PATH"]) ? "/home/app/data/verifier.db" : c["VERIFIER_DB_PATH"]!,
            StoreKind = string.IsNullOrWhiteSpace(c["VERIFIER_STORE"]) ? "sqlite" : c["VERIFIER_STORE"]!,
            AppleEnvironments = string.IsNullOrWhiteSpace(environments) ? AppleTransactionChecker.DefaultEnvironments
                : new HashSet<string>(environments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal),
            GoogleServiceAccountJson = c["GOOGLE_PLAY_SA_JSON"],
            AppleKeyId = c["APPLE_IAP_KEY_ID"],
            AppleIssuerId = c["APPLE_IAP_ISSUER_ID"],
            ApplePrivateKey = c["APPLE_IAP_PRIVATE_KEY"],
        };
    }

    public static bool ValidPlayer(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(char.IsAsciiLetterOrDigit);
}
