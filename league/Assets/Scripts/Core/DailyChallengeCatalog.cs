using System;
using System.Collections.Generic;
using System.Globalization;

namespace BloomHarbor
{
    /// <summary>
    /// The daily challenge (owner direction 2026-09-09): one special level per day, seeded from the
    /// date, drawn from the campaign's own recipe pool at the coast the player has reached, and
    /// carrying that coast's collect goal wherever the coast has one. Rule v4 permanently retains
    /// the original twelve-stage coast pool; campaign expansion cannot change a saved recipe.
    ///
    /// It is deliberately not a new level generator. A challenge is the authored campaign stage -
    /// its blocker layout, its collect props, its move budget and its solver-calibrated score
    /// target - rebuilt under a seed derived from the UTC date. The pool per coast is six fixed
    /// stage identities established when the campaign held twelve stages per coast:
    ///
    /// * The opening lessons are never a challenge.
    /// * At a coast with a collect prop, only that coast's collect stages are eligible, which is
    ///   what makes "a collect goal when the world has one" true rather than occasional.
    /// * The same six stage positions repeat on the original six-day cycle.
    ///
    /// Arrival Cove uses stages 6..11 (zero-based); later coasts use 0,2,4,6,8,10. A broader
    /// challenge pool would require a new daily rule version; campaign expansion alone cannot
    /// alter this rule's date order, recipe validity, seed semantics or authored board.
    ///
    /// Identity travels in the same three save fields a Daily Tide uses, under rule version
    /// <see cref="RuleVersion"/>: the recipe id carries the coast and the stage, and the seed
    /// carries the date. A saved challenge therefore resumes, retries and validates through exactly
    /// the paths a tide does, and a save whose recipe id disagrees with its date is refused.
    /// </summary>
    public static class DailyChallengeCatalog
    {
        /// <summary>Rule version four is the daily challenge; one, two and three are the tide's.</summary>
        public const int RuleVersion = 4;
        /// <summary>The newest daily rules any save may carry. A save above this is from the future.</summary>
        public const int MaximumRuleVersion = RuleVersion;

        /// <summary>Stages in the pool at every coast. Six either way, so the week never repeats twice.</summary>
        public const int PoolSize = 6;
        /// <summary>Campaign levels 1..6 teach; a challenge is never one of them.</summary>
        public const int LessonLevels = 6;
        /// <summary>
        /// The recipe id packs the coast above the stage: coast * Stride + stage. The stride is far
        /// wider than any coast the campaign has, so a coast growing from twelve stages to sixty
        /// needs no change here and no saved challenge id ever changes meaning.
        /// </summary>
        public const int RecipeStride = 128;

        // The same Monday the tide's cycle is anchored to, so the two dailies advance together.
        private static readonly DateTime CycleAnchor = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The coast a player at <paramref name="reachedCoast"/> plays the challenge at.</summary>
        public static int CoastFor(int reachedCoast)
        { return reachedCoast < 1 ? 1 : reachedCoast > LevelCatalog.CoastCount ? LevelCatalog.CoastCount : reachedCoast; }

        /// <summary>The original stage extent carried by this immutable daily rule.</summary>
        public static int StagesPerCoast { get { return LevelCatalog.LegacyLevelsPerCoast; } }

        /// <summary>The fixed v4 stage positions at this coast, in their original order.</summary>
        public static List<int> EligibleStages(int coast)
        {
            if (coast < 1 || coast > LevelCatalog.CoastCount) throw new ArgumentOutOfRangeException("coast");
            var eligible = new List<int>();
            for (int slot = 0; slot < PoolSize; slot++)
                eligible.Add(coast == 1 ? LessonLevels + slot : slot * 2);
            return eligible;
        }

        /// <summary>The six campaign stages this coast draws its challenges from, in pool order.</summary>
        public static int[] Pool(int coast)
        {
            var eligible = EligibleStages(coast);
            // A coast with nothing past its lessons cannot offer a challenge at all. Say so rather
            // than returning six copies of stage zero, which would name a lesson as the challenge.
            if (eligible.Count == 0)
                throw new InvalidOperationException("Coast " + coast + " has no stage a daily challenge can be built from.");
            var pool = new int[PoolSize];
            for (int i = 0; i < PoolSize; i++)
                pool[i] = eligible[(int)((long)i * eligible.Count / PoolSize)];
            return pool;
        }

        /// <summary>Days since the shared cycle anchor, never negative.</summary>
        public static int DayIndex(DateTime utcDate)
        {
            long days = (UtcDay(utcDate).Ticks - CycleAnchor.Ticks) / TimeSpan.TicksPerDay;
            return (int)((days % PoolSize + PoolSize) % PoolSize);
        }

        /// <summary>The campaign stage this date's challenge is built from at this coast.</summary>
        public static int StageFor(DateTime utcDate, int coast) { return Pool(coast)[DayIndex(utcDate)]; }

        /// <summary>The campaign level id the challenge borrows its layout and target from.</summary>
        public static int SourceLevelId(int coast, int stage)
        {
            if (coast < 1 || coast > LevelCatalog.CoastCount) throw new ArgumentOutOfRangeException("coast");
            if (stage < 0 || stage >= StagesPerCoast) throw new ArgumentOutOfRangeException("stage");
            return (coast - 1) * LevelCatalog.LevelsPerCoast + stage + 1;
        }

