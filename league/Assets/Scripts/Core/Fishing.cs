using System;

namespace BloomHarbor
{
    /// <summary>How big the fish is. Size is the amount a catch pays.</summary>
    public enum FishTier { Tiddler = 0, Common = 1, Prize = 2, Rare = 3, Legendary = 4 }

    /// <summary>
    /// Which fish. Species is the reward family, so a player learns the mapping in a few casts
    /// without reading a word — which is also what makes the screen work in a language the game has
    /// not been translated into yet.
    ///
    /// Three, not five: that is what the owner-selected art supplies, and three meanings are what a
    /// player can actually hold. Five near-identical silhouettes in different colours is how a
    /// collection screen becomes wallpaper.
    /// </summary>
    public enum FishSpecies { CoralMinnow = 0, StripedTeal = 1, GoldenFish = 2 }

    /// <summary>
    /// How well the fight went, and the only thing the simulation tells the ledger. It can move a
    /// catch down the existing payout table and never up: skill decides how much a cast pays, never
    /// whether it pays, and fishing may not become a coin faucet.
    /// </summary>
    public enum FishingGrade
    {
        /// <summary>Landed with an early hook, no overload and no slack, inside the target time.</summary>
        Perfect = 0,
        /// <summary>Landed.</summary>
        Clean = 1,
        /// <summary>Landed after a mistake. One size smaller.</summary>
        Slipped = 2,
        /// <summary>It got away. Two sizes smaller, floored at the smallest fish.</summary>
        Lost = 3
    }

    /// <summary>One catch: what was landed, before any reward is applied.</summary>
    public struct FishCatch
    {
        public FishTier Tier;
        public FishSpecies Species;
    }

    /// <summary>What a catch pays. Applied through the existing grant paths, never here.</summary>
    public struct FishingReward
    {
        public int Coins;
        public int Gems;
        public int BoosterCredits;
        public BoosterType Booster;
        public int Lives;
        public bool Any { get { return Coins > 0 || Gems > 0 || BoosterCredits > 0 || Lives > 0; } }
    }

    /// <summary>
    /// The fishing numbers a run plays by (D-FISH-13, fishing WP5): what <see cref="Fishing.Tune"/>
    /// takes. Plain numbers, so Core never reads Services; Services/Fishing/FishingTuning builds one
    /// from the remote config at boot.
    /// </summary>
    public struct FishingTuningValues
    {
        public int TicketCap, TicketRegenMinutes, TicketBankCap, DailyLoginTickets, FirstCampaignWinTickets;
        public int RewardedAdTickets, RewardedAdTicketsPerDay, DoubleDailyCap, CoinTicketPrice;
        public int[] GemLadder;
    }

    /// <summary>
    /// The fishing mini-game's rules: what is caught, what it pays, and what a cast costs.
    ///
    /// Pure C# with no UnityEngine reference, no <c>PlayerData</c> and no Services dependency, so
    /// the offline suite can run hundreds of thousands of seeded casts against the real table. The
    /// presentation layer cannot pay a reward even if it wanted to.
    ///
    /// **A cast costs a ticket, not coins.** The first draft priced casts in coins and then had to
    /// price around its own danger — a mini-game paying boosters and lives for coins is a machine
    /// that converts coins into store inventory. Tickets remove the arbitrage instead of pricing it:
    /// coins are no longer the door, so a large coin pile cannot be turned into boosters and lives,
    /// and the faucet is bounded by how fast tickets arrive rather than by a player's balance.
    ///
    /// Every number is provisional and marked OWNER in design/specs/fishing-minigame.md. The suite
    /// asserts the *relationships*, so retuning cannot silently invert the economics.
    /// </summary>
    public static class Fishing
    {
        /// <summary>Bumped when the table or the seeding changes, so a recorded cast is never
        /// replayed against different rules than the ones that decided it.</summary>
        public const int RuleVersion = 3; // 3: the per-install seed salt (D-FISH-8, fishing WP4)

