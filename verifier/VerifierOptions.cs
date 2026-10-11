namespace Shorepop.Verifier;

/// <summary>All configuration comes from environment variables (see README.md). Secrets are never logged.</summary>
public sealed class VerifierOptions
{
    public string ProjectId { get; init; } = "";
    public IReadOnlySet<string> AdditionalProjectIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string GooglePackageId { get; init; } = ShorepopCatalog.GooglePackageId;
    public string AppleBundleId { get; init; } = ShorepopCatalog.AppleBundleId;
    public string DatabasePath { get; init; } = "/home/app/data/verifier.db";
    public string StoreKind { get; init; } = "sqlite";
    public string? DatabaseUrl { get; init; }
    public string? RevenueProofKey { get; init; }
    public string? GoogleLicensePublicKey { get; init; }
    public long SubscriptionRevisionEpoch { get; init; }
    public IReadOnlySet<string> AppleEnvironments { get; init; } = AppleTransactionChecker.DefaultEnvironments;
    public string? GoogleServiceAccountJson { get; init; }
    public string? AppleKeyId { get; init; }
    public string? AppleIssuerId { get; init; }
    public string? ApplePrivateKey { get; init; }

    public bool Configured => Guid.TryParse(ProjectId, out _) && GooglePackageId == ShorepopCatalog.GooglePackageId &&
        AppleBundleId == ShorepopCatalog.AppleBundleId &&
        AdditionalProjectIds.Count <= 8 && AdditionalProjectIds.All(id => Guid.TryParseExact(id, "D", out _));

    public bool AllowsProject(string projectId) => Configured &&
        (string.Equals(projectId, ProjectId, StringComparison.Ordinal) || AdditionalProjectIds.Contains(projectId));

    public static VerifierOptions FromConfiguration(IConfiguration c)
    {
        string? environments = c["APPLE_ALLOWED_ENVIRONMENTS"];
        return new VerifierOptions
        {
            ProjectId = c["UGS_PROJECT_ID"] ?? "",
            AdditionalProjectIds = ParseAdditionalProjectIds(c["UGS_ADDITIONAL_PROJECT_IDS"]),
            GooglePackageId = c["GOOGLE_PACKAGE_ID"] ?? c["APP_BUNDLE_ID"] ?? ShorepopCatalog.GooglePackageId,
            AppleBundleId = c["APPLE_BUNDLE_ID"] ?? ShorepopCatalog.AppleBundleId,
            DatabasePath = string.IsNullOrWhiteSpace(c["VERIFIER_DB_PATH"]) ? "/home/app/data/verifier.db" : c["VERIFIER_DB_PATH"]!,
            StoreKind = string.IsNullOrWhiteSpace(c["VERIFIER_STORE"]) ? "sqlite" : c["VERIFIER_STORE"]!,
            DatabaseUrl = c["VERIFIER_DATABASE_URL"],
            RevenueProofKey = c["VERIFIER_REVENUE_PROOF_KEY"],
            GoogleLicensePublicKey = c["GOOGLE_PLAY_LICENSE_KEY"],
            SubscriptionRevisionEpoch = ParseRevisionEpoch(c["VERIFIER_SUBSCRIPTION_REVISION_EPOCH"]),
            AppleEnvironments = string.IsNullOrWhiteSpace(environments) ? AppleTransactionChecker.DefaultEnvironments
                : new HashSet<string>(environments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal),
            GoogleServiceAccountJson = c["GOOGLE_PLAY_SA_JSON"],
            AppleKeyId = c["APPLE_IAP_KEY_ID"],
            AppleIssuerId = c["APPLE_IAP_ISSUER_ID"],
            ApplePrivateKey = c["APPLE_IAP_PRIVATE_KEY"],
        };
    }

    private static IReadOnlySet<string> ParseAdditionalProjectIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new HashSet<string>(StringComparer.Ordinal);
        var ids = value.Split(',', StringSplitOptions.TrimEntries);
        if (ids.Length > 8 || ids.Any(id => !Guid.TryParseExact(id, "D", out _)))
            throw new InvalidOperationException("UGS_ADDITIONAL_PROJECT_IDS must contain at most eight comma-separated project UUIDs.");
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private static long ParseRevisionEpoch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long epoch) &&
            epoch >= 0 && epoch < long.MaxValue - 1_000_000) return epoch;
        throw new InvalidOperationException("VERIFIER_SUBSCRIPTION_REVISION_EPOCH must be a nonnegative revision baseline.");
    }

    public static bool ValidPlayer(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(char.IsAsciiLetterOrDigit);
}
