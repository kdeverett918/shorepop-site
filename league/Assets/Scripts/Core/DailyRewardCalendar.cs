using System;

namespace BloomHarbor
{
    /// <summary>What one day of the daily-challenge calendar pays.</summary>
    public enum DailyRewardKind { Coins, Booster, Gems, Chest }

    /// <summary>One day of the seven-day calendar. Plain data: no clock, no player, no Unity.</summary>
    [Serializable]
    public sealed class DailyRewardEntry
    {
        /// <summary>1..7. The day's place on the strip, not a calendar date.</summary>
        public int Day;
        public DailyRewardKind Kind;
        /// <summary>Coins this day pays. A gem day pays its coin stand-in here until gems exist.</summary>
        public int Coins;
        /// <summary>Gems this day pays once commerce-r1 adds a gem wallet. Zero until then.</summary>
        public int Gems;
        /// <summary>The booster a Booster or Chest day grants one free use of.</summary>
        public BoosterType Booster;
        public bool GrantsBooster;
        /// <summary>True on the day the mascot comes out for the chest.</summary>
        public bool MascotCameo;
        /// <summary>The badge word on the card and the strip. Never abbreviated further.</summary>
        public string Badge;
        /// <summary>The sentence the card reads under the badge.</summary>
        public string Copy;
    }

    /// <summary>
    /// The seven-day daily-challenge reward calendar (owner direction 2026-09-09: "a daily
    /// challenge each day for rewards like you would get in Toy Blast").
    ///
    /// The strip is a ladder, not a streak. A day is stamped only by clearing that day's challenge,
    /// stamps are never removed, and a day nobody played simply leaves the strip where it was: the
    /// player resumes on the same rung tomorrow. Nothing in this file knows what a missed day is,
    /// which is the point - there is no penalty to express.
    ///
    /// The escalation is the owner's: coins early, a booster on day three, gems on day five, and a
    /// large chest with the mascot on day seven. Day five pays gems now that commerce-r1's gem
    /// wallet exists; <see cref="GemsAvailable"/> is the one switch that turns it back into
    /// <see cref="GemFallbackCoins"/> coins, so the rung can never pay nothing at all. One table,
    /// no second source of truth: the card, the strip, the reveal and the receipts all read it.
    /// </summary>
    public static class DailyRewardCalendar
    {
        public const int Days = 7;

        /// <summary>
        /// False only while the save has nowhere to put a gem. PlayerData.gems landed with
        /// commerce-r1 on 2026-09-09, so day five pays gems; leave the flag and the fallback in
        /// place, because a rung that silently paid nothing would be the one failure a player
        /// could not see.
        /// </summary>
        public const bool GemsAvailable = true;
        /// <summary>
        /// Gems day five pays, instead of coins rather than beside them: one headline reward reads
        /// better on the card, and it keeps the free faucet out of the coin economy the campaign
        /// band models. Fifteen is commerce-r1's number (2026-09-09), a quarter of a life refill,
        /// so a daily player feels gems accumulate without the store losing its reason to exist.
        /// </summary>
        public const int GemGrant = 15;
        /// <summary>Coins day five pays instead if there is ever nowhere to put a gem.</summary>
        public const int GemFallbackCoins = 200;

        // The ladder, in coins. Anchored to the booster prices (40/60/90) and to campaign income
        // (a first clear pays 25 and each new star 10): a full week of challenges is worth about
        // eleven three-star campaign clears, which is a real reason to come back without making
        // the campaign's own coins pointless. Day seven is the week's payload on purpose.
        private static readonly int[] CoinLadder = { 75, 100, 0, 150, 0, 0, 400 };
        private static readonly int[] ChestBoosterDay = { 3, 6, 7 };

        /// <summary>The coins a chest day pays on top of its booster.</summary>
        public const int ChestCoins = 400;

        /// <summary>The whole week's coins, for the economy note and for a test that adds them up.</summary>
        public static int WeekCoins
        {
            get { int total = 0; for (int day = 1; day <= Days; day++) total += Day(day).Coins; return total; }
        }

        /// <summary>The reward on rung <paramref name="day"/> (1..7).</summary>
        public static DailyRewardEntry Day(int day)
        {
            if (day < 1 || day > Days) throw new ArgumentOutOfRangeException("day", "The calendar has seven days.");
            var entry = new DailyRewardEntry { Day = day, Kind = DailyRewardKind.Coins, Coins = CoinLadder[day - 1] };
            if (day == 3)
            {
                entry.Kind = DailyRewardKind.Booster; entry.GrantsBooster = true; entry.Booster = BoosterType.ExtraMoves;
                entry.Badge = Strings.Get(Strings.Key("reward.badge_booster"));
                entry.Copy = Strings.Format("reward.copy_booster", BoosterCatalog.Name(BoosterType.ExtraMoves));
                return entry;
            }
            if (day == 5)
            {
                entry.Kind = DailyRewardKind.Gems;
                entry.Gems = GemsAvailable ? GemGrant : 0;
                entry.Coins = GemsAvailable ? 0 : GemFallbackCoins;
                entry.Badge = GemsAvailable
                    ? Strings.Format("reward.badge_gems", GemGrant)
                    : Strings.Format("reward.badge_coins", GemFallbackCoins);
                entry.Copy = GemsAvailable
                    ? Strings.Format("reward.copy_gems", GemGrant)
                    : Strings.Format("reward.copy_gems_pending", GemFallbackCoins);
                return entry;
            }
            if (day == 6)
            {
                entry.Kind = DailyRewardKind.Booster; entry.GrantsBooster = true; entry.Booster = BoosterType.StartRocket;
                entry.Badge = Strings.Get(Strings.Key("reward.badge_booster"));
                entry.Copy = Strings.Format("reward.copy_booster", BoosterCatalog.Name(BoosterType.StartRocket));
                return entry;
            }
            if (day == Days)
            {
                entry.Kind = DailyRewardKind.Chest; entry.GrantsBooster = true; entry.Booster = BoosterType.StartBomb;
                entry.MascotCameo = true; entry.Coins = ChestCoins;
                entry.Badge = Strings.Get(Strings.Key("reward.badge_chest"));
                entry.Copy = Strings.Format("reward.copy_chest", ChestCoins, BoosterCatalog.Name(BoosterType.StartBomb));
                return entry;
            }
            entry.Badge = Strings.Format("reward.badge_coins", entry.Coins);
            entry.Copy = Strings.Format("reward.copy_coins", entry.Coins);
            return entry;
        }

        /// <summary>The short word the seven-day strip prints under each rung.</summary>
        public static string StripMark(int day)
        {
            var entry = Day(day);
            if (entry.Kind == DailyRewardKind.Chest) return Strings.Get(Strings.Key("reward.mark_chest"));
            if (entry.Kind == DailyRewardKind.Booster) return Strings.Get(Strings.Key("reward.mark_booster"));
            if (entry.Kind == DailyRewardKind.Gems && GemsAvailable) return Strings.Get(Strings.Key("reward.mark_gems"));
            // Digits, which belong to no language.
            return entry.Coins.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>True when this day hands out one free booster use.</summary>
        public static bool GrantsBooster(int day) { return Day(day).GrantsBooster; }

        /// <summary>The booster days, so a receipt can name them without walking the table.</summary>
        public static int[] BoosterDays { get { return (int[])ChestBoosterDay.Clone(); } }
    }
}
