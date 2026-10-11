using System;

namespace BloomHarbor
{
    /// <summary>
    /// The session-open prompts, in priority order. <see cref="None"/> is the answer whenever the
    /// caps forbid one or nothing is true, and it is the common case by design.
    /// </summary>
    public enum GuidancePrompt
    {
        None = 0,
        /// <summary>A saved board exists. Resume before start, always.</summary>
        Resume = 1,
        Tide = 2,
        Gift = 3,
        NewCoast = 4,
        Companion = 5,
        Harbor = 6,
        Booster = 7,
        LivesFull = 8,
        Learn = 9,
    }

    /// <summary>
    /// A feature introducing itself, the first time the player can actually use it. Separate from
    /// <see cref="GuidancePrompt"/> because these retire permanently once seen, while a prompt is
    /// only ever deferred.
    /// </summary>
    public enum TeachingCard
    {
        Lives = 0, Stars = 1, Boosters = 2, DailyTide = 3, StampCard = 4,
        Gems = 5, Harbor = 6, Companions = 7, Abilities = 8, Fishing = 9,
    }

    /// <summary>
    /// What is true right now, as plain booleans.
    ///
    /// Every field is a state the player can verify on the screen behind the prompt. Rule 6 of the
    /// spec is the reason this is a struct of facts rather than a set of predicates: a prompt this
    /// layer cannot justify from an observed fact is a prompt it cannot show.
    /// </summary>
    public struct GuidanceSignals
    {
        public bool HasSavedBoard;
        public bool TideUnplayed;
        public bool GiftUnclaimed;
        public bool UnlockPending;
        public bool CompanionUnseen;
        /// <summary>Coins still needed for the next harbor project. <see cref="int.MaxValue"/> when
        /// there is no next project, which is why this is not a bool: "close" is a number.</summary>
        public int HarborCoinsAway;
        public bool BoosterCreditsUnspent;
        public bool LivesFull;
        public int HoursSinceLifeSpent;
        /// <summary>True when <see cref="SessionGuidance.NextTeaching"/> found an unseen card.</summary>
        public bool TeachingWaiting;
    }

    /// <summary>
    /// Which teaching cards are currently earned — the feature is reachable, so the card may be
    /// shown. Seen-ness is a separate mask, because "earned" stays true forever and "seen" happens
    /// once.
    /// </summary>
    public struct TeachingSignals
    {
        public bool LivesDropped;
        public bool ThreeStarWin;
        public bool CanAffordBooster;
        public bool TideReady;
        public bool SecondStamp;
        public bool HasGems;
        public bool ProjectAffordable;
        public bool CompanionUnlocked;
        public bool AbilityGranted;
        public bool FishingAvailable;

        public bool Earned(TeachingCard card)
        {
            switch (card)
            {
                case TeachingCard.Lives: return LivesDropped;
                case TeachingCard.Stars: return ThreeStarWin;
                case TeachingCard.Boosters: return CanAffordBooster;
                case TeachingCard.DailyTide: return TideReady;
                case TeachingCard.StampCard: return SecondStamp;
                case TeachingCard.Gems: return HasGems;
                case TeachingCard.Harbor: return ProjectAffordable;
                case TeachingCard.Companions: return CompanionUnlocked;
                case TeachingCard.Abilities: return AbilityGranted;
                case TeachingCard.Fishing: return FishingAvailable;
                default: return false;
            }
        }
    }

    /// <summary>
    /// Which prompt a session opens with, and when a feature introduces itself.
    ///
    /// Pure C# — no UnityEngine, no <c>PlayerData</c>, no Services reference — so the offline suite
    /// can drive thousands of session sequences against the real ladder and the real caps. The
    /// caller gathers the facts; this decides; <c>GuidanceLog</c> records. Nothing here reads a save
    /// and nothing here shows anything.
    ///
    /// **The caps are the feature.** The ladder is the easy part and the part everyone builds. What
    /// separates a nudge from nagging is rules 1–7 of design/specs/session-guidance.md: one prompt
    /// per open, never on the first session, three a day at most, and a prompt refused twice is
    /// retired for a week. Those are asserted here, not left to the screen to remember.
    /// </summary>
    public static class SessionGuidance
    {
        /// <summary>Bumped when the ladder or the caps change, so a recorded decision is never
        /// compared against a different set of rules.</summary>
        public const int RuleVersion = 1;

