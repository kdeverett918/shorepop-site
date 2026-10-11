using System;

namespace BloomHarbor
{
    /// <summary>
    /// Tailwind, the campaign win streak (design/specs/meta-loop-r16.md, WP1). Consecutive
    /// first-time wins from <see cref="FromLevel"/> on fill a three-step meter, and each step starts
    /// the next campaign level with free boosters: one win a Rocket, two or more a Rocket and a Bomb.
    /// The third step is the meter's "full" state and gives what the second does: the owner's
    /// decision of 2026-09-22 took the extra moves out of it (they lifted the calibration player's
    /// win rate fifteen points), and a second Rocket cannot be expressed as a
    /// <see cref="BoosterLoadout"/>, which holds each booster once. A loss resets the meter.
    ///
    /// Pure data: the tier table and the one question every caller asks ("which loadout does this
    /// streak start a level with"). The count itself, and the rules for when it moves, live in
    /// <c>MetaProgress</c>, so the save owns the record and this owns the promise. The loadout goes
    /// through the ordinary deterministic <see cref="BoosterLoadout"/> path, so a Tailwind board is
    /// replayed and validated exactly like any other boosted board.
    /// </summary>
    public static class WinStreak
    {
        /// <summary>The first campaign level whose wins count and whose start takes Tailwind boosters:
        /// the mechanic lessons (4, 12, 20 and the Breeze pair) and the tutorial are behind it.</summary>
        public const int FromLevel = 15;
        /// <summary>Steps on the meter. A longer streak keeps the top step.</summary>
        public const int MaxTier = 3;

        /// <summary>The meter step a streak of <paramref name="wins"/> stands on, 0..<see cref="MaxTier"/>.</summary>
        public static int Tier(int wins) { return wins <= 0 ? 0 : Math.Min(MaxTier, wins); }

        /// <summary>True when a campaign level takes part at all: its wins count and its start is boosted.
        /// The authored lessons past <see cref="FromLevel"/> (Prism 20, Breeze 28 and 29) stay out:
        /// a booster dropped on a lesson board would rewrite the lesson, so they neither add to the
        /// streak nor reset it.</summary>
        public static bool Counts(int levelId)
        { return levelId >= FromLevel && levelId <= LevelCatalog.LevelCount && !LevelCatalog.IsMechanicLesson(levelId); }

        /// <summary>The free starting boosters of a step. Step 0 is the empty loadout.</summary>
        public static BoosterLoadout Loadout(int tier)
        {
            var loadout = new BoosterLoadout();
            if (tier >= 1) loadout.Set(BoosterType.StartRocket, true);
            if (tier >= 2) loadout.Set(BoosterType.StartBomb, true);
            return loadout;
        }

        /// <summary>The mask of <see cref="Loadout"/> for a step.</summary>
        public static int Mask(int tier) { return Loadout(Math.Max(0, Math.Min(MaxTier, tier))).Mask; }

        /// <summary>True for a mask that is exactly one step's loadout (step 1 to 3; steps 2 and 3
        /// share one). A validator uses this to refuse anything else claimed as Tailwind.</summary>
        public static bool IsTierMask(int mask)
        {
            for (int tier = 1; tier <= MaxTier; tier++) if (Mask(tier) == mask) return true;
            return false;
        }
    }
}