        // --- Tickets -----------------------------------------------------------------------------
        //
        // D-FISH-13 (fishing WP5, 2026-10-10): every number below is a remote knob (the fish1_* keys),
        // fed once at boot by Services/Fishing/FishingTuning through Tune, inside the compiled rails
        // beside it. A value outside a rail is clamped, never refused. Applied only at boot, so the
        // price a rung prints is the price its tap charges. Core never reads Services: the tuning is
        // handed in as plain numbers.

        /// <summary>Tickets a player can hold. The cap is what makes regeneration an appointment
        /// rather than a pile that accrues while nobody plays. Remote fish1_ticket_cap, 3-10.</summary>
        public static int TicketCap { get; private set; } = DefaultTicketCap;
        public const int DefaultTicketCap = 5, MinimumTicketCap = 3, MaximumTicketCap = 10;

        /// <summary>One ticket every half hour by default: a full stock in two and a half hours, which
        /// is a lunchtime and an evening rather than a whole day. Remote fish1_regen_minutes, 10-120.</summary>
        public static int TicketRegenMinutes { get; private set; } = DefaultTicketRegenMinutes;
        public const int DefaultTicketRegenMinutes = 30, MinimumTicketRegenMinutes = 10, MaximumTicketRegenMinutes = 120;

        /// <summary>
        /// Granted tickets the bank holds above the regenerating stock (D-FISH-5, fishing WP4). The
        /// stock still regenerates only to <see cref="TicketCap"/> -- that is the appointment -- but a
        /// ticket somebody was given (login, first win, a chest, an ad, gems, coins, the album) is
        /// never thrown away because the stock happened to be full. Remote fish1_bank_cap, 5-999.
        /// </summary>
        public static int TicketBankCap { get; private set; } = DefaultTicketBankCap;
        public const int DefaultTicketBankCap = 99, MinimumTicketBankCap = 5, MaximumTicketBankCap = 999;
        /// <summary>
        /// The bank's hard bound (WP5): the widest the remote bank cap can ever be. Tickets bought for
        /// real money bank past the live cap up to here and are never truncated; a save is validated
        /// against this, so lowering the remote cap never makes a saved bank unreadable.
        /// </summary>
        public const int TicketBankHardCap = MaximumTicketBankCap;

        public const int TicketsPerCast = 1;
        /// <summary>The daily login gift's tickets. Remote fish1_login_tickets, 0-10.</summary>
        public static int DailyLoginTickets { get; private set; } = DefaultDailyLoginTickets;
        public const int DefaultDailyLoginTickets = 2, MaximumDailyLoginTickets = 10;
        /// <summary>The day's first campaign win's tickets. Remote fish1_first_win_tickets, 0-5.</summary>
        public static int FirstCampaignWinTickets { get; private set; } = DefaultFirstCampaignWinTickets;
        public const int DefaultFirstCampaignWinTickets = 1, MaximumFirstCampaignWinTickets = 5;
        /// <summary>Tickets one rewarded watch pays. Remote fish1_ad_tickets, 1-5.</summary>
        public static int RewardedAdTickets { get; private set; } = DefaultRewardedAdTickets;
        public const int DefaultRewardedAdTickets = 1, MinimumRewardedAdTickets = 1, MaximumRewardedAdTickets = 5;
        /// <summary>Rewarded ticket watches a UTC day (D-FISH-13: 3 became 10). Remote fish1_ad_daily_cap, 0-50.</summary>
        public static int RewardedAdTicketsPerDay { get; private set; } = DefaultRewardedAdTicketsPerDay;
        public const int DefaultRewardedAdTicketsPerDay = 10, MaximumRewardedAdTicketsPerDay = 50;
        /// <summary>The catch plate's "2x" watches a UTC day (D-FISH-12). Remote fish1_double_daily_cap, 0-50.</summary>
        public static int DoubleDailyCap { get; private set; } = DefaultDoubleDailyCap;
        public const int DefaultDoubleDailyCap = 20, MaximumDoubleDailyCap = 50;