        /// <summary>Prompt values including <see cref="GuidancePrompt.None"/>, so an array indexed
        /// by prompt needs no offset arithmetic at any call site.</summary>
        public const int PromptCount = 10;
        public const int TeachingCount = 10;

        /// <summary>The first session is the tutorial; guidance starts on the second open.</summary>
        public const int FirstGuidedSession = 2;
        /// <summary>OWNER. However many sessions a day holds.</summary>
        public const int PromptsPerDay = 3;
        /// <summary>OWNER. Refusing twice is an answer.</summary>
        public const int DismissalsBeforeRetire = 2;
        /// <summary>OWNER. How long a refused prompt stays gone.</summary>
        public const int RetireDays = 7;
        /// <summary>OWNER. "Close" for the harbor prompt.</summary>
        public const int HarborCloseCoins = 60;
        /// <summary>OWNER. A full stock only reads as news after a day away.</summary>
        public const int LivesFullHours = 24;

        /// <summary>The ladder, top to bottom. Resume before start, free before paid, new before
        /// familiar, teaching last.</summary>
        public static readonly GuidancePrompt[] Ladder =
        {
            GuidancePrompt.Resume, GuidancePrompt.Tide, GuidancePrompt.Gift, GuidancePrompt.NewCoast,
            GuidancePrompt.Companion, GuidancePrompt.Harbor, GuidancePrompt.Booster,
            GuidancePrompt.LivesFull, GuidancePrompt.Learn,
        };

        /// <summary>Whether this prompt's own condition holds, ignoring every cap.</summary>
        public static bool Holds(GuidancePrompt prompt, GuidanceSignals signals)
        {
            switch (prompt)
            {
                case GuidancePrompt.Resume: return signals.HasSavedBoard;
                case GuidancePrompt.Tide: return signals.TideUnplayed;
                case GuidancePrompt.Gift: return signals.GiftUnclaimed;
                case GuidancePrompt.NewCoast: return signals.UnlockPending;
                case GuidancePrompt.Companion: return signals.CompanionUnseen;
                case GuidancePrompt.Harbor:
                    return signals.HarborCoinsAway > 0 && signals.HarborCoinsAway <= HarborCloseCoins;
                case GuidancePrompt.Booster: return signals.BoosterCreditsUnspent;
                case GuidancePrompt.LivesFull:
                    return signals.LivesFull && signals.HoursSinceLifeSpent >= LivesFullHours;
                case GuidancePrompt.Learn: return signals.TeachingWaiting;
                default: return false;
            }
        }

        /// <summary>
        /// The one prompt this session opens with, or <see cref="GuidancePrompt.None"/>.
        ///
        /// <paramref name="retiredUntilUtcTicks"/> is indexed by prompt and holds the instant each
        /// one becomes offerable again; zero means "never refused". Ticks rather than date keys so
        /// the comparison is a single integer and cannot be broken by a date format.
        /// </summary>
        public static GuidancePrompt Choose(GuidanceSignals signals, int sessionOrdinal, int shownToday,
            long[] retiredUntilUtcTicks, long nowUtcTicks)
        {
            // Rule 2: the first session is the tutorial. Rule 5: three a day, however many opens.
            if (sessionOrdinal < FirstGuidedSession) return GuidancePrompt.None;
            if (shownToday >= PromptsPerDay) return GuidancePrompt.None;
            foreach (var prompt in Ladder)
            {
                if (!Holds(prompt, signals)) continue;
                if (IsRetired(prompt, retiredUntilUtcTicks, nowUtcTicks)) continue;
                // Rule 1: the first one that survives wins, and the rest wait for another session.
                return prompt;
            }
            return GuidancePrompt.None;
        }

