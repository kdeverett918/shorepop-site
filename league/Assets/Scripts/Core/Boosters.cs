using System;

namespace BloomHarbor
{
    /// <summary>
    /// The three pre-level boosters (design/specs/monetization.md section 4a, owner direction
    /// 2026-09-09). Each is bought with coins on the map's level plaque before PLAY and applied
    /// once, deterministically, as the board is created. A booster changes the starting board,
    /// never the rules: the score target, the star thresholds, the move bonus and the RNG stream
    /// are the level's own, and nothing here is influenced by spending after the board exists.
    /// </summary>
    public enum BoosterType { StartRocket, StartBomb, ExtraMoves }

    /// <summary>
    /// Prices and effects. These live in Core, without a Unity dependency, so the economy
    /// simulation and the map plaque read one number each and cannot drift apart.
    /// </summary>
    public static class BoosterCatalog
    {
        public const int Count = 3;

        /// <summary>Moves the +moves booster adds to the level's own budget.</summary>
        public const int ExtraMovesGranted = 3;

        // design/specs/monetization.md section 4a. Priced in whole clears, so a booster is always a
        // decision about one level rather than loose change. The unit is the smallest thing a level
        // can pay, a one-star first clear, which under Progression's constants is
        // FirstClearCoins + FirstClearBonusCoins + CoinsPerNewStar:
        //
        //     +3 moves            1 x a one-star clear
        //     Start with a Rocket 1.5 x
        //     Start with a Bomb   2.5 x
        //
        // The numbers are literals here and not a call into Progression, because Core is compiled
        // without Services by tests/core and a dependency the other way round would break it. The
        // derivation is asserted instead: tests/economy recomputes these three from Progression's
        // own constants and fails if a reward tuning pass moves the clear out from under them
        // (retuned 2026-09-09 after the first-clear and three-star bonuses raised a clear from
        // 35-55 coins to 60-105).
        private static readonly int[] Prices = { 90, 150, 60 };
        // The names a player reads, so they come from the catalogue rather than from this table.
        // The marks below stay literal: they name a shape the row draws, not a word anyone reads.
        private static readonly string[] NameKeys =
        {
            Strings.Key("booster.name_rocket"), Strings.Key("booster.name_bomb"), Strings.Key("booster.name_moves"),
        };
        private static readonly string[] Marks = { "rocket", "bomb", "moves" };

        public static bool IsDefined(BoosterType type) { return (int)type >= 0 && (int)type < Count; }

        public static int Price(BoosterType type)
        {
            if (!IsDefined(type)) throw new ArgumentOutOfRangeException("type", "Unknown booster.");
            return Prices[(int)type];
        }

        /// <summary>The words the plaque and the store show. Never abbreviated by the catalog.</summary>
        public static string Name(BoosterType type)
        {
            if (!IsDefined(type)) throw new ArgumentOutOfRangeException("type", "Unknown booster.");
            return Strings.Get(NameKeys[(int)type]);
        }

        /// <summary>The shape mark this booster draws, so the row reads without colour.</summary>
        public static string Mark(BoosterType type)
        {
            if (!IsDefined(type)) throw new ArgumentOutOfRangeException("type", "Unknown booster.");
            return Marks[(int)type];
        }

        /// <summary>Cost of every booster in one loadout. Zero for an empty loadout.</summary>
        public static int TotalPrice(BoosterLoadout loadout)
        {
            if (loadout == null) return 0;
            int total = 0;
            for (int i = 0; i < Count; i++) if (loadout.Has((BoosterType)i)) total += Prices[i];
            return total;
        }

        /// <summary>The dearest loadout, used as the bound on one attempt's booster spend.</summary>
        public static int MaximumLoadoutPrice
        {
            get { int total = 0; for (int i = 0; i < Count; i++) total += Prices[i]; return total; }
        }

        /// <summary>The most moves any loadout can add, so snapshot bounds can reserve for it.</summary>
        public const int MaximumBoosterMoves = ExtraMovesGranted;
    }

    /// <summary>
    /// The boosters chosen for one attempt, as a small bitmask. A loadout is part of the run
    /// definition: the same level seed and the same loadout produce the same board, so a replay
    /// used for league score validation reproduces a boosted run exactly (design/specs/ugs-project.md).
    /// </summary>
    [Serializable]
    public sealed class BoosterLoadout
    {
        /// <summary>Bit (1 &lt;&lt; (int)BoosterType) per chosen booster. Public for JsonUtility.</summary>
        public int Mask;

        public BoosterLoadout() { }
        public BoosterLoadout(int mask) { Mask = mask & AllMask; }

        public static readonly BoosterLoadout Empty = new BoosterLoadout();
        public const int AllMask = (1 << BoosterCatalog.Count) - 1;

        public bool IsEmpty { get { return (Mask & AllMask) == 0; } }
        public int ChosenCount
        {
            get { int n = 0; for (int i = 0; i < BoosterCatalog.Count; i++) if (Has((BoosterType)i)) n++; return n; }
        }

        public bool Has(BoosterType type)
        {
            return BoosterCatalog.IsDefined(type) && (Mask & (1 << (int)type)) != 0;
        }

        public void Set(BoosterType type, bool chosen)
        {
            if (!BoosterCatalog.IsDefined(type)) throw new ArgumentOutOfRangeException("type", "Unknown booster.");
            if (chosen) Mask |= 1 << (int)type; else Mask &= ~(1 << (int)type);
        }

        public void Toggle(BoosterType type) { Set(type, !Has(type)); }
        public void Clear() { Mask = 0; }
        public BoosterLoadout Clone() { return new BoosterLoadout(Mask); }

        /// <summary>Moves this loadout adds before the first tap.</summary>
        public int ExtraMoves { get { return Has(BoosterType.ExtraMoves) ? BoosterCatalog.ExtraMovesGranted : 0; } }

        /// <summary>Rejects a mask that carries a bit no booster owns, so a tampered save cannot
        /// smuggle an undefined effect through the snapshot.</summary>
        public static bool IsValidMask(int mask) { return mask >= 0 && (mask & ~AllMask) == 0; }
    }
}