        /// <summary>Gems buy a handful at a time, cheaper per unit than a life refill, because this
        /// is engagement rather than progression.</summary>
        public const int GemTicketBundle = 5;
        /// <summary>
        /// The five-pack's daily price ladder (WP5): the first pack of a UTC day costs the first rung,
        /// the second the second, and every pack past the last rung costs the last. Remote
        /// fish1_gem_ladder, each rung 10-500, a later rung never cheaper than an earlier one.
        /// </summary>
        public static readonly int[] DefaultGemLadder = { 25, 40, 60, 90 };
        public const int MinimumGemLadderPrice = 10, MaximumGemLadderPrice = 500, MaximumGemLadderRungs = 8;
        static int[] gemLadder = (int[])DefaultGemLadder.Clone();
        /// <summary>The ladder's first rung: what the day's first five-pack costs.</summary>
        public static int GemTicketBundlePrice { get { return gemLadder[0]; } }
        /// <summary>The five-pack's price after <paramref name="boughtToday"/> packs today.</summary>
        public static int GemTicketBundlePriceAt(int boughtToday)
        {
            if (boughtToday < 0) boughtToday = 0;
            return gemLadder[boughtToday < gemLadder.Length ? boughtToday : gemLadder.Length - 1];
        }
        /// <summary>The live ladder (a copy).</summary>
        public static int[] GemLadder() { return (int[])gemLadder.Clone(); }
        /// <summary>The ladder's dearest rung, for a box measured against the widest price.</summary>
        public static int GemTicketBundleTopPrice { get { return gemLadder[gemLadder.Length - 1]; } }

        /// <summary>
        /// Coins buy a single ticket at **deliberately poor value**. It exists so an impatient
        /// player is never hard-blocked, and it is priced above what a cast returns so that farming
        /// coins through fishing is a loss. Remote fish1_coin_ticket, 140-1000.
        ///
        /// The rail's floor is what holds that at any remote value (fishing WP5 review, 2026-10-10;
        /// the floor was 50): a cast returns about 63.4 coins of value, and the catch plate's 2x chip
        /// pays a landed cast's reward again, so an ad watcher's cast returns about 126.9. 140 sits
        /// above twice the return with a tenth to spare. The suite asserts the floor against twice
        /// the table's exact expected value, not only the default against it.
        /// </summary>
        public static int CoinTicketPrice { get; private set; } = DefaultCoinTicketPrice;
        public const int DefaultCoinTicketPrice = 150, MinimumCoinTicketPrice = 140, MaximumCoinTicketPrice = 1000;

        /// <summary>
        /// Installs this run's fishing numbers (D-FISH-13). Every value is held inside its rail here
        /// as well, whatever produced it; a ladder that is missing, empty or too long is the default.
        /// Called once, at boot, before any screen reads a number.
        /// </summary>
        public static void Tune(FishingTuningValues values)
        {
            TicketCap = Clamp(values.TicketCap, MinimumTicketCap, MaximumTicketCap);
            TicketRegenMinutes = Clamp(values.TicketRegenMinutes, MinimumTicketRegenMinutes, MaximumTicketRegenMinutes);
            TicketBankCap = Clamp(values.TicketBankCap, MinimumTicketBankCap, MaximumTicketBankCap);
            DailyLoginTickets = Clamp(values.DailyLoginTickets, 0, MaximumDailyLoginTickets);
            FirstCampaignWinTickets = Clamp(values.FirstCampaignWinTickets, 0, MaximumFirstCampaignWinTickets);
            RewardedAdTickets = Clamp(values.RewardedAdTickets, MinimumRewardedAdTickets, MaximumRewardedAdTickets);
            RewardedAdTicketsPerDay = Clamp(values.RewardedAdTicketsPerDay, 0, MaximumRewardedAdTicketsPerDay);
            DoubleDailyCap = Clamp(values.DoubleDailyCap, 0, MaximumDoubleDailyCap);
            CoinTicketPrice = Clamp(values.CoinTicketPrice, MinimumCoinTicketPrice, MaximumCoinTicketPrice);
            gemLadder = LadderWithinRails(values.GemLadder);
        }

