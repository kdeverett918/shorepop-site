namespace Shorepop.League;

public sealed class LeagueOptions
{
    public string ProjectId { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public string DatabaseProvider { get; set; } = "sqlite";
    public string DatabaseUrl { get; set; } = "";
    public string RepeatPolicy { get; set; } = "UNCONFIGURED";
    public string BoosterPolicy { get; set; } = "UNCONFIGURED";
    // Operational configuration must cite the owner's decision; this code does not choose it.
    public string OwnerDecisionReference { get; set; } = "";
    public const string Category = "unboosted";
    public bool Configured => Guid.TryParse(ProjectId, out _) &&
        ((DatabaseProvider == "sqlite" && !string.IsNullOrWhiteSpace(DatabasePath) && Path.IsPathFullyQualified(DatabasePath)) ||
         (DatabaseProvider == "postgres" && !string.IsNullOrWhiteSpace(DatabaseUrl))) &&
        !string.IsNullOrWhiteSpace(OwnerDecisionReference) &&
        (RepeatPolicy == "BEST_PER_LEVEL_PER_WEEK" || RepeatPolicy == "FIRST_CLEAR_PER_LEVEL_EVER") &&
        (BoosterPolicy == "UNBOOSTED_ONLY" || BoosterPolicy == TailwindPolicy);
    /// <summary>
    /// Also accepts a board started with the free Tailwind streak boosters (exactly one streak
    /// step's loadout, all credited, no coins). The streak itself is claimed by the client and not
    /// verifiable here, so choosing this policy is an owner decision like the other two.
    /// </summary>
    public const string TailwindPolicy = "UNBOOSTED_OR_TAILWIND";
    public bool AllowsTailwind => BoosterPolicy == TailwindPolicy;
    public void RequireConfigured()
    {
        if (!Configured) throw new LeagueRefusal(503, "league_unconfigured");
    }
}