        public static bool IsRetired(GuidancePrompt prompt, long[] retiredUntilUtcTicks, long nowUtcTicks)
        {
            if (retiredUntilUtcTicks == null) return false;
            int index = (int)prompt;
            if (index < 0 || index >= retiredUntilUtcTicks.Length) return false;
            long until = retiredUntilUtcTicks[index];
            // A stamp in the future retires it. A backward clock cannot un-retire one, because the
            // comparison is against the stored instant rather than against an elapsed span.
            return until > 0 && nowUtcTicks < until;
        }

        /// <summary>The instant a prompt refused this many times becomes offerable again. Zero when
        /// it has not been refused enough to retire.</summary>
        public static long RetireUntil(int dismissals, long nowUtcTicks)
        {
            if (dismissals < DismissalsBeforeRetire) return 0;
            long span = TimeSpan.FromDays(RetireDays).Ticks;
            // Saturating rather than overflowing: a clock at the end of time retires it forever
            // rather than wrapping into the past and showing the prompt again immediately.
            return nowUtcTicks > long.MaxValue - span ? long.MaxValue : nowUtcTicks + span;
        }

        /// <summary>
        /// The next feature to introduce itself: the first card in order that the player has earned
        /// and has not seen. Order matters — it is the order features become reachable — and it is
        /// deliberately not the ladder above, which is about what to do rather than what to learn.
        /// </summary>
        public static bool NextTeaching(int seenMask, TeachingSignals earned, out TeachingCard card)
        {
            card = TeachingCard.Lives;
            for (int i = 0; i < TeachingCount; i++)
            {
                var candidate = (TeachingCard)i;
                if (HasSeen(seenMask, candidate)) continue;
                if (!earned.Earned(candidate)) continue;
                card = candidate; return true;
            }
            return false;
        }

        public static bool HasSeen(int seenMask, TeachingCard card)
        {
            int bit = 1 << (int)card;
            return (seenMask & bit) != 0;
        }

        public static int MarkSeen(int seenMask, TeachingCard card) { return seenMask | (1 << (int)card); }

        /// <summary>Every teaching bit set, for the suite and for the validator's range check.</summary>
        public static int AllTeachingSeen { get { return (1 << TeachingCount) - 1; } }

        /// <summary>
        /// The localization key for a prompt's line of copy, and for its button.
        ///
        /// Written out rather than composed from a slug: the catalogue audit finds keys by reading
        /// call sites, and a key assembled at runtime is a key no audit can see, which is how a
        /// locale file quietly accumulates strings nobody asks for any more. Keys from the first
        /// line, not after; retrofitting copy is the expensive mistake the rest of the game is
        /// still paying for.
        /// </summary>
        public static string CopyKey(GuidancePrompt prompt)
        {
            switch (prompt)
            {
                case GuidancePrompt.Resume: return Strings.Key("guidance.resume.copy");
                case GuidancePrompt.Tide: return Strings.Key("guidance.tide.copy");
                case GuidancePrompt.Gift: return Strings.Key("guidance.gift.copy");
                case GuidancePrompt.NewCoast: return Strings.Key("guidance.new_coast.copy");
                case GuidancePrompt.Companion: return Strings.Key("guidance.companion.copy");
                case GuidancePrompt.Harbor: return Strings.Key("guidance.harbor.copy");
                case GuidancePrompt.Booster: return Strings.Key("guidance.booster.copy");
                case GuidancePrompt.LivesFull: return Strings.Key("guidance.lives_full.copy");
                case GuidancePrompt.Learn: return Strings.Key("guidance.learn.copy");
                default: return "";
            }
        }