        /// <summary>The compiled defaults, as <see cref="Tune"/> takes them.</summary>
        public static FishingTuningValues DefaultTuning()
        {
            return new FishingTuningValues
            {
                TicketCap = DefaultTicketCap, TicketRegenMinutes = DefaultTicketRegenMinutes, TicketBankCap = DefaultTicketBankCap,
                DailyLoginTickets = DefaultDailyLoginTickets, FirstCampaignWinTickets = DefaultFirstCampaignWinTickets,
                RewardedAdTickets = DefaultRewardedAdTickets, RewardedAdTicketsPerDay = DefaultRewardedAdTicketsPerDay,
                DoubleDailyCap = DefaultDoubleDailyCap, CoinTicketPrice = DefaultCoinTicketPrice, GemLadder = (int[])DefaultGemLadder.Clone()
            };
        }

        /// <summary>Puts every number back to the compiled default (a fresh run, a suite between cases).</summary>
        public static void ResetTuning() { Tune(DefaultTuning()); }

        /// <summary>A ladder held inside the rails: 1-8 rungs, each 10-500, never cheaper than the rung before.</summary>
        public static int[] LadderWithinRails(int[] ladder)
        {
            if (ladder == null || ladder.Length == 0 || ladder.Length > MaximumGemLadderRungs) return (int[])DefaultGemLadder.Clone();
            var rungs = new int[ladder.Length];
            for (int i = 0; i < rungs.Length; i++)
            {
                rungs[i] = Clamp(ladder[i], MinimumGemLadderPrice, MaximumGemLadderPrice);
                if (i > 0 && rungs[i] < rungs[i - 1]) rungs[i] = rungs[i - 1];
            }
            return rungs;
        }

        static int Clamp(int value, int minimum, int maximum) { return value < minimum ? minimum : value > maximum ? maximum : value; }

        // --- The table ----------------------------------------------------------------------------

        /// <summary>Tier shares in per-mille, smallest first. Must total 1000.</summary>
        static readonly int[] TierShare = { 340, 340, 200, 100, 20 };

        /// <summary>
        /// Species shares in per-mille, by size. The coral minnow dominates the small sizes because
        /// coins are the reward that cannot be capped, refused or wasted; the golden fish
        /// concentrates in the sizes a player rarely reaches.
        /// </summary>
        static readonly int[][] SpeciesShare =
        {
            new[] { 1000,   0,   0 }, // Tiddler: coins only
            new[] {  720, 280,   0 }, // Common
            new[] {  480, 380, 140 }, // Prize
            new[] {  340, 380, 280 }, // Rare
            new[] {  240, 360, 400 }, // Legendary
        };

        static readonly int[] CoinPay = { 15, 30, 60, 120, 250 };
        static readonly int[] BoosterPay = { 1, 1, 1, 2, 3 };
        static readonly int[] GemPay = { 3, 5, 8, 12, 20 };
        /// <summary>The golden fish pays a life only at the top two sizes; below that it pays gems.</summary>
        static readonly int[] LifePay = { 0, 0, 0, 1, 2 };

        public static int TierCount { get { return TierShare.Length; } }
        public static int SpeciesCount { get { return SpeciesShare[0].Length; } }

        /// <summary>Every species at every size: the collection album's slots.</summary>
        public static int CollectionSlots { get { return TierCount * SpeciesCount; } }

        /// <summary>The album slot a catch fills. Stable, so a saved album survives a table retune.</summary>
        public static int CollectionSlot(FishCatch landed)
        {
            return (int)landed.Species * TierCount + (int)landed.Tier;
        }

        // --- Ticket arithmetic ----------------------------------------------------------------------