        public static int RecipeIdFor(int coast, int stage)
        {
            if (coast < 1 || coast > LevelCatalog.CoastCount) throw new ArgumentOutOfRangeException("coast");
            // The stride is what the recipe id can carry, so a campaign whose coasts outgrow it must
            // widen the id rather than silently fold two stages onto one number.
            if (stage < 0 || stage >= RecipeStride) throw new ArgumentOutOfRangeException("stage",
                "The daily challenge recipe id carries stages 0.." + (RecipeStride - 1) + "; widen RecipeStride first.");
            return coast * RecipeStride + stage;
        }

        public static int CoastOf(int recipeId) { return recipeId / RecipeStride; }
        public static int StageOf(int recipeId) { return recipeId % RecipeStride; }

        /// <summary>True when this id names a coast and a stage that coast's pool actually offers.</summary>
        public static bool IsValidRecipeId(int recipeId)
        {
            int coast = CoastOf(recipeId), stage = StageOf(recipeId);
            if (coast < 1 || coast > LevelCatalog.CoastCount) return false;
            foreach (int candidate in Pool(coast)) if (candidate == stage) return true;
            return false;
        }

        /// <summary>The challenge seed for a date. Its own key, so it never collides with the tide's.</summary>
        public static uint GetSeed(DateTime utcDate)
        {
            DateTime day = UtcDay(utcDate);
            string key = "BloomHarbor/DailyChallenge/v4/" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            uint hash = 2166136261u;
            unchecked { foreach (char character in key) { hash ^= character; hash *= 16777619u; } }
            return hash == 0 ? 0xC0FFEE11u : hash;
        }

        /// <summary>Today's challenge for a player who has reached <paramref name="reachedCoast"/>.</summary>
        public static DailyTideChallenge Describe(DateTime utcDate, int reachedCoast)
        {
            DateTime day = UtcDay(utcDate);
            int coast = CoastFor(reachedCoast);
            int stage = StageFor(day, coast);
            uint seed = GetSeed(day);
            return new DailyTideChallenge
            {
                RuleVersion = RuleVersion,
                RecipeId = RecipeIdFor(coast, stage),
                Seed = seed,
                DateKey = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Level = CreateRecipe(RecipeIdFor(coast, stage), seed)
            };
        }

        /// <summary>The level a saved recipe id and seed name, without consulting a clock.</summary>
        public static LevelDefinition CreateRecipe(int recipeId, uint seed)
        {
            if (seed == 0) throw new ArgumentOutOfRangeException("seed", "A daily challenge seed must be nonzero.");
            if (!IsValidRecipeId(recipeId)) throw new ArgumentOutOfRangeException("recipeId", "Unknown daily challenge recipe.");
            int coast = CoastOf(recipeId), stage = StageOf(recipeId);
            LevelDefinition source = LevelCatalog.DailyChallengeSource(SourceLevelId(coast, stage));
            var level = new LevelDefinition
            {
                // Id zero is what marks a board as a daily rather than a campaign stop; the whole
                // daily restore path keys off it, and a challenge is a daily.
                Id = 0,
                Chapter = coast,
                Name = "Daily challenge",
                Seed = seed,
                Width = source.Width,
                Height = source.Height,
                ColorCount = source.ColorCount,
                Moves = source.Moves,
                BreezeEnabled = source.BreezeEnabled,
                InitialBreezeCharge = source.InitialBreezeCharge,
                TargetScore = source.TargetScore,
                Introduction = IntroductionFor(coast, source)
            };
            // The authored layout and the coast's collect props come across exactly as the campaign
            // stage holds them, so the challenge is the stage the player is living in rather than a
            // new difficulty nobody calibrated. Only the tile seed is the day's.
            foreach (var blocker in source.Blockers)
                level.Blockers.Add(new BlockerDefinition(blocker.X, blocker.Y, blocker.CrateHp, blocker.IceHp, blocker.Prop, blocker.Shell));
            foreach (var goal in source.Goals)
                if (goal != null) level.Goals.Add(new GoalDefinition(goal.Type, goal.Target, goal.Color, goal.Prop));
            if (source.InitialColors != null) level.InitialColors = (int[])source.InitialColors.Clone();
            return level;
        }

        /// <summary>The line the challenge board opens with: the coast's own words for its props.</summary>
        private static string IntroductionFor(int coast, LevelDefinition source)
        {
            if (source.CollectGoal != null && CollectPropCatalog.Has(coast))
            {
                var prop = CollectPropCatalog.Get(coast);
                return "Today's challenge. Reach the score and free every " + prop.Singular + ".";
            }
            return "Today's challenge. Reach the score before your moves run out.";
        }

        /// <summary>The collect goal today's challenge carries, or null at a coast without props.</summary>
        public static GoalDefinition CollectGoalFor(DateTime utcDate, int reachedCoast)
        { return Describe(utcDate, reachedCoast).Level.CollectGoal; }

        /// <summary>Every recipe id the catalog can ever produce, for a receipt or a sweep.</summary>
        public static IReadOnlyList<int> AllRecipeIds()
        {
            var ids = new List<int>();
            for (int coast = 1; coast <= LevelCatalog.CoastCount; coast++)
                foreach (int stage in Pool(coast)) ids.Add(RecipeIdFor(coast, stage));
            return ids;
        }

        private static DateTime UtcDay(DateTime date)
        {
            if (date.Kind == DateTimeKind.Local)
                throw new ArgumentException("Pass UTC or an explicitly UTC calendar date; core does not apply a machine time zone.", "date");
            return new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
