using BloomHarbor;

namespace Shorepop.League;

public static class ReplayValidator
{
    // Must equal CampaignAttempts.RulesVersion, and this server must run the same Core as the client
    // that sends it: deploy it before a client with new board rules ships (README.md, deploy order).
    public const string RulesVersion = "shorepop-big-wave-v4";
    public const int MaximumActions = 2000;
    public static void Validate(CampaignWin win, DateTime receivedUtc) => Validate(win, receivedUtc, false);

    public static void Validate(CampaignWin win, DateTime receivedUtc, bool allowTailwind)
    {
        var a = win.Attempt;
        if (receivedUtc.Kind != DateTimeKind.Utc || a == null || string.IsNullOrEmpty(a.Id) || a.Id.Length != 32 ||
            !Guid.TryParseExact(a.Id, "N", out _) || a.Id != a.Id.ToLowerInvariant() || !a.EvidenceComplete ||
            a.Actions == null || a.Actions.Count < 1 || a.Actions.Count > MaximumActions ||
            a.LevelId < 1 || a.LevelId > 300 || a.StableSlot < 1 || a.StableSlot > 300 ||
            a.Rules != RulesVersion || a.StartedUtcTicks <= 0 || a.StartedUtcTicks > receivedUtc.AddMinutes(5).Ticks ||
            win.CompletedUtcTicks < a.StartedUtcTicks || win.CompletedUtcTicks > receivedUtc.AddMinutes(5).Ticks ||
            win.Score <= 0 || win.Stars < 1 || win.Stars > 3)
            throw new LeagueRefusal(422, "invalid_evidence");
        if (a.Boosters < 0 || (a.Boosters & ~BoosterLoadout.AllMask) != 0 || a.CreditedBoosters < 0 ||
            (a.CreditedBoosters & ~a.Boosters) != 0 ||
            a.PaidCoins != BoosterCatalog.TotalPrice(new BoosterLoadout(a.Boosters & ~a.CreditedBoosters)) ||
            a.StreakBoosters < 0 || (a.StreakBoosters & ~a.CreditedBoosters) != 0 ||
            (a.StreakBoosters != 0 && !WinStreak.IsTierMask(a.StreakBoosters)))
            throw new LeagueRefusal(422, "invalid_loadout");
        // There is no trusted inventory/entitlement service in this backend. Neither paidCoins,
        // creditedBoosters nor a replay proves ownership, so bought and daily-credit boosters are
        // refused whatever the configuration. The one exception, only under the Tailwind policy,
        // is a board whose every booster is exactly one Tailwind step's free loadout.
        bool tailwindOnly = allowTailwind && a.Boosters != 0 && a.Boosters == a.StreakBoosters &&
            a.CreditedBoosters == a.Boosters && a.PaidCoins == 0 && WinStreak.IsTierMask(a.Boosters);
        if ((a.Boosters != 0 && !tailwindOnly) || a.Actions.Any(x => x != null && (x.Kind == "ability" || x.Kind == "revive")))
            throw new LeagueRefusal(422, "entitlement_unverified");
        var definition = DefinitionForSlot(a.StableSlot);
        if (a.Seed != definition.Seed) throw new LeagueRefusal(422, "seed_mismatch");
        string? refusal = Replay(definition, win);
        // A level that gained an authored opening special (design uplift S1 #7: Rocket practice's row
        // rocket) also accepts the board a client dealt before it, under the same rules version: that
        // board starts without the free rocket, so nothing is gained by claiming it, and a client on it keeps
        // scoring once this server runs the newer catalog. The first refusal is the one reported.
        if (refusal != null && definition.HasAuthoredSpecials && Replay(definition.WithoutAuthoredSpecials(), win) == null) refusal = null;
        if (refusal != null) throw new LeagueRefusal(422, refusal);
    }

    /// <summary>Replays the win on this definition's board: null when it reproduces the claim, else the refusal code.</summary>
    static string? Replay(LevelDefinition definition, CampaignWin win)
    {
        var a = win.Attempt!; // Validate refused a missing attempt or action list before any replay
        var board = BoardModel.NewLevel(definition, new BoosterLoadout(a.Boosters));
        foreach (var action in a.Actions!)
        {
            if (action == null || !ApplyAction(board, action)) return "illegal_action";
        }
        if (board.Status != GameStatus.Won || board.Score != win.Score || board.StarsEarned != win.Stars ||
            board.ExportSnapshot().BoostersApplied != a.Boosters)
            return "replay_mismatch";
        return null;
    }

    public static LevelDefinition DefinitionForSlot(int stableSlot)
    {
        // Numeric level labels change during expansion. Permanent slots are the replay identity.
        for (int id = 1; id <= LevelCatalog.Count; id++)
            if (LevelCatalog.SlotOf(id) == stableSlot) return LevelCatalog.Get(id);
        throw new LeagueRefusal(422, "unknown_stable_slot");
    }

    // Pure replay supports all recorded actions. Validate above separately refuses abilities
    // until a server-authoritative entitlement provider exists; Undo does not erase that refusal.
    public static bool ApplyAction(BoardModel board, CampaignAction action) => action.Kind switch
    {
        "tap" => board.Tap(action.X, action.Y).Accepted,
        "breeze" => board.UseBreeze(action.Right).Accepted,
        "ability" => board.UseAbility((AbilityType)action.Ability, action.X, action.Y).Accepted,
        "undo" => board.Undo(),
        _ => false
    };
}