        /// <summary>
        /// The stock after regeneration, and the stamp the caller should keep.
        ///
        /// A backward clock — a device whose time was changed, or a restored save — must never grant
        /// tickets, so a negative elapsed span regenerates nothing. The remainder of a part-served
        /// interval is preserved, so a player never loses progress toward the next ticket by opening
        /// the app at an unlucky moment. This is the discipline the lives stock already uses.
        /// </summary>
        public static int Regenerate(int current, long lastUtcTicks, long nowUtcTicks, out long stampUtcTicks)
        {
            current = current < 0 ? 0 : current;
            stampUtcTicks = nowUtcTicks;
            if (current >= TicketCap) return current;
            if (lastUtcTicks <= 0 || nowUtcTicks <= lastUtcTicks) return current;
            long interval = TimeSpan.TicksPerMinute * TicketRegenMinutes;
            long earned = (nowUtcTicks - lastUtcTicks) / interval;
            if (earned <= 0) { stampUtcTicks = lastUtcTicks; return current; }
            int granted = (int)Math.Min(earned, TicketCap - current);
            int next = current + granted;
            stampUtcTicks = next >= TicketCap ? nowUtcTicks : lastUtcTicks + granted * interval;
            return next;
        }

        /// <summary>Seconds until the next ticket, or 0 when the stock is full or already due.</summary>
        public static int SecondsUntilNextTicket(int current, long lastUtcTicks, long nowUtcTicks)
        {
            if (current >= TicketCap || lastUtcTicks <= 0 || nowUtcTicks < lastUtcTicks) return 0;
            long due = lastUtcTicks + TimeSpan.TicksPerMinute * TicketRegenMinutes - nowUtcTicks;
            return due <= 0 ? 0 : (int)(due / TimeSpan.TicksPerSecond);
        }

        public static bool CanCast(int tickets) { return tickets >= TicketsPerCast; }

        // --- The catch ------------------------------------------------------------------------------

        /// <summary>
        /// The seed for one cast. Derived from the install's own salt, the date and the cast index so
        /// the outcome is fixed before the player sees it -- the cast is recorded durably first, and
        /// quitting on a catch cannot reroll it -- and so two installs do not share one published
        /// sequence of catches (D-FISH-8, fishing WP4).
        /// </summary>
        public static uint SeedFor(long salt, string dateKey, int castIndex)
        {
            unchecked
            {
                uint hash = 2166136261u;
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                string key = salt.ToString(invariant) + "|" + (dateKey ?? "") + "|" + castIndex.ToString(invariant)
                    + "|" + RuleVersion.ToString(invariant);
                for (int i = 0; i < key.Length; i++) { hash ^= key[i]; hash *= 16777619u; }
                // One avalanche pass, so neighbouring cast indices do not produce neighbouring rolls.
                hash ^= hash >> 16; hash *= 2246822507u; hash ^= hash >> 13; hash *= 3266489909u; hash ^= hash >> 16;
                return hash;
            }
        }

        /// <summary>The seed for a salt of zero. Kept for the simulations and suites that need a
        /// sequence without a saved document; the game always seeds through the document's salt.</summary>
        public static uint SeedFor(string dateKey, int castIndex) { return SeedFor(0L, dateKey, castIndex); }

        /// <summary>The catch a seed decides. Deterministic and side-effect free.</summary>
        public static FishCatch Resolve(uint seed)
        {
            int tier = Pick(TierShare, (int)(seed % 1000u));
            int species = Pick(SpeciesShare[tier], (int)((seed / 1000u) % 1000u));
            return new FishCatch { Tier = (FishTier)tier, Species = (FishSpecies)species };
        }

        static int Pick(int[] shares, int roll)
        {
            int running = 0, last = 0;
            for (int i = 0; i < shares.Length; i++)
            {
                if (shares[i] > 0) last = i;
                running += shares[i];
                if (roll < running) return i;
            }
            return last;
        }

        /// <summary>
        /// A fumbled reel drops one size, never below the smallest fish.
        ///
        /// This is the rule that keeps a cast honest: a cast that can pay nothing is the fastest way
        /// to make a currency sink feel like a confidence trick, so skill decides how much a cast
        /// pays and never whether it pays.
        /// </summary>
        public static FishCatch Fumble(FishCatch landed)
        {
            if (landed.Tier > FishTier.Tiddler) landed.Tier = (FishTier)((int)landed.Tier - 1);
            // The species table thins toward the small sizes; keep the fish legal for its new size.
            if (SpeciesShare[(int)landed.Tier][(int)landed.Species] == 0) landed.Species = FishSpecies.CoralMinnow;
            return landed;
        }