        public static string ActionKey(GuidancePrompt prompt)
        {
            switch (prompt)
            {
                case GuidancePrompt.Resume: return Strings.Key("guidance.resume.action");
                case GuidancePrompt.Tide: return Strings.Key("guidance.tide.action");
                case GuidancePrompt.Gift: return Strings.Key("guidance.gift.action");
                case GuidancePrompt.NewCoast: return Strings.Key("guidance.new_coast.action");
                case GuidancePrompt.Companion: return Strings.Key("guidance.companion.action");
                case GuidancePrompt.Harbor: return Strings.Key("guidance.harbor.action");
                case GuidancePrompt.Booster: return Strings.Key("guidance.booster.action");
                case GuidancePrompt.LivesFull: return Strings.Key("guidance.lives_full.action");
                case GuidancePrompt.Learn: return Strings.Key("guidance.learn.action");
                default: return "";
            }
        }

        /// <summary>
        /// A teaching card's heading and its one sentence. Written out for the same reason the
        /// prompt keys are: the catalogue audit reads call sites, and a key assembled from a slug is
        /// a key no audit can see.
        /// </summary>
        public static string TeachingTitleKey(TeachingCard card)
        {
            switch (card)
            {
                case TeachingCard.Lives: return Strings.Key("teaching.lives.title");
                case TeachingCard.Stars: return Strings.Key("teaching.stars.title");
                case TeachingCard.Boosters: return Strings.Key("teaching.boosters.title");
                case TeachingCard.DailyTide: return Strings.Key("teaching.daily_tide.title");
                case TeachingCard.StampCard: return Strings.Key("teaching.stamp_card.title");
                case TeachingCard.Gems: return Strings.Key("teaching.gems.title");
                case TeachingCard.Harbor: return Strings.Key("teaching.harbor.title");
                case TeachingCard.Companions: return Strings.Key("teaching.companions.title");
                case TeachingCard.Abilities: return Strings.Key("teaching.abilities.title");
                default: return Strings.Key("teaching.fishing.title");
            }
        }

        public static string TeachingCopyKey(TeachingCard card)
        {
            switch (card)
            {
                case TeachingCard.Lives: return Strings.Key("teaching.lives.copy");
                case TeachingCard.Stars: return Strings.Key("teaching.stars.copy");
                case TeachingCard.Boosters: return Strings.Key("teaching.boosters.copy");
                case TeachingCard.DailyTide: return Strings.Key("teaching.daily_tide.copy");
                case TeachingCard.StampCard: return Strings.Key("teaching.stamp_card.copy");
                case TeachingCard.Gems: return Strings.Key("teaching.gems.copy");
                case TeachingCard.Harbor: return Strings.Key("teaching.harbor.copy");
                case TeachingCard.Companions: return Strings.Key("teaching.companions.copy");
                case TeachingCard.Abilities: return Strings.Key("teaching.abilities.copy");
                default: return Strings.Key("teaching.fishing.copy");
            }
        }

        /// <summary>True when this prompt's copy takes an argument, so a caller knows to Format it.</summary>
        public static bool CopyTakesArgument(GuidancePrompt prompt) { return prompt == GuidancePrompt.Harbor; }

        public static string Slug(GuidancePrompt prompt)
        {
            switch (prompt)
            {
                case GuidancePrompt.Resume: return "resume";
                case GuidancePrompt.Tide: return "tide";
                case GuidancePrompt.Gift: return "gift";
                case GuidancePrompt.NewCoast: return "new_coast";
                case GuidancePrompt.Companion: return "companion";
                case GuidancePrompt.Harbor: return "harbor";
                case GuidancePrompt.Booster: return "booster";
                case GuidancePrompt.LivesFull: return "lives_full";
                case GuidancePrompt.Learn: return "learn";
                default: return "none";
            }
        }

        public static string Slug(TeachingCard card)
        {
            switch (card)
            {
                case TeachingCard.Lives: return "lives";
                case TeachingCard.Stars: return "stars";
                case TeachingCard.Boosters: return "boosters";
                case TeachingCard.DailyTide: return "daily_tide";
                case TeachingCard.StampCard: return "stamp_card";
                case TeachingCard.Gems: return "gems";
                case TeachingCard.Harbor: return "harbor";
                case TeachingCard.Companions: return "companions";
                case TeachingCard.Abilities: return "abilities";
                default: return "fishing";
            }
        }
    }
}
