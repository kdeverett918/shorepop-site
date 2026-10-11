using System;

namespace BloomHarbor
{
    /// <summary>The chest families (design/specs/meta-loop-r16.md, WP2). Append-only: the value is
    /// what a saved claim and an analytics row name. Album = 2 is fishing WP4's species chest.</summary>
    public enum ChestKind { Star = 0, Level = 1, Album = 2 }

    /// <summary>
    /// What one chest holds, before any cap is applied. Coins always land; the rest may not fit
    /// (a full ability stock, a full ticket pouch, a full booster credit), and whatever does not fit
    /// is paid as coins instead, so a chest never shows a prize it then withholds.
    /// </summary>
    public struct ChestContents
    {
        public ChestKind Kind;
        /// <summary>Zero-based index of this chest within its family, which picks its contents.</summary>
        public int Index;
        public int Coins;
        /// <summary>Ability units; the unlocked ability with the lowest stock receives them.</summary>
        public int AbilityUnits;
        public int FishingTickets;
        /// <summary>One free booster use of <see cref="Booster"/>, or none.</summary>
        public bool GrantsBooster;
        public BoosterType Booster;
        /// <summary>Gems, which only an album chest holds (fishing WP4, D-FISH-7): 0 for a Common or
        /// Uncommon fish, 5 Rare, 10 Epic, 20 Legendary.</summary>
        public int Gems;
    }

    /// <summary>
    /// The star chest and the level chest: when each is earned and what it holds. Plain,
    /// deterministic data. The star and level chests hold no gems (monetization section 4b allows
    /// gems in two places only); the album chest, a one-time prize per completed species, is the
    /// fishing spec's named exception (D-FISH-7), at 105 gems over the whole album
    /// (Core/Fishing/RewardChests.Album.cs). The progress the chests count - stars and cleared
    /// levels - is never spent, so an earned chest stays earned.
    /// </summary>
    public static partial class RewardChests
    {
        /// <summary>Stars per star chest.</summary>
        public const int StarsPerChest = 20;
        /// <summary>The first level chest is for this many cleared levels, then one per <see cref="LevelChestInterval"/>.</summary>
        public const int FirstLevelChest = 5, LevelChestInterval = 10;
        /// <summary>
        /// What a prize that does not fit is paid as instead. An ability unit is worth a little
        /// less than the cheapest booster; a ticket is the coin price of a cast's ticket in the
        /// fishing economy's own ratio; a booster use is its catalogue price's lower end.
        /// </summary>
        public const int AbilityOverflowCoins = 50, TicketOverflowCoins = 30, BoosterOverflowCoins = 60;

        /// <summary>Star chests a star total has earned.</summary>
        public static int StarChestsEarned(int totalStars) { return totalStars <= 0 ? 0 : totalStars / StarsPerChest; }

        /// <summary>Level chests a count of cleared levels has earned: at 5, 15, 25, ...</summary>
        public static int LevelChestsEarned(int clears)
        { return clears < FirstLevelChest ? 0 : 1 + (clears - FirstLevelChest) / LevelChestInterval; }

        /// <summary>The cleared-level count that earns level chest <paramref name="index"/> (zero-based).</summary>
        public static int LevelChestAt(int index) { return FirstLevelChest + LevelChestInterval * Math.Max(0, index); }

        /// <summary>The star total that earns star chest <paramref name="index"/> (zero-based).</summary>
        public static int StarChestAt(int index) { return StarsPerChest * (Math.Max(0, index) + 1); }

        /// <summary>
        /// A star chest's contents rotate through three: 120 coins and a Rocket, 150 coins with an
        /// ability unit and two fishing tickets, 200 coins and a Bomb.
        /// </summary>
        public static ChestContents StarChest(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");
            var chest = new ChestContents { Kind = ChestKind.Star, Index = index };
            switch (index % 3)
            {
                case 0: chest.Coins = 120; chest.GrantsBooster = true; chest.Booster = BoosterType.StartRocket; break;
                case 1: chest.Coins = 150; chest.AbilityUnits = 1; chest.FishingTickets = 2; break;
                default: chest.Coins = 200; chest.GrantsBooster = true; chest.Booster = BoosterType.StartBomb; break;
            }
            return chest;
        }

        /// <summary>A level chest: 150 coins, an ability unit and one booster use, the booster
        /// turning Rocket, Bomb, extra moves from one chest to the next.</summary>
        public static ChestContents LevelChest(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");
            var booster = index % 3 == 0 ? BoosterType.StartRocket : index % 3 == 1 ? BoosterType.StartBomb : BoosterType.ExtraMoves;
            return new ChestContents { Kind = ChestKind.Level, Index = index, Coins = 150, AbilityUnits = 1, GrantsBooster = true, Booster = booster };
        }
    }
}