        /// <summary>What a catch pays. Nothing here touches a wallet.</summary>
        public static FishingReward Reward(FishCatch landed)
        {
            int tier = (int)landed.Tier;
            var reward = new FishingReward();
            switch (landed.Species)
            {
                case FishSpecies.CoralMinnow:
                    reward.Coins = CoinPay[tier];
                    break;
                case FishSpecies.StripedTeal:
                    reward.BoosterCredits = BoosterPay[tier];
                    // Rotates with the size so a player collects a spread rather than three of one.
                    reward.Booster = (BoosterType)(tier % BoosterCatalog.Count);
                    break;
                case FishSpecies.GoldenFish:
                    if (LifePay[tier] > 0) reward.Lives = LifePay[tier];
                    else reward.Gems = GemPay[tier];
                    break;
            }
            // A family that somehow pays nothing at this size still pays the size's coins, so no
            // catch is ever empty. The table keeps that from happening; this holds if it changes.
            if (!reward.Any) reward.Coins = CoinPay[tier];
            return reward;
        }

        /// <summary>
        /// A reward valued in coins, so a cast's worth can be compared with what a ticket costs.
        ///
        /// The conversion rates are **passed in** rather than read here: Core does not reference
        /// Services, and the gem exchange rate and the life price both live there. Building it the
        /// other way compiled against the suites that include Services and broke the two that
        /// deliberately compile Core alone — which is the boundary those suites exist to defend.
        /// </summary>
        public static int CoinValue(FishingReward reward, int coinsPerGem, int coinsPerLife)
        {
            int value = reward.Coins;
            value += reward.Gems * Math.Max(0, coinsPerGem);
            if (reward.BoosterCredits > 0) value += reward.BoosterCredits * BoosterCatalog.Price(reward.Booster);
            value += reward.Lives * Math.Max(0, coinsPerLife);
            return value;
        }

        /// <summary>
        /// The gems that stand in for lives a catch earned but the stock could not hold (D-FISH-11,
        /// fishing WP2, 2026-10-09). A golden fish caught at full lives used to pay nothing at all,
        /// which is the one catch in the table that could be worth less than a Tiny minnow. Each
        /// refused life pays the size's gem price divided by its life price: 12 gems for a Rare life,
        /// 10 for each Legendary one. A size that pays no lives converts nothing.
        /// </summary>
        public static int GemsForRefusedLives(FishTier tier, int refused)
        {
            int t = (int)tier;
            if (refused <= 0 || t < 0 || t >= LifePay.Length || LifePay[t] <= 0) return 0;
            return refused * GemPay[t] / LifePay[t];
        }

        /// <summary>A size's share of every cast, in per-mille. Read-only, for the odds sheet.</summary>
        public static int TierPerMille(FishTier tier)
        {
            int t = (int)tier;
            return t < 0 || t >= TierShare.Length ? 0 : TierShare[t];
        }

        /// <summary>Total tier share, for the suite to assert the table is whole.</summary>
        public static int TierShareTotal()
        {
            int total = 0;
            for (int i = 0; i < TierShare.Length; i++) total += TierShare[i];
            return total;
        }

        /// <summary>
        /// The per-mille share a reward family holds at one size. Zero means the table never pays
        /// that family there -- the roster reads this so a named fish can never be allowed at a size
        /// its family cannot be priced at.
        /// </summary>
        public static int FamilyShare(FishTier tier, FishSpecies family)
        {
            int t = (int)tier, f = (int)family;
            if (t < 0 || t >= SpeciesShare.Length) return 0;
            var row = SpeciesShare[t];
            return f < 0 || f >= row.Length ? 0 : row[f];
        }

        /// <summary>Total species share for a size, for the same reason.</summary>
        public static int SpeciesShareTotal(FishTier tier)
        {
            int total = 0, index = (int)tier;
            for (int i = 0; i < SpeciesShare[index].Length; i++) total += SpeciesShare[index][i];
            return total;
        }
    }
}
